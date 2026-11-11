/* HA PC Bridge card — status, controls and settings for one PC. No build step. */
const CARD_VERSION = "0.3.1";

const I18N = {
  ru: {
    online: "онлайн", offline: "офлайн", login: "вход", uptime: "аптайм",
    cpu: "CPU", gpu: "GPU", ram: "RAM free", power: "Потребление", estimate: "оценка", of: "из",
    closeWindow: "Закрыть окно", noWindow: "нет активного окна",
    shutdown: "Выкл", restart: "Рестарт", lock: "Блок", monitor: "Монитор", settings: "Настройки", abort: "Отмена выкл.",
    settingsOf: "Настройки", pcOffline: "ПК офлайн — настройки можно менять, когда он включён.",
    interval: "Частота обновления", tempUnit: "Температура", tray: "Значок в трее", delay: "Задержка выключения",
    mac: "MAC для WoL", broadcast: "Broadcast", ip: "IP", gpuName: "Основная GPU", gpuAuto: "авто",
    baseline: "Прочее железо, Вт", sensors: "Датчики", commands: "Команды", cancel: "Отмена", apply: "Применить",
    saved: "Отправлено на ПК", sec: "с", h: "ч", min: "мин", noDevice: "Выберите компьютер в настройках карточки",
    notFound: "Компьютер не найден", pick: "Компьютер", macBad: "MAC в формате AA:BB:CC:DD:EE:FF",
    s: { cpuLoad: "CPU load", cpuTemp: "CPU temp", cpuPower: "CPU Вт", gpuLoad: "GPU load", gpuTemp: "GPU temp",
         gpuPower: "GPU Вт", gpuMemory: "VRAM", ramFree: "RAM free", ramUsage: "RAM %", totalPower: "Потребление",
         loggedUser: "Пользователь", lastLogin: "Вход", lastBoot: "Загрузка", activeWindow: "Окно",
         activeProcess: "Процесс", idleTime: "Простой", locked: "Блокировка" },
    c: { powerSwitch: "Питание", shutdown: "Выкл", restart: "Рестарт", abortShutdown: "Отмена выкл.", sleep: "Сон",
         hibernate: "Гибернация", lock: "Блок", logoff: "Logoff", monitorOff: "Монитор", closeActiveWindow: "Закрыть окно" },
  },
  en: {
    online: "online", offline: "offline", login: "login", uptime: "uptime",
    cpu: "CPU", gpu: "GPU", ram: "RAM free", power: "Power draw", estimate: "estimate", of: "of",
    closeWindow: "Close window", noWindow: "no active window",
    shutdown: "Off", restart: "Restart", lock: "Lock", monitor: "Monitor", settings: "Settings", abort: "Abort",
    settingsOf: "Settings", pcOffline: "PC is offline — settings can be changed while it's on.",
    interval: "Update interval", tempUnit: "Temperature", tray: "Tray icon", delay: "Shutdown delay",
    mac: "WoL MAC", broadcast: "Broadcast", ip: "IP", gpuName: "Primary GPU", gpuAuto: "auto",
    baseline: "Other hardware, W", sensors: "Sensors", commands: "Commands", cancel: "Cancel", apply: "Apply",
    saved: "Sent to the PC", sec: "s", h: "h", min: "min", noDevice: "Pick a PC in the card settings",
    notFound: "PC not found", pick: "PC", macBad: "MAC must look like AA:BB:CC:DD:EE:FF",
    s: { cpuLoad: "CPU load", cpuTemp: "CPU temp", cpuPower: "CPU W", gpuLoad: "GPU load", gpuTemp: "GPU temp",
         gpuPower: "GPU W", gpuMemory: "VRAM", ramFree: "RAM free", ramUsage: "RAM %", totalPower: "Power draw",
         loggedUser: "User", lastLogin: "Login", lastBoot: "Boot", activeWindow: "Window",
         activeProcess: "Process", idleTime: "Idle", locked: "Locked" },
    c: { powerSwitch: "Power", shutdown: "Off", restart: "Restart", abortShutdown: "Abort", sleep: "Sleep",
         hibernate: "Hibernate", lock: "Lock", logoff: "Log off", monitorOff: "Monitor", closeActiveWindow: "Close window" },
  },
};

