"""Command buttons."""

from __future__ import annotations

from homeassistant.components.button import ButtonEntity
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from . import PcConfigEntry
from .const import COMMAND_TOGGLE
from .entity import PcEntity

ICONS = {
    "shutdown": "mdi:power",
    "restart": "mdi:restart",
    "abort_shutdown": "mdi:cancel",
    "sleep": "mdi:power-sleep",
    "hibernate": "mdi:snowflake",
    "lock": "mdi:lock",
    "logoff": "mdi:logout",
    "monitor_off": "mdi:monitor-off",
    "close_active_window": "mdi:close-box-outline",
}


async def async_setup_entry(hass: HomeAssistant, entry: PcConfigEntry, add: AddEntitiesCallback) -> None:
    hub = entry.runtime_data
    add(PcButton(hub, key) for key, toggle in COMMAND_TOGGLE.items() if hub.command_enabled(toggle))


class PcButton(PcEntity, ButtonEntity):
    def __init__(self, hub, key: str) -> None:
        super().__init__(hub, key)
        self._attr_icon = ICONS.get(key)

    async def async_press(self) -> None:
        await self.hub.async_command(self.key)
