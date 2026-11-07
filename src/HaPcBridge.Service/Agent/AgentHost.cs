using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using HaPcBridge.Common;
using HaPcBridge.Service.Native;
using Microsoft.Extensions.Logging;

namespace HaPcBridge.Service.Agent;

/// <summary>
/// Runs the named-pipe server for the user-session agent and makes sure an agent is running
/// in the console session (a Windows service in Session 0 can't see the desktop itself).
/// </summary>
internal sealed class AgentHost
{
    static readonly string AgentExe = Path.Combine(AppContext.BaseDirectory, "HaPcBridge.Agent.exe");

    readonly ILogger _log;
    readonly object _lock = new();
    StreamWriter? _writer;
    uint _connectedSession = Win32.INVALID_SESSION;
    DateTime _lastLaunchAttempt = DateTime.MinValue;

    public IpcMessage? LastStatus { get; private set; }
    public DateTime LastStatusAt { get; private set; }
    public bool Connected => _writer != null;

    /// <summary>Called when a fresh agent connects, so the service can push config to it.</summary>
    public event Action? AgentConnected;

    /// <summary>Handles request messages from the agent (settings window); the result is sent back.</summary>
    public Func<IpcMessage, IpcMessage?>? Request { get; set; }

    public AgentHost(ILogger log) => _log = log;

    public async Task RunAsync(CancellationToken ct)
    {
        _ = Task.Run(() => SupervisorLoop(ct), ct);

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = CreatePipe(); }
            catch (Exception ex) { _log.LogError(ex, "Cannot create agent pipe"); await Task.Delay(5000, ct); continue; }

            try
            {
                await pipe.WaitForConnectionAsync(ct);
                if (!IsTrustedClient(pipe, out var pid))
                {
                    _log.LogWarning("Rejected pipe client pid {Pid}: not our agent executable", pid);
                    pipe.Dispose();
                    continue;
                }
                await HandleClientAsync(pipe, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning("Agent connection ended: {Msg}", ex.Message); }
            finally
            {
                lock (_lock) { _writer = null; _connectedSession = Win32.INVALID_SESSION; }
                LastStatus = null;
                pipe.Dispose();
            }
        }
    }

    static NamedPipeServerStream CreatePipe()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(Ipc.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, sec);
    }

    /// <summary>Only our own HaPcBridge.Agent.exe (same folder as the service) may talk to us.</summary>
    static bool IsTrustedClient(NamedPipeServerStream pipe, out uint pid)
    {
        pid = 0;
        if (!Win32.GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out pid)) return false;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            var path = p.MainModule?.FileName;
            return path != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(AgentExe), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var reader = new StreamReader(pipe, new UTF8Encoding(false));
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            var msg = Ipc.Deserialize(line);
            if (msg == null) continue;

            switch (msg.Type)
            {
                case "hello":
                    lock (_lock) { _writer = writer; _connectedSession = (uint)(msg.SessionId ?? -1); }
                    _log.LogInformation("Agent connected: session {Session}, user {User}", msg.SessionId, msg.User);
                    AgentConnected?.Invoke();
                    break;
                case "status":
                    LastStatus = msg;
                    LastStatusAt = DateTime.UtcNow;
                    break;
                default:
                    IpcMessage? reply = null;
                    try { reply = Request?.Invoke(msg); }
                    catch (Exception ex) { reply = new IpcMessage { Type = "saved", Ok = false, Error = ex.Message }; }
                    if (reply != null)
                        lock (_lock) { try { writer.WriteLine(Ipc.Serialize(reply)); } catch { } }
                    break;
            }
        }
    }

    public bool Send(IpcMessage msg)
    {
        lock (_lock)
        {
            if (_writer == null) return false;
            try { _writer.WriteLine(Ipc.Serialize(msg)); return true; }
            catch (Exception ex) { _log.LogWarning("Send to agent failed: {Msg}", ex.Message); return false; }
        }
    }

    /// <summary>Starts the agent in the console session whenever a user is logged on and no agent is connected.</summary>
    async Task SupervisorLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var session = Win32.WTSGetActiveConsoleSessionId();
                var hasUser = session != Win32.INVALID_SESSION && session != 0
                              && !string.IsNullOrEmpty(Win32.QueryString(session, Win32.WTS_INFO_CLASS.WTSUserName));

                bool wrongSession;
                lock (_lock) wrongSession = _writer != null && _connectedSession != session;
                if (wrongSession)
                {
                    // Fast user switching: tell the old agent to quit; a new one is launched below.
                    Send(IpcMessage.Cmd(AgentCommands.Exit));
                }

                if (hasUser && !Connected && DateTime.UtcNow - _lastLaunchAttempt > TimeSpan.FromSeconds(20))
                {
                    _lastLaunchAttempt = DateTime.UtcNow;
                    Launch(session);
                }
            }
            catch (Exception ex) { _log.LogWarning("Agent supervisor: {Msg}", ex.Message); }

            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { break; }
        }
    }

    void Launch(uint session)
    {
        if (!File.Exists(AgentExe)) { _log.LogWarning("Agent not found at {Path}", AgentExe); return; }
        if (!IsLocalSystem()) { _log.LogDebug("Not running as LocalSystem (console mode?) — start HaPcBridge.Agent.exe yourself"); return; }

        if (!Win32.WTSQueryUserToken(session, out var token))
        {
            _log.LogDebug("WTSQueryUserToken({Session}) failed: {Err}", session, Marshal.GetLastWin32Error());
            return;
        }
        IntPtr env = IntPtr.Zero;
        try
        {
            Win32.CreateEnvironmentBlock(out env, token, false);
            var si = new Win32.STARTUPINFO { cb = Marshal.SizeOf<Win32.STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var ok = Win32.CreateProcessAsUserW(token, AgentExe, $"\"{AgentExe}\"", IntPtr.Zero, IntPtr.Zero, false,
                Win32.CREATE_UNICODE_ENVIRONMENT | Win32.CREATE_NO_WINDOW, env, AppContext.BaseDirectory, ref si, out var pi);
            if (ok)
            {
                _log.LogInformation("Started agent in session {Session} (pid {Pid})", session, pi.dwProcessId);
                Win32.CloseHandle(pi.hThread);
                Win32.CloseHandle(pi.hProcess);
            }
            else _log.LogWarning("CreateProcessAsUser failed: {Err}", Marshal.GetLastWin32Error());
        }
        finally
        {
            if (env != IntPtr.Zero) Win32.DestroyEnvironmentBlock(env);
            Win32.CloseHandle(token);
        }
    }

    static bool IsLocalSystem()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.IsSystem;
    }
}
