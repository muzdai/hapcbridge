using System.Security;
using System.Text;
using System.Text.Json;
using HaPcBridge.Common;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Notification = HaPcBridge.Common.Notification;

namespace HaPcBridge.Agent;

/// <summary>
/// Shows Home Assistant notifications as Windows toasts (with buttons) and reports what the user did.
/// Falls back to a tray balloon if toasts are unavailable.
/// </summary>
internal sealed class Notifier
{
    const string AppId = "muzdai.HaPcBridge";

    readonly NotifyIcon _tray;
    readonly SynchronizationContext _ui;
    readonly Action<string, string> _report; // (tag, action)
    readonly List<ToastNotification> _alive = new(); // keeps event handlers reachable
    bool _registered;
    string? _balloonTag;

    public Notifier(NotifyIcon tray, SynchronizationContext ui, Action<string, string> report)
    {
        _tray = tray;
        _ui = ui;
        _report = report;
        _tray.BalloonTipClicked += (_, _) => { if (_balloonTag != null) _report(_balloonTag, "clicked"); };
    }

    public void Show(Notification n)
    {
        try { ShowToast(n); }
        catch { ShowBalloon(n); }
    }

    /// <summary>
    /// An app without an installer package needs its id registered once per user,
    /// otherwise Windows silently drops its toasts.
    /// </summary>
    void Register()
    {
        if (_registered) return;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HaPcBridge");
        Directory.CreateDirectory(dir);
        var icon = Path.Combine(dir, "icon.png");
        if (!File.Exists(icon))
            using (var bmp = AppIcon.Bitmap(64)) bmp.Save(icon, System.Drawing.Imaging.ImageFormat.Png);

        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}");
        key.SetValue("DisplayName", "HA PC Bridge");
        key.SetValue("IconUri", icon);
        _registered = true;
    }

    void ShowToast(Notification n)
    {
        Register();

        var xml = new StringBuilder();
        xml.Append($"<toast launch=\"clicked\" duration=\"{(n.Long ? "long" : "short")}\"><visual><binding template=\"ToastGeneric\">");
        if (n.Title.Length > 0) xml.Append($"<text>{Esc(n.Title)}</text>");
        if (n.Message.Length > 0) xml.Append($"<text>{Esc(n.Message)}</text>");
        xml.Append("</binding></visual>");
        if (n.Actions.Count > 0)
        {
            xml.Append("<actions>");
            foreach (var a in n.Actions)
                xml.Append($"<action content=\"{Esc(a.Title)}\" arguments=\"action:{Esc(a.Id)}\"/>");
            xml.Append("</actions>");
        }
        if (n.Silent) xml.Append("<audio silent=\"true\"/>");
        xml.Append("</toast>");

        var doc = new XmlDocument();
        doc.LoadXml(xml.ToString());
        var toast = new ToastNotification(doc) { Group = "ha" };
        if (n.Tag.Length > 0) toast.Tag = n.Tag;

        var tag = n.Tag;
        toast.Activated += (_, e) =>
        {
            var args = (e as ToastActivatedEventArgs)?.Arguments ?? "";
            Done(toast, tag, args.StartsWith("action:", StringComparison.Ordinal) ? args[7..] : "clicked");
        };
        toast.Dismissed += (_, e) =>
        {
            // Timeouts just move the toast to the notification centre; only report an explicit close.
            if (e.Reason == ToastDismissalReason.UserCanceled) Done(toast, tag, "dismissed");
        };
        toast.Failed += (_, _) => _ui.Post(_ => { _alive.Remove(toast); ShowBalloon(n); }, null);

        lock (_alive)
        {
            if (_alive.Count > 50) _alive.RemoveRange(0, 25);
            _alive.Add(toast);
        }
        ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
    }

    void Done(ToastNotification toast, string tag, string action)
    {
        lock (_alive) _alive.Remove(toast);
        _ui.Post(_ => _report(tag, action), null); // toast events arrive on a thread-pool thread
    }

    void ShowBalloon(Notification n)
    {
        // Balloons need a visible tray icon; show it for the moment if the user hid it.
        var wasVisible = _tray.Visible;
        _tray.Visible = true;
        _balloonTag = n.Tag;
        var title = n.Title.Length > 0 ? n.Title : "Home Assistant";
        var text = n.Message.Length > 0 ? n.Message : " ";
        _tray.ShowBalloonTip(n.Long ? 25000 : 7000, title, text, ToolTipIcon.None);
        if (!wasVisible)
        {
            var t = new System.Windows.Forms.Timer { Interval = 30000 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); _tray.Visible = false; };
            t.Start();
        }
    }

    static string Esc(string s) => SecurityElement.Escape(s) ?? "";

    public static string EventJson(string tag, string action) =>
        JsonSerializer.Serialize(new { tag, action });
}
