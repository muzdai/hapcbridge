"""End-to-end checks for the HA PC Bridge integration against a mocked MQTT broker."""

import asyncio
import json
from unittest.mock import patch

from homeassistant.core import HomeAssistant
from homeassistant.helpers import entity_registry as er
from homeassistant.helpers.service_info.mqtt import MqttServiceInfo
from pytest_homeassistant_custom_component.common import MockConfigEntry, async_fire_mqtt_message

from custom_components.hapcbridge.const import DOMAIN

ROOT = "hapcbridge/gaming_pc"
INFO = {"mac": "9C:6B:00:12:AB:CD", "ip": "192.168.1.50", "broadcast": "192.168.1.255", "hostname": "GAMING-PC", "version": "0.2.0"}
CONFIG = {
    "deviceName": "Gaming PC", "temperatureUnit": "C", "updateIntervalSeconds": 10, "trayIcon": True, "haDiscovery": True,
    "sensors": {"idleTime": False}, "commands": {"sleep": False, "hibernate": False}, "wol": {"mac": "", "broadcastAddress": "192.168.1.255"},
}
STATE = {"cpu_load": 23.0, "cpu_temp": 58.2, "gpu_load": 87, "ram_free": 18.4, "total_power": 472, "total_power_source": "estimate",
         "logged_user": "vlad", "last_login": "2026-11-07T05:14:00+00:00", "active_window": "Notepad", "locked": False}


async def _setup(hass: HomeAssistant, mqtt_mock):
    entry = MockConfigEntry(domain=DOMAIN, unique_id="gaming_pc", data={"device_id": "gaming_pc", "base_topic": "hapcbridge"}, title="GAMING-PC")
    entry.add_to_hass(hass)

    # A real broker delivers retained messages right after SUBSCRIBE; emulate that.
    setup = hass.async_create_task(hass.config_entries.async_setup(entry.entry_id))
    await asyncio.sleep(0.1)  # let the integration subscribe
    async_fire_mqtt_message(hass, f"{ROOT}/config", json.dumps(CONFIG), retain=True)
    async_fire_mqtt_message(hass, f"{ROOT}/info", json.dumps(INFO), retain=True)
    async_fire_mqtt_message(hass, f"{ROOT}/availability", "online", retain=True)
    assert await setup
    await hass.async_block_till_done()
    async_fire_mqtt_message(hass, f"{ROOT}/state", json.dumps(STATE))
    await hass.async_block_till_done()
    return entry


def _eid(hass, key, domain="sensor"):
    return er.async_get(hass).async_get_entity_id(domain, DOMAIN, f"hapcbridge_gaming_pc_{key}")


async def test_entities_and_commands(hass: HomeAssistant, mqtt_mock) -> None:
    entry = await _setup(hass, mqtt_mock)

    assert hass.states.get(_eid(hass, "cpu_load")).state == "23.0"
    assert hass.states.get(_eid(hass, "ram_free")).state == "18.4"
    assert hass.states.get(_eid(hass, "total_power")).attributes["source"] == "estimate"
    assert hass.states.get(_eid(hass, "mac")).state == "9C:6B:00:12:AB:CD"
    assert hass.states.get(_eid(hass, "locked", "binary_sensor")).state == "off"
    assert hass.states.get(_eid(hass, "power", "switch")).state == "on"
    assert _eid(hass, "idle_time") is None          # disabled in config
    assert _eid(hass, "sleep", "button") is None     # disabled in config

    # The integration asked the PC to stop its own MQTT discovery.
    mqtt_mock.async_publish.assert_any_call(f"{ROOT}/config/set", json.dumps({"haDiscovery": False}), 1, False)

    await hass.services.async_call("button", "press", {"entity_id": _eid(hass, "lock", "button")}, blocking=True)
    mqtt_mock.async_publish.assert_any_call(f"{ROOT}/command", "lock", 1, False)

    await hass.services.async_call("switch", "turn_off", {"entity_id": _eid(hass, "power", "switch")}, blocking=True)
    mqtt_mock.async_publish.assert_any_call(f"{ROOT}/power/set", "OFF", 1, False)

    await hass.services.async_call(DOMAIN, "set_config", {"config_entry_id": entry.entry_id, "config": {"temperatureUnit": "F"}}, blocking=True)
    mqtt_mock.async_publish.assert_any_call(f"{ROOT}/config/set", json.dumps({"temperatureUnit": "F"}), 1, False)

    # Offline: data entities unavailable, power switch and MAC stay usable.
    async_fire_mqtt_message(hass, f"{ROOT}/availability", "offline")
    await hass.async_block_till_done()
    assert hass.states.get(_eid(hass, "cpu_load")).state == "unavailable"
    assert hass.states.get(_eid(hass, "power", "switch")).state == "off"
    assert hass.states.get(_eid(hass, "mac")).state == "9C:6B:00:12:AB:CD"

    with patch("custom_components.hapcbridge.hub._send_magic_packet") as wol:
        await hass.services.async_call("switch", "turn_on", {"entity_id": _eid(hass, "power", "switch")}, blocking=True)
    wol.assert_called_once_with("9C:6B:00:12:AB:CD", "192.168.1.255")


async def test_layout_change_reloads(hass: HomeAssistant, mqtt_mock) -> None:
    await _setup(hass, mqtt_mock)
    cfg = {**CONFIG, "haDiscovery": False, "temperatureUnit": "F", "sensors": {"idleTime": True}}
    # Live update: brokers forward it to existing subscribers with retain=0.
    async_fire_mqtt_message(hass, f"{ROOT}/config", json.dumps(cfg))
    await hass.async_block_till_done()
    async_fire_mqtt_message(hass, f"{ROOT}/state", json.dumps({**STATE, "idle_time": 42, "cpu_temp": 137}))
    await hass.async_block_till_done()
    assert hass.states.get(_eid(hass, "idle_time")) is not None
    temp = hass.states.get(_eid(hass, "cpu_temp"))
    assert temp.attributes["unit_of_measurement"] == "°F" and float(temp.state) == 137


async def test_websocket_and_card(hass: HomeAssistant, mqtt_mock, hass_ws_client, hass_client) -> None:
    await _setup(hass, mqtt_mock)
    ws = await hass_ws_client(hass)
    await ws.send_json({"id": 1, "type": "hapcbridge/devices"})
    res = await ws.receive_json()
    assert res["success"]
    dev = res["result"][0]
    assert dev["device_id"] == "gaming_pc" and dev["name"] == "Gaming PC" and dev["online"]
    assert dev["entities"]["power"].startswith("switch.")
    assert dev["info"]["ip"] == "192.168.1.50"

    client = await hass_client()
    resp = await client.get("/hapcbridge_static/hapcbridge-card.js")
    assert resp.status == 200
    assert "hapcbridge-card" in await resp.text()


async def test_mqtt_discovery_flow(hass: HomeAssistant, mqtt_mock) -> None:
    info = MqttServiceInfo(topic=f"{ROOT}/info", payload=json.dumps(INFO), qos=0, retain=True, subscribed_topic="hapcbridge/+/info", timestamp=0)
    result = await hass.config_entries.flow.async_init(DOMAIN, context={"source": "mqtt"}, data=info)
    assert result["type"] == "form" and result["step_id"] == "confirm"
    with patch("custom_components.hapcbridge.async_setup_entry", return_value=True):
        result = await hass.config_entries.flow.async_configure(result["flow_id"], {})
    assert result["type"] == "create_entry"
    assert result["title"] == "GAMING-PC"
    assert result["data"] == {"device_id": "gaming_pc", "base_topic": "hapcbridge"}
