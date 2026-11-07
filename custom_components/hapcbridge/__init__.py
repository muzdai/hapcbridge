"""HA PC Bridge — Windows PCs over MQTT, with a settings card."""

from __future__ import annotations

import logging
from pathlib import Path
from typing import Any

import voluptuous as vol

from homeassistant.components import websocket_api
from homeassistant.components.frontend import add_extra_js_url
from homeassistant.components.http import StaticPathConfig
from homeassistant.config_entries import ConfigEntry, ConfigEntryState
from homeassistant.const import Platform
from homeassistant.core import HomeAssistant, ServiceCall, callback
from homeassistant.exceptions import ServiceValidationError
from homeassistant.helpers import config_validation as cv, entity_registry as er
from homeassistant.helpers.typing import ConfigType

from .const import CARD_URL, DOMAIN, VERSION
from .hub import PcHub

_LOGGER = logging.getLogger(__name__)

PLATFORMS = [Platform.SENSOR, Platform.BINARY_SENSOR, Platform.SWITCH, Platform.BUTTON]
CONFIG_SCHEMA = cv.config_entry_only_config_schema(DOMAIN)

type PcConfigEntry = ConfigEntry[PcHub]

SET_CONFIG_SCHEMA = vol.Schema(
    {
        vol.Required("config_entry_id"): cv.string,
        vol.Required("config"): dict,
    }
)


async def async_setup(hass: HomeAssistant, config: ConfigType) -> bool:
    """Register the card, the websocket API and the set_config action once."""
    await hass.http.async_register_static_paths(
        [StaticPathConfig(CARD_URL, str(Path(__file__).parent / "frontend" / "hapcbridge-card.js"), False)]
    )
    if "frontend" in hass.config.components:  # always true in a real HA; absent in bare test setups
        add_extra_js_url(hass, f"{CARD_URL}?v={VERSION}")

    websocket_api.async_register_command(hass, ws_devices)

    async def set_config(call: ServiceCall) -> None:
        hub = _hub(hass, call.data["config_entry_id"])
        await hub.async_set_config(call.data["config"])

    hass.services.async_register(DOMAIN, "set_config", set_config, schema=SET_CONFIG_SCHEMA)
    return True


async def async_setup_entry(hass: HomeAssistant, entry: PcConfigEntry) -> bool:
    hub = PcHub(hass, entry)
    await hub.async_start()
    entry.runtime_data = hub

    @callback
    def _reload() -> None:
        hass.config_entries.async_schedule_reload(entry.entry_id)

    hub.on_layout_change = _reload
    entry.async_on_unload(entry.add_update_listener(_options_updated))
    if not hub.separate_entities:
        # "Computer object only": drop the per-sensor entities left from before.
        registry = er.async_get(hass)
        for ent in er.async_entries_for_config_entry(registry, entry.entry_id):
            if ent.unique_id != f"hapcbridge_{hub.device_id}_power":
                registry.async_remove(ent.entity_id)
    await hass.config_entries.async_forward_entry_setups(entry, PLATFORMS)
    return True


async def _options_updated(hass: HomeAssistant, entry: ConfigEntry) -> None:
    await hass.config_entries.async_reload(entry.entry_id)


async def async_unload_entry(hass: HomeAssistant, entry: PcConfigEntry) -> bool:
    ok = await hass.config_entries.async_unload_platforms(entry, PLATFORMS)
    if ok:
        await entry.runtime_data.async_stop()
    return ok


def _hub(hass: HomeAssistant, entry_id: str) -> PcHub:
    entry = hass.config_entries.async_get_entry(entry_id)
    if entry is None or entry.domain != DOMAIN or not hasattr(entry, "runtime_data"):
        raise ServiceValidationError(f"Unknown HA PC Bridge entry: {entry_id}")
    return entry.runtime_data


def describe(hass: HomeAssistant, entry: ConfigEntry) -> dict[str, Any]:
    """Everything the card needs for one PC."""
    hub: PcHub = entry.runtime_data
    registry = er.async_get(hass)
    entities = {}
    for ent in er.async_entries_for_config_entry(registry, entry.entry_id):
        key = ent.unique_id.removeprefix(f"hapcbridge_{hub.device_id}_")
        entities[key] = ent.entity_id
    return {
        "entry_id": entry.entry_id,
        "device_id": hub.device_id,
        "name": hub.name,
        "online": hub.online,
        "info": hub.info,
        "config": hub.config,
        "entities": entities,
    }


@websocket_api.websocket_command({vol.Required("type"): "hapcbridge/devices"})
@callback
def ws_devices(hass: HomeAssistant, connection: websocket_api.ActiveConnection, msg: dict[str, Any]) -> None:
    connection.send_result(
        msg["id"],
        [describe(hass, e) for e in hass.config_entries.async_entries(DOMAIN) if e.state is ConfigEntryState.LOADED],
    )
