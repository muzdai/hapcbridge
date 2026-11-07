"""Power switch: on = Wake-on-LAN, off = shutdown through the service."""

from __future__ import annotations

from typing import Any

from homeassistant.components.switch import SwitchDeviceClass, SwitchEntity
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from . import PcConfigEntry
from .entity import PcEntity


async def async_setup_entry(hass: HomeAssistant, entry: PcConfigEntry, add: AddEntitiesCallback) -> None:
    hub = entry.runtime_data
    if hub.command_enabled("powerSwitch"):
        add([PcPower(hub)])


class PcPower(PcEntity, SwitchEntity):
    _needs_online = False
    _attr_device_class = SwitchDeviceClass.SWITCH
    _attr_icon = "mdi:desktop-tower-monitor"

    def __init__(self, hub) -> None:
        super().__init__(hub, "power")

    @property
    def is_on(self) -> bool:
        return self.hub.online

    async def async_turn_on(self, **kwargs: Any) -> None:
        try:
            await self.hub.async_wake()
        except ValueError as err:
            raise HomeAssistantError(str(err)) from err

    async def async_turn_off(self, **kwargs: Any) -> None:
        if not self.hub.online:
            return
        await self.hub.async_power_off()