const MAC_RE = /^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$/;

async function fetchDevices(hass) {
  try { return await hass.callWS({ type: "hapcbridge/devices" }); } catch (e) { return []; }
}

const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

const STYLE = `
  ha-card { padding: 16px; }
  .head { display: flex; align-items: center; gap: 12px; margin-bottom: 12px; }
  .head ha-icon { color: var(--state-icon-active-color, var(--primary-color)); --mdc-icon-size: 28px; }
  .head .t { flex: 1; min-width: 0; }
  .name { font-size: 16px; font-weight: 500; }
  .sub { font-size: 12px; color: var(--secondary-text-color); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .pill { font-size: 11px; padding: 2px 8px; border-radius: 999px; }
  .pill.on { background: rgba(var(--rgb-success-color, 67,160,71), .15); color: var(--success-color, #43a047); }
  .pill.off { background: var(--secondary-background-color); color: var(--secondary-text-color); }
  .tiles { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 6px; }
  .tile { background: var(--secondary-background-color); border-radius: 8px; padding: 6px 8px; min-width: 0; }
  .tile .l { font-size: 11px; color: var(--secondary-text-color); }
  .tile .v { font-size: 16px; font-weight: 500; line-height: 1.3; white-space: nowrap; }
  .tile .x { font-size: 11px; color: var(--secondary-text-color); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .row { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 8px 0; border-top: 1px solid var(--divider-color); font-size: 14px; }
  .row .grow { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--secondary-text-color); }
  .actions { display: flex; gap: 6px; flex-wrap: wrap; padding-top: 10px; border-top: 1px solid var(--divider-color); }
  button.b { display: inline-flex; align-items: center; gap: 4px; font: inherit; font-size: 13px; padding: 5px 10px; border-radius: 8px;
    border: 1px solid var(--divider-color); background: none; color: var(--primary-text-color); cursor: pointer; }
  button.b:hover { background: var(--secondary-background-color); }
  button.b[disabled] { opacity: .45; cursor: default; }
  button.b ha-icon { --mdc-icon-size: 16px; }
  button.b.primary { background: var(--primary-color); color: var(--text-primary-color, #fff); border-color: transparent; }
  .push { margin-left: auto; }
  .settings { margin-top: 12px; border-top: 1px solid var(--divider-color); padding-top: 8px; }
  .settings h3 { font-size: 15px; font-weight: 500; margin: 4px 0 6px; }
  .note { font-size: 13px; color: var(--warning-color, #ffa600); margin: 4px 0 8px; }
  .err { font-size: 12px; color: var(--error-color, #db4437); }
  input.in { font: inherit; font-size: 13px; padding: 4px 8px; border-radius: 6px; border: 1px solid var(--divider-color);
    background: var(--card-background-color); color: var(--primary-text-color); }
  input.mono { font-family: var(--code-font-family, monospace); width: 170px; }
  .seg { display: inline-flex; border: 1px solid var(--divider-color); border-radius: 8px; overflow: hidden; }
  .seg button { font: inherit; font-size: 13px; padding: 4px 12px; border: 0; background: none; color: var(--primary-text-color); cursor: pointer; }
  .seg button.on { background: var(--primary-color); color: var(--text-primary-color, #fff); }
  .chips { display: flex; flex-wrap: wrap; gap: 6px; }
  .chip { font: inherit; font-size: 12px; padding: 3px 10px; border-radius: 8px; border: 1px solid var(--divider-color); background: none; color: var(--primary-text-color); cursor: pointer; }
  .chip.on { background: rgba(var(--rgb-primary-color, 3,169,244), .15); border-color: var(--primary-color); color: var(--primary-color); }
  .block { display: block; }
  .block > div:first-child { margin-bottom: 8px; }
  .foot { display: flex; justify-content: flex-end; align-items: center; gap: 8px; padding-top: 10px; border-top: 1px solid var(--divider-color); }
  fieldset { border: 0; margin: 0; padding: 0; }
  fieldset[disabled] { opacity: .55; }
  @media (max-width: 420px) { .tiles { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
`;

