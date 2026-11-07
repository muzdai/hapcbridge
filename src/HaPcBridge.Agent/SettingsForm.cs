using System.Reflection;
using HaPcBridge.Common;

namespace HaPcBridge.Agent;

/// <summary>Settings window. Edits a copy of the config; the service validates and saves it.</summary>
internal sealed class SettingsForm : Form
{
    static readonly Dictionary<string, string> SensorNames = new()
    {
        [nameof(SensorToggles.CpuLoad)] = "Загрузка CPU",
        [nameof(SensorToggles.CpuTemp)] = "Температура CPU",
        [nameof(SensorToggles.CpuPower)] = "Мощность CPU",
        [nameof(SensorToggles.GpuLoad)] = "Загрузка GPU",
        [nameof(SensorToggles.GpuTemp)] = "Температура GPU",
        [nameof(SensorToggles.GpuPower)] = "Мощность GPU",
        [nameof(SensorToggles.GpuMemory)] = "Память GPU",
        [nameof(SensorToggles.RamFree)] = "Свободная RAM",
        [nameof(SensorToggles.RamUsage)] = "Загрузка RAM, %",
        [nameof(SensorToggles.TotalPower)] = "Потребление",
        [nameof(SensorToggles.LoggedUser)] = "Пользователь",
        [nameof(SensorToggles.LastLogin)] = "Время входа",
        [nameof(SensorToggles.LastBoot)] = "Время загрузки",
        [nameof(SensorToggles.ActiveWindow)] = "Активное окно",
        [nameof(SensorToggles.ActiveProcess)] = "Активный процесс",
        [nameof(SensorToggles.IdleTime)] = "Время простоя",
        [nameof(SensorToggles.Locked)] = "Блокировка",
    };

    static readonly Dictionary<string, string> CommandNames = new()
    {
        [nameof(CommandToggles.PowerSwitch)] = "Переключатель питания (WoL / выкл.)",
        [nameof(CommandToggles.Shutdown)] = "Выключение",
        [nameof(CommandToggles.Restart)] = "Перезагрузка",
        [nameof(CommandToggles.AbortShutdown)] = "Отмена выключения",
        [nameof(CommandToggles.Sleep)] = "Сон",
        [nameof(CommandToggles.Hibernate)] = "Гибернация",
        [nameof(CommandToggles.Lock)] = "Блокировка",
        [nameof(CommandToggles.Logoff)] = "Выход из системы",
        [nameof(CommandToggles.MonitorOff)] = "Выключить монитор",
        [nameof(CommandToggles.CloseActiveWindow)] = "Закрыть активное окно",
    };

    readonly BridgeConfig _cfg;
    readonly Func<BridgeConfig, string?, Task<(bool Ok, string? Error)>> _save;

    readonly TextBox _host = new(), _user = new(), _password = new() { UseSystemPasswordChar = true };
    readonly NumericUpDown _port = Num(1, 65535), _keepAlive = Num(5, 600);
    readonly CheckBox _tls = new() { Text = "TLS", AutoSize = true }, _untrusted = new() { Text = "Доверять самоподписанным сертификатам", AutoSize = true };
    readonly TextBox _deviceId = new(), _deviceName = new(), _baseTopic = new();

    readonly NumericUpDown _interval = Num(2, 3600), _delay = Num(0, 3600), _baseline = Num(0, 2000);
    readonly NumericUpDown _efficiency = Num(50, 100);
    readonly ComboBox _unit = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox _tray = new() { Text = "Значок в трее", AutoSize = true };
    readonly CheckBox _remote = new() { Text = "Разрешить менять настройки из Home Assistant", AutoSize = true };
    readonly CheckBox _discovery = new() { Text = "MQTT Discovery службы (без интеграции из HACS)", AutoSize = true };
    readonly TextBox _gpu = new() { PlaceholderText = "авто" }, _mac = new() { PlaceholderText = "авто" }, _broadcast = new();

