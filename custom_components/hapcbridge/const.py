"""Constants for HA PC Bridge."""

from __future__ import annotations

DOMAIN = "hapcbridge"
VERSION = "0.3.0"

CONF_DEVICE_ID = "device_id"
CONF_BASE_TOPIC = "base_topic"
CONF_SEPARATE_ENTITIES = "separate_entities"
DEFAULT_BASE_TOPIC = "hapcbridge"

CARD_URL = "/hapcbridge_static/hapcbridge-card.js"

SIGNAL_UPDATE = "hapcbridge_update_{}"

# Keys in the state JSON → sensor toggle in config.sensors (camelCase, as the service writes it).
SENSOR_TOGGLE = {
    "cpu_load": "cpuLoad",
    "cpu_temp": "cpuTemp",
    "cpu_power": "cpuPower",
    "gpu_load": "gpuLoad",
    "gpu_temp": "gpuTemp",
    "gpu_power": "gpuPower",
    "gpu_memory_used": "gpuMemory",
    "ram_free": "ramFree",
    "ram_usage": "ramUsage",
    "total_power": "totalPower",
    "logged_user": "loggedUser",
    "last_login": "lastLogin",
    "last_boot": "lastBoot",
    "active_window": "activeWindow",
    "active_process": "activeProcess",
    "idle_time": "idleTime",
    "locked": "locked",
}

# Button key (= command payload) → toggle in config.commands.
COMMAND_TOGGLE = {
    "shutdown": "shutdown",
    "restart": "restart",
    "abort_shutdown": "abortShutdown",
    "sleep": "sleep",
    "hibernate": "hibernate",
    "lock": "lock",
    "logoff": "logoff",
    "monitor_off": "monitorOff",
    "close_active_window": "closeActiveWindow",
}