class HaPcBridgeCard extends HTMLElement {
  setConfig(config) {
    this._config = config || {};
    this._open = false;
    this._device = null;
    if (!this.shadowRoot) this.attachShadow({ mode: "open" });
  }

  set hass(hass) {
    const first = !this._hass;
    this._hass = hass;
    this._t = I18N[(hass.language || "en").startsWith("ru") ? "ru" : "en"];
    if (first) { this._load(); return; }
    if (!this._device) {
      // PC not found (integration still starting, wrong id): look again, but not on every state change in the house.
      if (Date.now() - (this._lastLoad || 0) > 10000) this._load();
      return;
    }
    // HA calls this for every state change of every entity. Redraw only when our Computer entity changed.
    const comp = this._comp();
    if (comp !== this._shown || hass.language !== this._shownLang) this._renderStatus();
  }

  getCardSize() { return this._open ? 12 : 5; }

  static getConfigElement() { return document.createElement("hapcbridge-card-editor"); }

  static async getStubConfig(hass) {
    const list = await fetchDevices(hass);
    return { device_id: list[0]?.device_id || "" };
  }

  async _load() {
    if (this._loading) return;
    this._loading = true;
    this._lastLoad = Date.now();
    const list = await fetchDevices(this._hass);
    this._loading = false;
    this._device = list.find((d) => d.device_id === this._config.device_id) || null;
    this._renderAll();
  }

  // ---------------------------------------------------------------- helpers
  // Everything comes from the Computer entity (switch.<pc>_computer) and its attributes.
  _comp() {
    const id = this._device?.entities?.power;
    return id ? this._hass.states[id] : undefined;
  }
  _attrs() { return this._comp()?.attributes || {}; }
  _has(key) { return key in this._attrs(); }
  _val(key) {
    const v = this._attrs()[key];
    return v === undefined || v === null || v === "" ? null : v;
  }
  _num(key, digits = 0) {
    const v = this._val(key);
    if (v === null || isNaN(Number(v))) return "—";
    return Number(v).toFixed(digits);
  }
  _unit(key) { return key.endsWith("_temp") ? this._attrs().temperature_unit || "°C" : ""; }
  _online() { return this._comp()?.state === "on"; }
  _cmd(key) { return (this._attrs().commands || []).includes(key); }

  _call(domain, service, data) { return this._hass.callService(domain, service, data); }
  _press(key) {
    const id = this._device?.entities?.power;
    if (id) this._call("hapcbridge", "command", { entity_id: id, command: key });
  }

  // ---------------------------------------------------------------- render
  _renderAll() {
    const t = this._t;
    if (!this._config.device_id || !this._device) {
      this.shadowRoot.innerHTML = `<style>${STYLE}</style><ha-card><div class="sub">${esc(this._config.device_id ? t.notFound + ": " + this._config.device_id : t.noDevice)}</div></ha-card>`;
      return;
    }
    this.shadowRoot.innerHTML = `<style>${STYLE}</style><ha-card><div id="status"></div><div id="settings"></div></ha-card>`;
    this._renderStatus();
    this._renderSettings();
  }

