using System.Diagnostics;
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
    readonly ToolStripLabel _statusItem;
    readonly System.Windows.Forms.Timer _timer;
    readonly CancellationTokenSource _cts = new();
    readonly object _writeLock = new();
    StreamWriter? _writer;
    TaskCompletionSource<IpcMessage>? _pending;
    string? _pendingType;
    SettingsForm? _settings;
    readonly EventWaitHandle _openSettingsSignal;
    readonly Native.WinEventProc _foregroundProc; // kept in a field so the GC doesn't collect the callback
    readonly IntPtr _foregroundHook;
    readonly Notifier _notifier;
    bool _locked;
    string? _lastSent;
    DateTime _lastSentAt;

    public AgentContext()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var title = new ToolStripLabel("HA PC Bridge") { Image = AppIcon.Bitmap(16), Font = new Font("Segoe UI", 9f, FontStyle.Bold), Padding = new Padding(0, 4, 0, 2) };
        // A label, not a disabled menu item: Windows greys out the images of disabled items.
        _statusItem = new ToolStripLabel("Нет связи со службой") { Image = AppIcon.Dot(AppIcon.Off), ForeColor = SystemColors.GrayText, Padding = new Padding(0, 2, 0, 2), ImageScaling = ToolStripItemImageScaling.None };
        var settingsItem = new ToolStripMenuItem("Настройки…", null, async (_, _) => await OpenSettingsAsync());
        settingsItem.Font = new Font(settingsItem.Font, FontStyle.Bold);
        var about = new ToolStripMenuItem("О программе", null, (_, _) => MessageBox.Show(
            $"HA PC Bridge {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}\n\n" +
            "Состояние компьютера в Home Assistant через MQTT.\nhttps://github.com/muzdai/hapcbridge",
            "О программе", MessageBoxButtons.OK, MessageBoxIcon.Information));
        var menu = new ContextMenuStrip { ShowImageMargin = true, Font = new Font("Segoe UI", 9f) };
        menu.Items.AddRange([title, new ToolStripSeparator(), _statusItem, new ToolStripSeparator(), settingsItem, about]);

        // config.json is readable only by admins/SYSTEM (it holds the MQTT password);
        // the service pushes the tray setting over the pipe right after we connect.
        _tray = new NotifyIcon
        {
            Icon = AppIcon.Icon(),
            Text = "HA PC Bridge",
            ContextMenuStrip = menu,
            Visible = false,
        };
        _tray.DoubleClick += async (_, _) => await OpenSettingsAsync();
        _notifier = new Notifier(_tray, _ui, (tag, action) =>
            Write(new IpcMessage { Type = "notify_event", Payload = Notifier.EventJson(tag, action) }));

        // "HaPcBridge.Agent.exe --settings" from a second process (Start menu shortcut, icon hidden) opens the window here.
        _openSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.OpenSettingsEvent);
        ThreadPool.RegisterWaitForSingleObject(_openSettingsSignal,
            (_, _) => _ui.Post(async _ => await OpenSettingsAsync(), null), null, Timeout.Infinite, executeOnlyOnce: false);
        if (Environment.GetCommandLineArgs().Contains("--settings"))
            _ui.Post(async _ => { await Task.Delay(3000); await OpenSettingsAsync(); }, null);

        SystemEvents.SessionSwitch += OnSessionSwitch;

        // Window switches arrive as events (instant); the timer only catches title changes and keeps idle time fresh.
        _foregroundProc = (_, _, _, _, _, _, _) => SendStatus();
        _foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _foregroundProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

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
                if (!IsRealService(pipe)) { await Task.Delay(10000, ct); continue; }
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
                _ui.Post(_ => SetStatus("Нет связи со службой"), null);
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

    /// <summary>
    /// The real service runs in session 0, where no user program can run. Refuse anything else,
    /// so a program that grabbed our pipe name can't receive the MQTT password from the settings window.
    /// </summary>
    static bool IsRealService(NamedPipeClientStream pipe)
    {
        if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid)) return false;
        if (!Native.ProcessIdToSessionId(pid, out var serverSession)) return false;
        if (serverSession == 0) return true;
        // Debugging only: the service started by hand in a console. Has to be asked for explicitly with --dev.
        return Environment.GetCommandLineArgs().Contains("--dev");
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
                if (!string.IsNullOrEmpty(m.StatusText)) SetStatus(m.StatusText);
                break;

            case "notify":
                if (Notification.FromJson(m.Payload ?? "") is { } n) _notifier.Show(n);
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

    void SetStatus(string text)
    {
        _statusItem.Text = text;
        var color = text.StartsWith("Подключено", StringComparison.Ordinal) ? AppIcon.Ok
                  : text.StartsWith("Нет связи со службой", StringComparison.Ordinal) ? AppIcon.Off : AppIcon.Warn;
        _statusItem.Image = AppIcon.Dot(color);
        _tray.Text = Truncate("HA PC Bridge — " + text, 127);
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    protected override void ExitThreadCore()
    {
        _cts.Cancel();
        if (_foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(_foregroundHook);
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }
}
