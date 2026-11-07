using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO.Pipes;
using System.Text;
using HaPcBridge.Common;
using Microsoft.Win32;

namespace HaPcBridge.Agent;

/// <summary>
/// Lives in the user's session: reads what the service can't see from Session 0
/// (foreground window, idle time, lock state), executes desktop commands and shows the tray icon.
/// </summary>
internal sealed class AgentContext : ApplicationContext
{
    static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow"];

    readonly SynchronizationContext _ui;
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _statusItem;
    readonly System.Windows.Forms.Timer _timer;
    readonly CancellationTokenSource _cts = new();
    readonly object _writeLock = new();
    StreamWriter? _writer;
    TaskCompletionSource<IpcMessage>? _pending;
    string? _pendingType;
    SettingsForm? _settings;
    readonly EventWaitHandle _openSettingsSignal;
    bool _locked;
    string? _lastSent;
    DateTime _lastSentAt;

    public AgentContext()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _statusItem = new ToolStripMenuItem("Нет связи со службой") { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        var settingsItem = new ToolStripMenuItem("Настройки…", null, async (_, _) => await OpenSettingsAsync());
        settingsItem.Font = new Font(settingsItem.Font, FontStyle.Bold);
        menu.Items.Add(settingsItem);

        // config.json is readable only by admins/SYSTEM (it holds the MQTT password);
        // the service pushes the tray setting over the pipe right after we connect.
        _tray = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "HA PC Bridge",
            ContextMenuStrip = menu,
            Visible = false,
        };
        _tray.DoubleClick += async (_, _) => await OpenSettingsAsync();

