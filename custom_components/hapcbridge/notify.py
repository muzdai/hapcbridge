"""Standard notify entity: notify.send_message → a Windows notification on the PC."""

from __future__ import annotations

from homeassistant.components.notify import NotifyEntity, NotifyEntityFeature
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from . import PcConfigEntry
from .entity import PcEntity


async def async_setup_entry(hass: HomeAssistant, entry: PcConfigEntry, add: AddEntitiesCallback) -> None:
    hub = entry.runtime_data
    if hub.separate_entities and hub.notifications_enabled:
        add([PcNotify(hub)])


class PcNotify(PcEntity, NotifyEntity):
    _attr_supported_features = NotifyEntityFeature.TITLE
    _attr_icon = "mdi:message-badge-outline"

    def __init__(self, hub) -> None:
        super().__init__(hub, "notification")

    async def async_send_message(self, message: str, title: str | None = None) -> None:
        await self.hub.async_notify(message, title)
