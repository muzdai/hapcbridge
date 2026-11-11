using System.Reflection;
using System.Text.RegularExpressions;
using HaPcBridge.Common;

namespace HaPcBridge.Agent;

/// <summary>Settings window. Edits a copy of the config; the service validates and saves it.</summary>
internal sealed class SettingsForm : Form
{
    sealed record Item(string Key, string Label, string? Hint = null);
    sealed record Section(string Title, Item[] Items);

    static readonly Section[] SensorSections =
    [
        new("Процессор", [new("CpuLoad", "Загрузка, %"), new("CpuTemp", "Температура"), new("CpuPower", "Мощность, Вт")]),
        new("Видеокарта", [new("GpuLoad", "Загрузка, %"), new("GpuTemp", "Температура"), new("GpuPower", "Мощность, Вт"), new("GpuMemory", "Видеопамять, ГБ")]),
        new("Память и питание", [new("RamFree", "Свободная RAM, ГБ"), new("RamUsage", "Загрузка RAM, %"), new("TotalPower", "Потребление ПК, Вт")]),
        new("Пользователь", [new("LoggedUser", "Кто вошёл"), new("LastLogin", "Время входа"), new("LastBoot", "Время загрузки"), new("Locked", "Блокировка"), new("IdleTime", "Время простоя")]),
        new("Окна", [new("ActiveWindow", "Активное окно"), new("ActiveProcess", "Активный процесс")]),
    ];

    static readonly Section[] CommandSections =
    [
        new("Питание", [new("PowerSwitch", "Вкл. / выкл. (WoL)"), new("Shutdown", "Выключение"), new("Restart", "Перезагрузка"),
                        new("AbortShutdown", "Отмена выключения"), new("Sleep", "Сон"), new("Hibernate", "Гибернация")]),
        new("Сеанс и окна", [new("Lock", "Блокировка"), new("Logoff", "Выход из системы"), new("MonitorOff", "Выключить монитор"),
                             new("CloseActiveWindow", "Закрыть активное окно")]),
    ];

    readonly BridgeConfig _cfg;
    readonly Func<BridgeConfig, string?, Task<(bool Ok, string? Error)>> _save;

    // connection
    readonly TextBox _host = Input(), _user = Input(), _password = Input(), _deviceId = Input(), _deviceName = Input(), _baseTopic = Input();
    readonly NumericUpDown _port = Num(1, 65535, 80), _keepAlive = Num(5, 600);
    readonly CheckBox _tls = Check("Шифрование TLS"), _untrusted = Check("Доверять самоподписанному сертификату");

    // general
    readonly NumericUpDown _interval = Num(2, 3600), _baseline = Num(0, 2000), _efficiency = Num(50, 100), _delay = Num(0, 3600);
    readonly CheckBox _tray = Check("Значок в трее");
    readonly CheckBox _remote = Check("Разрешить менять настройки из Home Assistant");
    readonly CheckBox _discovery = Check("Собственное MQTT Discovery (без интеграции из HACS)");
    readonly TextBox _gpu = Input("авто"), _mac = Input("авто"), _broadcast = Input();
    readonly RadioButton _celsius = new() { Text = "Цельсий, °C", AutoSize = true }, _fahrenheit = new() { Text = "Фаренгейт, °F", AutoSize = true };

    // sensors & commands
    readonly Dictionary<string, CheckBox> _sensorBoxes = new(), _commandBoxes = new();