        // "HaPcBridge.Agent.exe --settings" from a second process (Start menu shortcut, icon hidden) opens the window here.
        _openSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.OpenSettingsEvent);
        ThreadPool.RegisterWaitForSingleObject(_openSettingsSignal,
            (_, _) => _ui.Post(async _ => await OpenSettingsAsync(), null), null, Timeout.Infinite, executeOnlyOnce: false);
        if (Environment.GetCommandLineArgs().Contains("--settings"))
            _ui.Post(async _ => { await Task.Delay(3000); await OpenSettingsAsync(); }, null);

        SystemEvents.SessionSwitch += OnSessionSwitch;

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => SendStatus();
        _timer.Start();

        _ = Task.Run(() => PipeLoop(_cts.Token));
    }

    // ---------------- pipe ----------------

    async Task PipeLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", Ipc.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000, ct);
                var reader = new StreamReader(pipe, new UTF8Encoding(false));
                var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

                Native.ProcessIdToSessionId((uint)Environment.ProcessId, out var session);
                lock (_writeLock) _writer = writer;
                Write(new IpcMessage { Type = "hello", SessionId = (int)session, User = Environment.UserName });
                _lastSent = null;
                _ui.Post(_ => SendStatus(), null);

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break;
                    var msg = Ipc.Deserialize(line);
                    if (msg == null) continue;
                    var pending = _pending;
                    if (pending != null && msg.Type == _pendingType) { pending.TrySetResult(msg); continue; }
                    _ui.Post(_ => Handle(msg), null);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { /* service not running yet / restarted */ }
            finally
            {
                lock (_writeLock) _writer = null;
                _ui.Post(_ => _statusItem.Text = "Нет связи со службой", null);
            }

            try { await Task.Delay(3000, ct); } catch { break; }
        }
    }

    /// <summary>Sends a request and waits for the reply of the given type (one request at a time).</summary>
    async Task<IpcMessage?> RequestAsync(IpcMessage m, string replyType, int timeoutMs = 5000)
    {
        var tcs = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingType = replyType;
        _pending = tcs;
        try
        {
            lock (_writeLock) if (_writer == null) return null;
            Write(m);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            return done == tcs.Task ? tcs.Task.Result : null;
        }
        finally { _pending = null; _pendingType = null; }
    }

    async Task OpenSettingsAsync()
    {
        if (_settings is { IsDisposed: false }) { _settings.Activate(); return; }

        var reply = await RequestAsync(new IpcMessage { Type = "get_config" }, "full_config");
        if (reply?.ConfigJson is not { } json)
        {
            MessageBox.Show("Служба HA PC Bridge не отвечает. Проверьте, что она запущена (services.msc → HA PC Bridge).",
                "HA PC Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var cfg = System.Text.Json.JsonSerializer.Deserialize<BridgeConfig>(json, ConfigStore.JsonOptions) ?? new BridgeConfig();

        _settings = new SettingsForm(cfg, reply.HasPassword == true, reply.StatusText, async (edited, password) =>
        {
            var r = await RequestAsync(new IpcMessage
            {
                Type = "set_config",
                ConfigJson = System.Text.Json.JsonSerializer.Serialize(edited, ConfigStore.JsonOptions),
                Password = password,
            }, "saved");
            return r == null ? (false, "Служба не ответила") : (r.Ok == true, r.Error);
        });
        _settings.FormClosed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    void Write(IpcMessage m)
    {
        lock (_writeLock)
        {
            if (_writer == null) return;
            try { _writer.WriteLine(Ipc.Serialize(m)); }
            catch { _writer = null; }
        }
    }

    void Handle(IpcMessage m)
    {
        switch (m.Type)
        {
            case "config":
                if (m.TrayIcon is bool show) _tray.Visible = show;
                if (!string.IsNullOrEmpty(m.StatusText))
                {
                    _statusItem.Text = m.StatusText;
                    _tray.Text = Truncate("HA PC Bridge — " + m.StatusText, 127);
                }
                break;

            case "command":
                switch (m.Command)
                {
                    case AgentCommands.CloseActiveWindow: CloseActiveWindow(); break;
                    case AgentCommands.Lock: Native.LockWorkStation(); break;
                    case AgentCommands.MonitorOff:
                        Native.PostMessageW(Native.HWND_BROADCAST, Native.WM_SYSCOMMAND, Native.SC_MONITORPOWER, new IntPtr(2));
                        break;
                    case AgentCommands.Exit: ExitThread(); break;
                }
                break;
        }
    }

    // ---------------- status ----------------

    void SendStatus()
    {
        var (title, process) = _locked ? (null, null) : Foreground();
        var idle = Math.Round(Native.IdleSeconds());
        var msg = new IpcMessage
        {
            Type = "status",
            ActiveWindow = title,
            ActiveProcess = process,
            IdleSeconds = idle,
            Locked = _locked,
        };

        // Send on change of window/lock, and at least every 10 s so idle time stays fresh.
        var key = $"{title}|{process}|{_locked}";
        if (key == _lastSent && DateTime.UtcNow - _lastSentAt < TimeSpan.FromSeconds(10)) return;
        _lastSent = key;
        _lastSentAt = DateTime.UtcNow;
        Write(msg);
    }

    static (string? Title, string? Process) Foreground()
    {
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero || ShellClasses.Contains(Native.ClassName(h))) return (null, null);

        var title = Native.WindowTitle(h);
        string? proc = null;
        Native.GetWindowThreadProcessId(h, out var pid);
        try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { }
        return (string.IsNullOrWhiteSpace(title) ? proc : title, proc);
    }

    static void CloseActiveWindow()
    {
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero || ShellClasses.Contains(Native.ClassName(h))) return;
        // WM_CLOSE = same as clicking ✕: the app may still ask to save. No process is killed.
        Native.PostMessageW(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        _locked = e.Reason switch
        {
            SessionSwitchReason.SessionLock => true,
            SessionSwitchReason.SessionUnlock => false,
            _ => _locked,
        };
        _lastSent = null;
        SendStatus();
    }

    // ---------------- tray ----------------

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var body = new SolidBrush(Color.FromArgb(3, 169, 244)); // HA blue
            g.FillRectangle(body, 3, 4, 26, 18);
            using var screen = new SolidBrush(Color.FromArgb(225, 245, 254));
            g.FillRectangle(screen, 6, 7, 20, 12);
            g.FillRectangle(body, 13, 22, 6, 4);
            g.FillRectangle(body, 8, 26, 16, 3);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    protected override void ExitThreadCore()
    {
        _cts.Cancel();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }
}
