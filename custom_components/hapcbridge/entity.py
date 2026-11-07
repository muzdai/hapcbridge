"""Shared entity base."""

from __future__ import annotations

from homeassistant.core import callback
from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.dispatcher import async_dispatcher_connect
from homeassistant.helpers.entity import Entity

from .const import DOMAIN, SIGNAL_UPDATE
from .hub import PcHub


class PcEntity(Entity):
    _attr_has_entity_name = True
    _attr_should_poll = False
    #: False for entities that must work while the PC is off (power switch, MAC).
    _needs_online = True

    def __init__(self, hub: PcHub, key: str) -> None:
        self.hub = hub
        self.key = key
        self._attr_unique_id = f"hapcbridge_{hub.device_id}_{key}"
        self._attr_translation_key = key
        self._attr_device_info = DeviceInfo(
            identifiers={(DOMAIN, hub.device_id)},
            name=hub.name,
            manufacturer="HA PC Bridge",
            model="Windows PC",
            sw_version=hub.info.get("version"),
            hw_version=hub.info.get("os"),
        )

    @property
    def available(self) -> bool:
        return self.hub.online or not self._needs_online

    async def async_added_to_hass(self) -> None:
        self.async_on_remove(
            async_dispatcher_connect(self.hass, SIGNAL_UPDATE.format(self.hub.entry.entry_id), self._handle_update)
        )

    @callback
    def _handle_update(self) -> None:
        self.async_write_ha_state()