  _renderStatus() {
    const root = this.shadowRoot?.getElementById("status");
    if (!root || !this._device) return;
    this._shown = this._comp();
    this._shownLang = this._hass.language;
    const t = this._t, on = this._online(), e = this._device.entities, h = (k) => this._has(k);

    const user = this._val("logged_user");
    const login = this._val("last_login");
    const boot = this._val("last_boot");
    const sub = [];
    if (on && user && user !== "none") sub.push(esc(user));
    if (on && login) sub.push(`${t.login} ${new Date(login).toLocaleTimeString(this._hass.language, { hour: "2-digit", minute: "2-digit" })}`);
    if (on && boot) sub.push(`${t.uptime} ${this._dur((Date.now() - new Date(boot)) / 1000)}`);
    if (!on && this._device.info?.ip) sub.push(esc(this._device.info.ip));

    const src = this._val("total_power_source") || "";
    const tile = (label, v, x) => `<div class="tile"><div class="l">${label}</div><div class="v">${v}</div><div class="x">${x || "&nbsp;"}</div></div>`;
    const tiles = [
      h("cpu_load") || h("cpu_temp") ? tile(t.cpu, `${this._num("cpu_load")}%`, this._join(this._temp("cpu_temp"), this._w("cpu_power"))) : "",
      h("gpu_load") || h("gpu_temp") ? tile(t.gpu, `${this._num("gpu_load")}%`, this._join(this._temp("gpu_temp"), this._w("gpu_power"))) : "",
      h("ram_free") ? tile(t.ram, `${this._num("ram_free", 1)} GB`, h("ram_usage") && this._val("ram_usage") !== null ? `${this._num("ram_usage")}%` : "") : "",
      h("total_power") ? tile(t.power, `${this._num("total_power")} W`, src === "estimate" ? t.estimate : src.startsWith("psu") ? "PSU" : "") : "",
    ].join("");

    const win = this._val("active_window");
    const btn = (key, icon, label, extra = "") =>
      this._cmd(key) ? `<button class="b ${extra}" data-press="${key}" ${on ? "" : "disabled"}><ha-icon icon="${icon}"></ha-icon>${label}</button>` : "";

    root.innerHTML = `
      <div class="head">
        <ha-icon icon="${on ? "mdi:desktop-tower-monitor" : "mdi:desktop-tower"}"></ha-icon>
        <div class="t"><div class="name">${esc(this._device.name)}</div><div class="sub">${sub.join(" · ") || "&nbsp;"}</div></div>
        <span class="pill ${on ? "on" : "off"}">${on ? t.online : t.offline}</span>
        ${e.power ? `<ha-switch id="pw" ${on ? "checked" : ""}></ha-switch>` : ""}
      </div>
      ${tiles ? `<div class="tiles">${tiles}</div>` : ""}
      ${h("active_window") || this._cmd("close_active_window") ? `<div class="row" style="margin-top:10px">
        <span class="grow"><ha-icon icon="mdi:application-outline" style="--mdc-icon-size:16px"></ha-icon> ${esc(on && win ? win : t.noWindow)}</span>
        ${btn("close_active_window", "mdi:close", t.closeWindow)}</div>` : ""}
      <div class="actions">
        ${btn("shutdown", "mdi:power", t.shutdown)}
        ${btn("restart", "mdi:restart", t.restart)}
        ${btn("lock", "mdi:lock", t.lock)}
        ${btn("monitor_off", "mdi:monitor-off", t.monitor)}
        <button class="b push" id="gear"><ha-icon icon="mdi:cog"></ha-icon>${t.settings}</button>
      </div>`;

    root.querySelectorAll("[data-press]").forEach((b) => b.addEventListener("click", () => this._press(b.dataset.press)));
    root.querySelector("#gear").addEventListener("click", () => this._toggleSettings());
    const pw = root.querySelector("#pw");
    if (pw) pw.addEventListener("change", (ev) => {
      this._call("switch", ev.target.checked ? "turn_on" : "turn_off", { entity_id: e.power });
    });

    // Online state flipped while the settings form is open → re-render it (enable/disable).
    if (this._open && this._formOnline !== on) this._renderSettings();
  }