    readonly CheckedListBox _sensors = new() { CheckOnClick = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
    readonly CheckedListBox _commands = new() { CheckOnClick = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
    readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    readonly Button _ok = new() { Text = "Сохранить", AutoSize = true };

    public SettingsForm(BridgeConfig cfg, bool hasPassword, string? serviceStatus,
        Func<BridgeConfig, string?, Task<(bool Ok, string? Error)>> save)
    {
        _cfg = cfg;
        _save = save;

        Text = "HA PC Bridge — настройки";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 480);
        Font = SystemFonts.MessageBoxFont ?? Font;
        ShowInTaskbar = true;
        TopMost = true;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("Подключение", BuildConnection(hasPassword)));
        tabs.TabPages.Add(Page("Общие", BuildGeneral()));
        tabs.TabPages.Add(Page("Датчики", _sensors));
        tabs.TabPages.Add(Page("Команды", _commands));

        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        _ok.Click += async (_, _) => await SaveAsync();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.AddRange([cancel, _ok]);
        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(10, 6, 10, 0) };
        statusBar.Controls.Add(_status);
        _status.Text = string.IsNullOrEmpty(serviceStatus) ? "" : "Служба: " + serviceStatus;

        Controls.Add(tabs);
        Controls.Add(statusBar);
        Controls.Add(buttons);
        AcceptButton = _ok;
        CancelButton = cancel;

