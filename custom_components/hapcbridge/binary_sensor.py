"""Binary sensors."""

from __future__ import annotations

from homeassistant.components.binary_sensor import BinarySensorEntity
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from . import PcConfigEntry
from .entity import PcEntity


async def async_setup_entry(hass: HomeAssistant, entry: PcConfigEntry, add: AddEntitiesCallback) -> None:
    hub = entry.runtime_data
    if not hub.separate_entities:
        return
    if hub.sensor_enabled("locked"):
        add([PcLocked(hub)])


class PcLocked(PcEntity, BinarySensorEntity):
    _attr_icon = "mdi:lock"

    def __init__(self, hub) -> None:
        super().__init__(hub, "locked")

    @property
    def is_on(self) -> bool | None:
        value = self.hub.state.get("locked")
        return None if value is None else bool(value)