    readonly Label _footer = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 8, 0, 0) };
    readonly Button _ok = new() { Text = "Сохранить", MinimumSize = new Size(110, 30), AutoSize = true };
    readonly ErrorProvider _errors = new() { BlinkStyle = ErrorBlinkStyle.NeverBlink };

    public SettingsForm(BridgeConfig cfg, bool hasPassword, string? serviceStatus,
        Func<BridgeConfig, string?, Task<(bool Ok, string? Error)>> save)
    {
        _cfg = cfg;
        _save = save;

        Text = "HA PC Bridge — настройки";
        Icon = AppIcon.Icon();
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 690);
        ShowInTaskbar = true;
        _password.UseSystemPasswordChar = true;
        _password.PlaceholderText = hasPassword ? "сохранён — пусто = не менять" : "";
        _errors.SetIconPadding(_mac, 4);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(16, 6) };
        tabs.TabPages.Add(Page("Подключение", BuildConnection()));
        tabs.TabPages.Add(Page("Датчики и команды", BuildData()));
        tabs.TabPages.Add(Page("Дополнительно", BuildGeneral()));

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 0) };
        body.Controls.Add(tabs);

        Controls.Add(body);
        Controls.Add(Header(serviceStatus));
        Controls.Add(Footer());
        AcceptButton = _ok;

        _ok.Click += async (_, _) => await SaveAsync();
        _tls.CheckedChanged += (_, _) => _untrusted.Enabled = _tls.Checked;
        _mac.TextChanged += (_, _) => _errors.SetError(_mac, "");
        _host.TextChanged += (_, _) => _errors.SetError(_host, "");

        Fill();
    }

    // ================================================================== layout

    Control Header(string? serviceStatus)
    {
        var connected = serviceStatus?.StartsWith("Подключено", StringComparison.Ordinal) == true;
        var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = SystemColors.Window };
        var icon = new PictureBox { Image = AppIcon.Bitmap(40), Size = new Size(40, 40), Location = new Point(16, 12), SizeMode = PictureBoxSizeMode.Zoom };
        var title = new Label { Text = "HA PC Bridge", AutoSize = true, Location = new Point(66, 11), Font = new Font(Font.FontFamily, 13f, FontStyle.Bold) };
        var dot = new PictureBox
        {
            Image = AppIcon.Dot(serviceStatus == null ? AppIcon.Off : connected ? AppIcon.Ok : AppIcon.Warn, 10, 9),
            Size = new Size(10, 10), Location = new Point(68, 42),
        };
        var status = new Label
        {
            Text = serviceStatus ?? "Нет данных о подключении",
            AutoSize = true, Location = new Point(82, 38), ForeColor = SystemColors.GrayText,
        };
        var version = new Label
        {
            Text = $"{Environment.MachineName}  ·  v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}",
            AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        header.Controls.AddRange([icon, title, dot, status, version]);
        header.Layout += (_, _) => version.Location = new Point(header.ClientSize.Width - version.Width - 16, 15);
        var line = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = SystemColors.ControlLight };
        header.Controls.Add(line);
        return header;
    }

    Control Footer()
    {
        var cancel = new Button { Text = "Отмена", MinimumSize = new Size(96, 30), AutoSize = true, DialogResult = DialogResult.Cancel };
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Right, WrapContents = false };
        buttons.Controls.AddRange([cancel, _ok]);
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(16, 10, 12, 10) };
        footer.Controls.Add(buttons);
        footer.Controls.Add(_footer);
        _footer.Location = new Point(16, 18);
        return footer;
    }

    Control BuildConnection()
    {
        var broker = Fields();
        Field(broker, "Адрес", _host);
        Field(broker, "Порт", _port);
        Field(broker, "Пользователь", _user);
        Field(broker, "Пароль", _password);
        Field(broker, "", _tls);
        Field(broker, "", _untrusted);
        Field(broker, "Keep-alive, с", _keepAlive);

        var device = Fields();
        Field(device, "ID устройства", _deviceId, "Латиница, цифры и _. Часть MQTT-топика и id сущностей в HA.");
        Field(device, "Имя в Home Assistant", _deviceName);
        Field(device, "Базовый топик", _baseTopic);

        var grid = Grid(1);
        AddRow(grid, Group("MQTT-брокер", broker));
        AddRow(grid, Group("Устройство", device));
        return grid;
    }

    Control BuildData()
    {
        var grid = Grid(3);

        AddRow(grid, Caption("Датчики", "что отправлять в Home Assistant", _sensorBoxes));
        var groups = SensorSections.Select(s => Group(s.Title, Checks(s.Items, _sensorBoxes))).ToList();
        groups.Add(Group("Температура", Stack(_celsius, _fahrenheit)));
        AddRow(grid, groups[0], groups[1], groups[2]);
        AddRow(grid, groups[3], groups[4], groups[5]);

        AddRow(grid, Caption("Команды", "что Home Assistant может выполнять на ПК", _commandBoxes));
        var delayRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        delayRow.Controls.AddRange([_delay, new Label { Text = "с", AutoSize = true, Margin = new Padding(4, 6, 0, 0) }]);
        var delayBox = Stack(
            new Label { Text = "Перед выключением и перезагрузкой Windows покажет предупреждение.", AutoSize = true, MaximumSize = new Size(190, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 6) },
            delayRow);
        AddRow(grid,
            Group(CommandSections[0].Title, Checks(CommandSections[0].Items, _commandBoxes)),
            Group(CommandSections[1].Title, Checks(CommandSections[1].Items, _commandBoxes)),
            Group("Задержка выключения", delayBox));
        return grid;
    }

    Control BuildGeneral()
    {
        var update = Fields();
        Field(update, "Частота обновления, с", _interval, "как часто отправлять датчики в HA");
        Field(update, "", _tray);

        var power = Fields();
        Field(power, "Прочее железо, Вт", _baseline, "плата, диски, вентиляторы — всё, что не видят датчики");
        Field(power, "КПД блока питания, %", _efficiency, "обычно 87–92");
        Field(power, "Основная видеокарта", _gpu, "Часть имени, например 4080. Пусто — выбрать самой.");

        var wol = Fields();
        Field(wol, "MAC-адрес", _mac, "Пусто — MAC сетевой карты с основным маршрутом.");
        Field(wol, "Broadcast", _broadcast);

        var ha = Stack(_remote, _discovery);

        var grid = Grid(1);
        AddRow(grid, Group("Обновление", update));
        AddRow(grid, Group("Расчёт потребления", power));
        AddRow(grid, Group("Wake-on-LAN", wol));
        AddRow(grid, Group("Home Assistant", ha));
        return grid;
    }

    // ================================================================== building blocks

    static TabPage Page(string title, Control content)
    {
        var p = new TabPage(title) { Padding = new Padding(8), UseVisualStyleBackColor = true, AutoScroll = true };
        content.Dock = DockStyle.Top;
        p.Controls.Add(content);
        return p;
    }

    static TableLayoutPanel Grid(int columns)
    {
        var t = new TableLayoutPanel { ColumnCount = columns, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
        for (var i = 0; i < columns; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        return t;
    }

    static void AddRow(TableLayoutPanel t, params Control[] cells)
    {
        var row = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (cells.Length == 1)
        {
            t.Controls.Add(cells[0], 0, row);
            t.SetColumnSpan(cells[0], t.ColumnCount);
            return;
        }
        for (var i = 0; i < cells.Length; i++) t.Controls.Add(cells[i], i, row);
    }

    static GroupBox Group(string title, Control content)
    {
        var g = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 6, 10, 8),
            Margin = new Padding(4, 2, 4, 6),
        };
        content.Dock = DockStyle.Fill;
        g.Controls.Add(content);
        return g;
    }

    static FlowLayoutPanel Stack(params Control[] controls)
    {
        var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        foreach (var c in controls) c.Margin = new Padding(0, 2, 0, 2);
        f.Controls.AddRange(controls);
        return f;
    }

    static FlowLayoutPanel Checks(Item[] items, Dictionary<string, CheckBox> store)
    {
        var boxes = items.Select(i => store[i.Key] = Check(i.Label)).ToArray<Control>();
        return Stack(boxes);
    }

    /// <summary>Section title with a muted subtitle and "все / ничего" links.</summary>
    static Control Caption(string title, string subtitle, Dictionary<string, CheckBox> boxes)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(4, 8, 4, 2), Dock = DockStyle.Fill };
        var t = new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Margin = new Padding(0, 0, 8, 0) };
        var s = new Label { Text = "— " + subtitle, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 16, 0) };
        LinkLabel Link(string text, bool value)
        {
            var l = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 4, 8, 0), LinkBehavior = LinkBehavior.HoverUnderline };
            l.LinkClicked += (_, _) => { foreach (var b in boxes.Values) b.Checked = value; };
            return l;
        }
        p.Controls.AddRange([t, s, Link("все", true), Link("ничего", false)]);
        return p;
    }

    static TableLayoutPanel Fields(int labelWidth = 170)
    {
        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    void Field(TableLayoutPanel t, string label, Control c, string? hint = null)
    {
        var row = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (label.Length > 0)
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 8, 0) }, 0, row);
        if (c is TextBox) { c.Anchor = AnchorStyles.Left | AnchorStyles.Right; c.MinimumSize = new Size(160, 0); }
        c.Margin = new Padding(0, 4, 18, 4);

        // Short numeric fields carry their hint on the same line.
        if (c is NumericUpDown && hint != null)
        {
            var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            c.Margin = new Padding(0, 4, 10, 4);
            line.Controls.AddRange([c, new Label { Text = hint, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 7, 0, 0) }]);
            c = line;
            hint = null;
        }

        t.Controls.Add(c, 1, row);
        if (label.Length == 0) { t.SetColumn(c, 0); t.SetColumnSpan(c, 2); }
        if (hint != null)
        {
            var r2 = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var h = new Label { Text = hint, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 4), MaximumSize = new Size(470, 0) };
            t.Controls.Add(h, 1, r2);
        }
    }

    static TextBox Input(string? placeholder = null) => new() { PlaceholderText = placeholder ?? "" };
    static CheckBox Check(string text) => new() { Text = text, AutoSize = true };
    static NumericUpDown Num(int min, int max, int width = 72) =>
        new() { Minimum = min, Maximum = max, Width = width, TextAlign = HorizontalAlignment.Right };

    // ================================================================== data

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
        (_cfg.IsFahrenheit ? _fahrenheit : _celsius).Checked = true;
        _tray.Checked = _cfg.TrayIcon;
        _delay.Value = Math.Clamp(_cfg.Commands.ShutdownDelaySeconds, 0, 3600);
        _gpu.Text = _cfg.PrimaryGpu;
        _baseline.Value = (decimal)Math.Clamp(_cfg.Power.BaselineWatts, 0, 2000);
        _efficiency.Value = (decimal)Math.Clamp(Math.Round(_cfg.Power.PsuEfficiency * 100), 50, 100);
        _mac.Text = _cfg.Wol.Mac;
        _broadcast.Text = _cfg.Wol.BroadcastAddress;
        _remote.Checked = _cfg.AllowRemoteConfig;
        _discovery.Checked = _cfg.HaDiscovery;

        LoadToggles(_sensorBoxes, _cfg.Sensors);
        LoadToggles(_commandBoxes, _cfg.Commands);
    }

    static void LoadToggles(Dictionary<string, CheckBox> boxes, object toggles)
    {
        foreach (var (key, box) in boxes)
            box.Checked = (bool)toggles.GetType().GetProperty(key)!.GetValue(toggles)!;
    }

    static void StoreToggles(Dictionary<string, CheckBox> boxes, object toggles)
    {
        foreach (var (key, box) in boxes)
            toggles.GetType().GetProperty(key)!.SetValue(toggles, box.Checked);
    }

    async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_host.Text)) { Invalid(_host, "Укажите адрес MQTT-брокера", 0); return; }
        var mac = _mac.Text.Trim();
        if (mac.Length > 0 && !Regex.IsMatch(mac, "^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$"))
        { Invalid(_mac, "MAC в формате AA:BB:CC:DD:EE:FF", 2); return; }

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
        _cfg.TemperatureUnit = _fahrenheit.Checked ? "F" : "C";
        _cfg.TrayIcon = _tray.Checked;
        _cfg.Commands.ShutdownDelaySeconds = (int)_delay.Value;
        _cfg.PrimaryGpu = _gpu.Text.Trim();
        _cfg.Power.BaselineWatts = (double)_baseline.Value;
        _cfg.Power.PsuEfficiency = (double)_efficiency.Value / 100.0;
        _cfg.Wol.Mac = mac.ToUpperInvariant().Replace('-', ':');
        _cfg.Wol.BroadcastAddress = string.IsNullOrWhiteSpace(_broadcast.Text) ? "255.255.255.255" : _broadcast.Text.Trim();
        _cfg.AllowRemoteConfig = _remote.Checked;
        _cfg.HaDiscovery = _discovery.Checked;
        StoreToggles(_sensorBoxes, _cfg.Sensors);
        StoreToggles(_commandBoxes, _cfg.Commands);

        _ok.Enabled = false;
        SetFooter("Сохраняю…", SystemColors.GrayText);
        var (ok, error) = await _save(_cfg, string.IsNullOrEmpty(_password.Text) ? null : _password.Text);
        _ok.Enabled = true;
        if (ok) { DialogResult = DialogResult.OK; Close(); }
        else SetFooter(error ?? "Служба не ответила", Color.Firebrick);
    }

    void Invalid(Control c, string text, int tab)
    {
        if (Controls.OfType<Panel>().SelectMany(p => p.Controls.OfType<TabControl>()).FirstOrDefault() is { } tabs)
            tabs.SelectedIndex = tab;
        _errors.SetError(c, text);
        SetFooter(text, Color.Firebrick);
        c.Focus();
    }

    void SetFooter(string text, Color color)
    {
        _footer.ForeColor = color;
        _footer.Text = text;
    }
}
