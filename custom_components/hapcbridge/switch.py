"""The Computer entity: on/off = Wake-on-LAN / shutdown, everything else in its attributes."""

from __future__ import annotations

from typing import Any

import voluptuous as vol

from homeassistant.components.switch import SwitchDeviceClass, SwitchEntity
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import HomeAssistantError, ServiceValidationError
from homeassistant.helpers import entity_platform
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from . import PcConfigEntry
from .const import COMMAND_TOGGLE
from .entity import PcEntity


async def async_setup_entry(hass: HomeAssistant, entry: PcConfigEntry, add: AddEntitiesCallback) -> None:
    hub = entry.runtime_data
    add([PcComputer(hub)])
    platform = entity_platform.async_get_current_platform()
    platform.async_register_entity_service(
        "command", {vol.Required("command"): vol.In(list(COMMAND_TOGGLE))}, "async_send_command"
    )


class PcComputer(PcEntity, SwitchEntity):
    """switch.<pc>_computer — state on/off, all sensors as attributes, commands via hapcbridge.command."""

    _needs_online = False
    _attr_device_class = SwitchDeviceClass.SWITCH
    _attr_icon = "mdi:desktop-tower-monitor"

    def __init__(self, hub) -> None:
        # Key stays "power" so the entity keeps its id/history from earlier versions.
        super().__init__(hub, "power")
        self.entity_id = f"switch.{hub.device_id}_computer"

    @property
    def is_on(self) -> bool:
        return self.hub.online

    @property
    def icon(self) -> str:
        return "mdi:desktop-tower-monitor" if self.hub.online else "mdi:desktop-tower"

    @property
    def extra_state_attributes(self) -> dict[str, Any]:
        return self.hub.computer_attributes()

    async def async_turn_on(self, **kwargs: Any) -> None:
        if not self.hub.command_enabled("powerSwitch"):
            raise ServiceValidationError("Power control is disabled in the PC settings")
        try:
            await self.hub.async_wake()
        except ValueError as err:
            raise HomeAssistantError(str(err)) from err

    async def async_turn_off(self, **kwargs: Any) -> None:
        if not self.hub.command_enabled("powerSwitch"):
            raise ServiceValidationError("Power control is disabled in the PC settings")
        if self.hub.online:
            await self.hub.async_power_off()

    async def async_send_command(self, command: str) -> None:
        if not self.hub.online:
            raise HomeAssistantError(f"{self.hub.name} is offline")
        if command not in self.hub.enabled_commands():
            raise ServiceValidationError(f"Command '{command}' is disabled in the PC settings")
        await self.hub.async_command(command)
