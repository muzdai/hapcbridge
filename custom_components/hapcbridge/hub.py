"""MQTT link to one PC running the HA PC Bridge service."""

from __future__ import annotations

import asyncio
import json
import logging
import re
import socket
from typing import Any, Callable

from homeassistant.components import mqtt
from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers.dispatcher import async_dispatcher_send

from .const import (
    COMMAND_TOGGLE,
    CONF_BASE_TOPIC,
    CONF_DEVICE_ID,
    CONF_SEPARATE_ENTITIES,
    DEFAULT_BASE_TOPIC,
    DOMAIN,
    EVENT_NOTIFICATION,
    SENSOR_TOGGLE,
    SIGNAL_UPDATE,
)

_LOGGER = logging.getLogger(__name__)

_MAC_RE = re.compile(r"^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$")


class PcHub:
    """Holds the latest state/info/config of one PC and sends commands to it."""

    def __init__(self, hass: HomeAssistant, entry: ConfigEntry) -> None:
        self.hass = hass
        self.entry = entry
        self.device_id: str = entry.data[CONF_DEVICE_ID]
        base = entry.data.get(CONF_BASE_TOPIC, DEFAULT_BASE_TOPIC)
        self.root = f"{base}/{self.device_id}"
        self.online = False
        self.state: dict[str, Any] = {}
        self.info: dict[str, Any] = {}
        self.config: dict[str, Any] = {}
        self._config_event = asyncio.Event()
        self._unsubs: list[Callable[[], None]] = []
        self._layout: str | None = None
        self.on_layout_change: Callable[[], None] | None = None

    # ------------------------------------------------------------------ lifecycle

    async def async_start(self) -> None:
        # After a reload (settings changed) reuse the config we already have instead of waiting for it.
        cached = self.hass.data.setdefault(DOMAIN, {}).get(self.device_id)
        if cached:
            self.config, self.info, self.online, self.state = cached
            self._config_event.set()

        subs = {
            "availability": self._on_availability,
            "state": self._on_state,
            "info": self._on_info,
            "config": self._on_config,
            "notify/event": self._on_notify_event,
        }
        for leaf, cb in subs.items():
            self._unsubs.append(await mqtt.async_subscribe(self.hass, f"{self.root}/{leaf}", cb, qos=1))

        # Retained config arrives right after subscribing; wait briefly so the
        # platforms know which entities are enabled. Defaults (everything on) otherwise.
        try:
            await asyncio.wait_for(self._config_event.wait(), timeout=5)
        except asyncio.TimeoutError:
            _LOGGER.info("%s: no retained config yet, using defaults", self.device_id)
        self._layout = self._layout_key()

    async def async_stop(self) -> None:
        if self.config:
            self.hass.data.setdefault(DOMAIN, {})[self.device_id] = (self.config, self.info, self.online, self.state)
        for unsub in self._unsubs:
            unsub()
        self._unsubs.clear()

    # ------------------------------------------------------------------ MQTT callbacks

    @callback
    def _on_availability(self, msg: mqtt.ReceiveMessage) -> None:
        self.online = msg.payload == "online"
        self._notify()

    @callback
    def _on_state(self, msg: mqtt.ReceiveMessage) -> None:
        data = _json(msg.payload)
        if data is not None:
            self.state = data
            self._notify()

    @callback
    def _on_info(self, msg: mqtt.ReceiveMessage) -> None:
        data = _json(msg.payload)
        if data is not None:
            self.info = data
            self._notify()

    @callback
    def _on_config(self, msg: mqtt.ReceiveMessage) -> None:
        data = _json(msg.payload)
        if data is None:
            return
        self.config = data
        self._config_event.set()

        # The integration owns the entities now: stop the service's own MQTT discovery.
        # (If the PC is offline the message is lost; it re-sends its config on connect and we try again.)
        if data.get("haDiscovery", True):
            self.hass.async_create_task(self.async_set_config({"haDiscovery": False}))

        # Sensors/commands switched on or off → entity set changes → reload the entry.
        layout = self._layout_key()
        if self._layout is not None and layout != self._layout and self.on_layout_change:
            self._layout = layout
            self.on_layout_change()
        self._notify()

    @callback
    def _on_notify_event(self, msg: mqtt.ReceiveMessage) -> None:
        """The user reacted to a notification on the PC → HA event for automations."""
        data = _json(msg.payload)
        if data is None or msg.retain:
            return
        self.hass.bus.async_fire(
            EVENT_NOTIFICATION,
            {
                "device_id": self.device_id,
                "entity_id": f"switch.{self.device_id}_computer",
                "tag": data.get("tag") or "",
                "action": data.get("action") or "clicked",
            },
        )

    @callback
    def _notify(self) -> None:
        async_dispatcher_send(self.hass, SIGNAL_UPDATE.format(self.entry.entry_id))

    def _layout_key(self) -> str:
        return json.dumps(
            [self.config.get("sensors", {}), self.config.get("commands", {}), self.config.get("temperatureUnit")],
            sort_keys=True,
        )

    # ------------------------------------------------------------------ helpers for entities

    @property
    def separate_entities(self) -> bool:
        """Also create one entity per sensor/command (on by default), besides the Computer object."""
        return bool(self.entry.options.get(CONF_SEPARATE_ENTITIES, True))

    def enabled_commands(self) -> list[str]:
        return [key for key, toggle in COMMAND_TOGGLE.items() if self.command_enabled(toggle)]

    def computer_attributes(self) -> dict[str, Any]:
        """Everything about the PC in one dict — the attributes of the Computer entity."""
        live = self.state if self.online else {}
        attrs: dict[str, Any] = {}
        for key, toggle in SENSOR_TOGGLE.items():
            if self.sensor_enabled(toggle):
                attrs[key] = live.get(key)
        if self.sensor_enabled("gpuLoad"):
            attrs["gpu_name"] = live.get("gpu_name")
        if self.sensor_enabled("totalPower"):
            attrs["total_power_source"] = live.get("total_power_source")
        attrs.update(
            {
                "temperature_unit": "°F" if self.fahrenheit else "°C",
                "ip": self.info.get("ip"),
                "mac": self.wol_mac,
                "broadcast": self.wol_broadcast,
                "hostname": self.info.get("hostname"),
                "os": self.info.get("os"),
                "bridge_version": self.info.get("version"),
                "device_id": self.device_id,
                "commands": self.enabled_commands(),
            }
        )
        return attrs

    def sensor_enabled(self, toggle: str) -> bool:
        return bool(self.config.get("sensors", {}).get(toggle, True))

    def command_enabled(self, toggle: str) -> bool:
        defaults = {"hibernate": False}
        return bool(self.config.get("commands", {}).get(toggle, defaults.get(toggle, True)))

    @property
    def fahrenheit(self) -> bool:
        return str(self.config.get("temperatureUnit", "C")).upper().startswith("F")

    @property
    def name(self) -> str:
        return self.config.get("deviceName") or self.info.get("hostname") or self.device_id

    # ------------------------------------------------------------------ actions

    async def async_command(self, command: str) -> None:
        await mqtt.async_publish(self.hass, f"{self.root}/command", command, qos=1, retain=False)

    async def async_power_off(self) -> None:
        await mqtt.async_publish(self.hass, f"{self.root}/power/set", "OFF", qos=1, retain=False)

    @property
    def notifications_enabled(self) -> bool:
        return self.command_enabled("notifications")

    async def async_notify(self, message: str, title: str | None = None, **extra: Any) -> None:
        """Show a notification on the PC. extra: tag, actions, duration, silent."""
        payload = {"message": message, "title": title or "", **{k: v for k, v in extra.items() if v not in (None, "", [])}}
        await mqtt.async_publish(self.hass, f"{self.root}/notify", json.dumps(payload, ensure_ascii=False), qos=1, retain=False)

    async def async_set_config(self, patch: dict[str, Any]) -> None:
        await mqtt.async_publish(self.hass, f"{self.root}/config/set", json.dumps(patch), qos=1, retain=False)

    @property
    def wol_mac(self) -> str | None:
        mac = (self.config.get("wol", {}) or {}).get("mac") or self.info.get("mac")
        return mac if mac and _MAC_RE.match(mac) else None

    @property
    def wol_broadcast(self) -> str:
        return (
            (self.config.get("wol", {}) or {}).get("broadcastAddress")
            or self.info.get("broadcast")
            or "255.255.255.255"
        )

    async def async_wake(self) -> None:
        mac = self.wol_mac
        if not mac:
            raise ValueError(f"{self.device_id}: MAC address unknown, start the PC once with the service running")
        await self.hass.async_add_executor_job(_send_magic_packet, mac, self.wol_broadcast)
        _LOGGER.info("Sent Wake-on-LAN to %s via %s", mac, self.wol_broadcast)


def _json(payload: Any) -> dict[str, Any] | None:
    if isinstance(payload, bytes):
        payload = payload.decode("utf-8", "replace")
    if not payload:
        return None
    try:
        data = json.loads(payload)
    except ValueError:
        return None
    return data if isinstance(data, dict) else None


def _send_magic_packet(mac: str, broadcast: str) -> None:
    raw = bytes.fromhex(mac.replace(":", "").replace("-", ""))
    packet = b"\xff" * 6 + raw * 16
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        for port in (9, 7):
            sock.sendto(packet, (broadcast, port))