  _temp(key) { const v = this._num(key); return v === "—" ? "" : `${v}${this._unit(key)}`; }
  _w(key) { const v = this._num(key); return v === "—" ? "" : `${v} W`; }
  _join(...p) { return p.filter(Boolean).join(" · "); }
  _dur(s) {
    const t = this._t, h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60);
    return h ? `${h} ${t.h} ${m} ${t.min}` : `${m} ${t.min}`;
  }

  async _toggleSettings() {
    this._open = !this._open;
    if (this._open) {
      const list = await fetchDevices(this._hass);
      this._device = list.find((d) => d.device_id === this._config.device_id) || this._device;
      this._draft = JSON.parse(JSON.stringify(this._device.config || {}));
    }
    this._renderSettings();
  }

  _renderSettings() {
    const root = this.shadowRoot?.getElementById("settings");
    if (!root) return;
    if (!this._open) { root.innerHTML = ""; return; }
    const t = this._t, on = this._online(), d = this._draft;
    this._formOnline = on;
    d.sensors ||= {}; d.commands ||= {}; d.wol ||= {}; d.power ||= {};
    const f = (v) => (v === undefined || v === null ? "" : esc(v));
    const isF = String(d.temperatureUnit || "C").toUpperCase().startsWith("F");
    const sensorKeys = Object.keys(t.s);
    const cmdKeys = Object.keys(t.c);
    const chip = (group, key, label) => {
      const val = d[group][key] ?? !(group === "commands" && key === "hibernate");
      return `<button class="chip ${val ? "on" : ""}" data-group="${group}" data-key="${key}">${label}</button>`;
    };

    root.innerHTML = `<div class="settings">
      <h3>${t.settingsOf} ${esc(this._device.name)}</h3>
      ${on ? "" : `<div class="note">${t.pcOffline}</div>`}
      <fieldset ${on ? "" : "disabled"}>
        <div class="row"><span>${t.interval}</span><span style="display:flex;align-items:center;gap:8px">
          <input id="interval" type="range" min="2" max="60" step="1" value="${f(d.updateIntervalSeconds ?? 10)}" style="width:130px">
          <b id="intervalOut" style="font-weight:500;min-width:36px;text-align:right">${f(d.updateIntervalSeconds ?? 10)} ${t.sec}</b></span></div>
        <div class="row"><span>${t.tempUnit}</span><span class="seg">
          <button data-unit="C" class="${isF ? "" : "on"}">°C</button><button data-unit="F" class="${isF ? "on" : ""}">°F</button></span></div>
        <div class="row"><span>${t.tray}</span><ha-switch id="tray" ${d.trayIcon !== false ? "checked" : ""}></ha-switch></div>
        <div class="row"><span>${t.delay}</span><span><input id="delay" class="in" type="number" min="0" max="3600" step="1" style="width:70px;text-align:right" value="${f(d.commands.shutdownDelaySeconds ?? 30)}"> ${t.sec}</span></div>
        <div class="row"><span>${t.mac}</span><span style="text-align:right"><input id="mac" class="in mono" placeholder="${f(this._device.info?.mac)}" value="${f(d.wol.mac)}"><div id="macErr" class="err"></div></span></div>
        <div class="row"><span>${t.broadcast}</span><input id="bcast" class="in mono" value="${f(d.wol.broadcastAddress)}"></div>
        <div class="row"><span>${t.ip}</span><span style="font-family:var(--code-font-family,monospace);font-size:13px;color:var(--secondary-text-color)">${f(this._device.info?.ip) || "—"}</span></div>
        <div class="row"><span>${t.gpuName}</span><input id="gpu" class="in" style="width:170px" placeholder="${t.gpuAuto}" value="${f(d.primaryGpu)}"></div>
        <div class="row"><span>${t.baseline}</span><input id="baseline" class="in" type="number" min="0" max="1000" step="5" style="width:70px;text-align:right" value="${f(d.power.baselineWatts ?? 60)}"></div>
        <div class="row block"><div>${t.sensors}</div><div class="chips">${sensorKeys.map((k) => chip("sensors", k, t.s[k])).join("")}</div></div>
        <div class="row block"><div>${t.commands}</div><div class="chips">${cmdKeys.map((k) => chip("commands", k, t.c[k])).join("")}</div></div>
      </fieldset>
      <div class="foot"><span id="msg" class="sub"></span>
        <button class="b" id="cancel">${t.cancel}</button>
        <button class="b primary" id="apply" ${on ? "" : "disabled"}>${t.apply}</button></div>
    </div>`;

    const $ = (s) => root.querySelector(s);
    $("#interval").addEventListener("input", (e) => { $("#intervalOut").textContent = `${e.target.value} ${t.sec}`; });
    root.querySelectorAll("[data-unit]").forEach((b) => b.addEventListener("click", () => {
      d.temperatureUnit = b.dataset.unit;
      root.querySelectorAll("[data-unit]").forEach((x) => x.classList.toggle("on", x === b));
    }));
    root.querySelectorAll(".chip").forEach((c) => c.addEventListener("click", () => {
      const g = c.dataset.group, k = c.dataset.key;
      d[g][k] = !c.classList.contains("on");
      c.classList.toggle("on", d[g][k]);
    }));
    $("#tray").addEventListener("change", (e) => { d.trayIcon = !!e.target.checked; });
    $("#mac").addEventListener("input", () => { $("#macErr").textContent = ""; });
    $("#cancel").addEventListener("click", () => { this._open = false; this._renderSettings(); });
    $("#apply").addEventListener("click", () => this._apply(root));
  }

  async _apply(root) {
    const t = this._t, d = this._draft, $ = (s) => root.querySelector(s);
    const mac = $("#mac").value.trim();
    if (mac && !MAC_RE.test(mac)) { $("#macErr").textContent = t.macBad; return; }

    const patch = {
      updateIntervalSeconds: Number($("#interval").value),
      temperatureUnit: String(d.temperatureUnit || "C").toUpperCase().startsWith("F") ? "F" : "C",
      trayIcon: d.trayIcon !== false,
      primaryGpu: $("#gpu").value.trim(),
      sensors: d.sensors,
      commands: { ...d.commands, shutdownDelaySeconds: Math.max(0, Math.round(Number($("#delay").value) || 0)) },
      wol: { mac: mac.toUpperCase().replace(/-/g, ":"), broadcastAddress: $("#bcast").value.trim() || "255.255.255.255" },
      power: { baselineWatts: Math.max(0, Number($("#baseline").value) || 0) },
    };
    $("#apply").disabled = true;
    try {
      await this._call("hapcbridge", "set_config", { config_entry_id: this._device.entry_id, config: patch });
      $("#msg").textContent = t.saved;
      // The PC saves, applies and re-publishes its config; entity set may change (entry reload).
      setTimeout(() => { this._open = false; this._device = null; this._load(); }, 2500);
    } catch (err) {
      $("#msg").textContent = err?.message || String(err);
      $("#apply").disabled = false;
    }
  }
}