        Fill();
    }

    // ------------------------------------------------------------------ layout

    Control BuildConnection(bool hasPassword)
    {
        _password.PlaceholderText = hasPassword ? "сохранён — оставьте пустым, чтобы не менять" : "";
        var t = Grid();
        Row(t, "MQTT-брокер", _host);
        Row(t, "Порт", _port);
        Row(t, "Пользователь", _user);
        Row(t, "Пароль", _password);
        Row(t, "", _tls);
        Row(t, "", _untrusted);
        Row(t, "Keep-alive, с", _keepAlive);
        Row(t, "ID устройства", _deviceId);
        Row(t, "Имя в HA", _deviceName);
        Row(t, "Базовый топик", _baseTopic);
        _tls.CheckedChanged += (_, _) => _untrusted.Enabled = _tls.Checked;
        return t;
    }

    Control BuildGeneral()
    {
        _unit.Items.AddRange(["°C", "°F"]);
        var t = Grid();
        Row(t, "Частота обновления, с", _interval);
        Row(t, "Температура", _unit);
        Row(t, "", _tray);
        Row(t, "Задержка выключения, с", _delay);
        Row(t, "Основная GPU (часть имени)", _gpu);
        Row(t, "Прочее железо, Вт", _baseline);
        Row(t, "КПД блока питания, %", _efficiency);
        Row(t, "MAC для WoL", _mac);
        Row(t, "Broadcast для WoL", _broadcast);
        Row(t, "", _remote);
        Row(t, "", _discovery);
        return t;
    }

    static TabPage Page(string title, Control content)
    {
        var p = new TabPage(title) { Padding = new Padding(10), UseVisualStyleBackColor = true };
        content.Dock = DockStyle.Fill;
        p.Controls.Add(content);
        return p;
    }

    static TableLayoutPanel Grid()
    {
        var t = new TableLayoutPanel { ColumnCount = 2, AutoScroll = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    static void Row(TableLayoutPanel t, string label, Control c)
    {
        var row = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 6, 0) }, 0, row);
        if (c is TextBox or ComboBox) c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        if (c is NumericUpDown) c.Width = 90;
        c.Margin = new Padding(0, 3, 0, 3);
        t.Controls.Add(c, 1, row);
    }

    static NumericUpDown Num(int min, int max) => new() { Minimum = min, Maximum = max, TextAlign = HorizontalAlignment.Right };

    // ------------------------------------------------------------------ data

    void Fill()
    {
        var m = _cfg.Mqtt;
        _host.Text = m.Host;
        _port.Value = Math.Clamp(m.Port, 1, 65535);
        _user.Text = m.Username;
        _tls.Checked = m.UseTls;
        _untrusted.Checked = m.AllowUntrustedCertificates;
        _untrusted.Enabled = m.UseTls;
        _keepAlive.Value = Math.Clamp(m.KeepAliveSeconds, 5, 600);
        _deviceId.Text = _cfg.DeviceId;
        _deviceName.Text = _cfg.DeviceName;
        _baseTopic.Text = _cfg.BaseTopic;

        _interval.Value = Math.Clamp(_cfg.UpdateIntervalSeconds, 2, 3600);
        _unit.SelectedIndex = _cfg.IsFahrenheit ? 1 : 0;
        _tray.Checked = _cfg.TrayIcon;
        _delay.Value = Math.Clamp(_cfg.Commands.ShutdownDelaySeconds, 0, 3600);
        _gpu.Text = _cfg.PrimaryGpu;
        _baseline.Value = (decimal)Math.Clamp(_cfg.Power.BaselineWatts, 0, 2000);
        _efficiency.Value = (decimal)Math.Clamp(Math.Round(_cfg.Power.PsuEfficiency * 100), 50, 100);
        _mac.Text = _cfg.Wol.Mac;
        _broadcast.Text = _cfg.Wol.BroadcastAddress;
        _remote.Checked = _cfg.AllowRemoteConfig;
        _discovery.Checked = _cfg.HaDiscovery;

        FillToggles(_sensors, _cfg.Sensors, SensorNames);
        FillToggles(_commands, _cfg.Commands, CommandNames);
    }

    static IEnumerable<PropertyInfo> Bools(object o) =>
        o.GetType().GetProperties().Where(p => p.PropertyType == typeof(bool) && p.CanWrite);

    static void FillToggles(CheckedListBox list, object toggles, Dictionary<string, string> names)
    {
        list.Items.Clear();
        foreach (var p in Bools(toggles))
            list.Items.Add(new Toggle(p.Name, names.GetValueOrDefault(p.Name, p.Name)), (bool)p.GetValue(toggles)!);
    }

    static void ReadToggles(CheckedListBox list, object toggles)
    {
        for (var i = 0; i < list.Items.Count; i++)
        {
            var t = (Toggle)list.Items[i];
            toggles.GetType().GetProperty(t.Key)!.SetValue(toggles, list.GetItemChecked(i));
        }
    }

    sealed record Toggle(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_host.Text)) { Fail("Укажите адрес MQTT-брокера"); return; }
        var mac = _mac.Text.Trim();
        if (mac.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(mac, "^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$"))
        { Fail("MAC в формате AA:BB:CC:DD:EE:FF"); return; }

        var m = _cfg.Mqtt;
        m.Host = _host.Text.Trim();
        m.Port = (int)_port.Value;
        m.Username = _user.Text.Trim();
        m.UseTls = _tls.Checked;
        m.AllowUntrustedCertificates = _untrusted.Checked;
        m.KeepAliveSeconds = (int)_keepAlive.Value;
        _cfg.DeviceId = _deviceId.Text.Trim();
        _cfg.DeviceName = _deviceName.Text.Trim();
        _cfg.BaseTopic = string.IsNullOrWhiteSpace(_baseTopic.Text) ? "hapcbridge" : _baseTopic.Text.Trim();

        _cfg.UpdateIntervalSeconds = (int)_interval.Value;
        _cfg.TemperatureUnit = _unit.SelectedIndex == 1 ? "F" : "C";
        _cfg.TrayIcon = _tray.Checked;
        _cfg.Commands.ShutdownDelaySeconds = (int)_delay.Value;
        _cfg.PrimaryGpu = _gpu.Text.Trim();
        _cfg.Power.BaselineWatts = (double)_baseline.Value;
        _cfg.Power.PsuEfficiency = (double)_efficiency.Value / 100.0;
        _cfg.Wol.Mac = mac.ToUpperInvariant().Replace('-', ':');
        _cfg.Wol.BroadcastAddress = string.IsNullOrWhiteSpace(_broadcast.Text) ? "255.255.255.255" : _broadcast.Text.Trim();
        _cfg.AllowRemoteConfig = _remote.Checked;
        _cfg.HaDiscovery = _discovery.Checked;
        ReadToggles(_sensors, _cfg.Sensors);
        ReadToggles(_commands, _cfg.Commands);

        _ok.Enabled = false;
        _status.ForeColor = SystemColors.GrayText;
        _status.Text = "Сохраняю…";
        var (ok, error) = await _save(_cfg, string.IsNullOrEmpty(_password.Text) ? null : _password.Text);
        _ok.Enabled = true;
        if (ok) { DialogResult = DialogResult.OK; Close(); }
        else Fail(error ?? "Служба не ответила");
    }

    void Fail(string text)
    {
        _status.ForeColor = Color.Firebrick;
        _status.Text = text;
    }
}
