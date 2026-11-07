"""Config flow: PCs are found automatically from their retained MQTT info topic."""

from __future__ import annotations

import json
from typing import Any

import voluptuous as vol

from homeassistant.config_entries import ConfigEntry, ConfigFlow, ConfigFlowResult, OptionsFlow
from homeassistant.core import callback
from homeassistant.helpers.service_info.mqtt import MqttServiceInfo

from .const import CONF_BASE_TOPIC, CONF_DEVICE_ID, CONF_SEPARATE_ENTITIES, DEFAULT_BASE_TOPIC, DOMAIN


class PcBridgeConfigFlow(ConfigFlow, domain=DOMAIN):
    VERSION = 1

    @staticmethod
    @callback
    def async_get_options_flow(config_entry: ConfigEntry) -> OptionsFlow:
        return PcBridgeOptionsFlow()

    def __init__(self) -> None:
        self._device_id: str | None = None
        self._base: str = DEFAULT_BASE_TOPIC
        self._title: str | None = None

    async def async_step_mqtt(self, discovery_info: MqttServiceInfo) -> ConfigFlowResult:
        parts = discovery_info.topic.split("/")
        if len(parts) != 3 or parts[2] != "info":
            return self.async_abort(reason="invalid_discovery_info")
        self._base, self._device_id = parts[0], parts[1]

        await self.async_set_unique_id(self._device_id)
        self._abort_if_unique_id_configured()

        try:
            payload = discovery_info.payload
            info = json.loads(payload if isinstance(payload, str) else payload.decode())
        except (ValueError, AttributeError):
            info = {}
        self._title = info.get("hostname") or self._device_id
        self.context["title_placeholders"] = {"name": self._title}
        return await self.async_step_confirm()

    async def async_step_confirm(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        if user_input is not None:
            return self._create()
        return self.async_show_form(
            step_id="confirm",
            description_placeholders={"name": self._title or "", "device_id": self._device_id or ""},
        )

    async def async_step_user(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        if user_input is not None:
            self._device_id = user_input[CONF_DEVICE_ID].strip().lower()
            self._base = user_input.get(CONF_BASE_TOPIC, DEFAULT_BASE_TOPIC).strip("/")
            self._title = self._device_id
            await self.async_set_unique_id(self._device_id)
            self._abort_if_unique_id_configured()
            return self._create()

        return self.async_show_form(
            step_id="user",
            data_schema=vol.Schema(
                {
                    vol.Required(CONF_DEVICE_ID): str,
                    vol.Optional(CONF_BASE_TOPIC, default=DEFAULT_BASE_TOPIC): str,
                }
            ),
        )

    def _create(self) -> ConfigFlowResult:
        return self.async_create_entry(
            title=self._title or self._device_id or "PC",
            data={CONF_DEVICE_ID: self._device_id, CONF_BASE_TOPIC: self._base},
        )


class PcBridgeOptionsFlow(OptionsFlow):
    """Computer object only, or the object plus one entity per sensor/command."""

    async def async_step_init(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        if user_input is not None:
            return self.async_create_entry(data=user_input)
        current = self.config_entry.options.get(CONF_SEPARATE_ENTITIES, True)
        return self.async_show_form(
            step_id="init",
            data_schema=vol.Schema({vol.Required(CONF_SEPARATE_ENTITIES, default=current): bool}),
        )