class HaPcBridgeCardEditor extends HTMLElement {
  setConfig(config) { this._config = { ...config }; this._render(); }
  set hass(hass) {
    const first = !this._hass;
    this._hass = hass;
    if (first) fetchDevices(hass).then((l) => { this._list = l; this._render(); });
  }
  _render() {
    if (!this._hass || !this._list) return;
    const ru = (this._hass.language || "").startsWith("ru");
    this.innerHTML = `<div style="padding:8px 0">
      <label style="display:block;font-size:13px;margin-bottom:4px">${ru ? "Компьютер" : "PC"}</label>
      <select style="width:100%;padding:8px;font:inherit;border-radius:6px">
        ${this._list.map((d) => `<option value="${esc(d.device_id)}" ${d.device_id === this._config.device_id ? "selected" : ""}>${esc(d.name)} (${esc(d.device_id)})</option>`).join("")}
      </select></div>`;
    this.querySelector("select").addEventListener("change", (e) => {
      this._config = { ...this._config, device_id: e.target.value };
      this.dispatchEvent(new CustomEvent("config-changed", { detail: { config: this._config }, bubbles: true, composed: true }));
    });
  }
}

if (!customElements.get("hapcbridge-card")) customElements.define("hapcbridge-card", HaPcBridgeCard);
if (!customElements.get("hapcbridge-card-editor")) customElements.define("hapcbridge-card-editor", HaPcBridgeCardEditor);

window.customCards = window.customCards || [];
if (!window.customCards.some((c) => c.type === "hapcbridge-card")) {
  window.customCards.push({
    type: "hapcbridge-card",
    name: "HA PC Bridge",
    description: "Status, controls and settings of a Windows PC",
    preview: false,
    documentationURL: "https://github.com/muzdai/hapcbridge",
  });
}
console.info(`%c HAPCBRIDGE-CARD %c ${CARD_VERSION} `, "background:#03a9f4;color:#fff", "background:#444;color:#fff");
