"""Sensors."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from homeassistant.components.sensor import (
    SensorDeviceClass,
    SensorEntity,
    SensorEntityDescription,
    SensorStateClass,
)
from homeassistant.const import (
    PERCENTAGE,
    EntityCategory,
    UnitOfInformation,
    UnitOfPower,
    UnitOfTemperature,
    UnitOfTime,
)
from homeassistant.core import HomeAssistant
from homeassistant.helpers import entity_registry as er
from homeassistant.helpers.entity_platform import AddEntitiesCallback
from homeassistant.util import dt as dt_util

from . import PcConfigEntry
from .const import SENSOR_TOGGLE
from .entity import PcEntity
from .hub import PcHub

M = SensorStateClass.MEASUREMENT


@dataclass(frozen=True, kw_only=True)
class PcSensorDescription(SensorEntityDescription):
    temperature: bool = False
    attr: str | None = None


SENSORS: tuple[PcSensorDescription, ...] = (
    PcSensorDescription(key="cpu_load", native_unit_of_measurement=PERCENTAGE, state_class=M, icon="mdi:cpu-64-bit", suggested_display_precision=0),
    PcSensorDescription(key="cpu_temp", device_class=SensorDeviceClass.TEMPERATURE, state_class=M, temperature=True, suggested_display_precision=0),
    PcSensorDescription(key="cpu_power", device_class=SensorDeviceClass.POWER, native_unit_of_measurement=UnitOfPower.WATT, state_class=M, suggested_display_precision=0),
    PcSensorDescription(key="gpu_load", native_unit_of_measurement=PERCENTAGE, state_class=M, icon="mdi:expansion-card", suggested_display_precision=0, attr="gpu_name"),
    PcSensorDescription(key="gpu_temp", device_class=SensorDeviceClass.TEMPERATURE, state_class=M, temperature=True, suggested_display_precision=0),
    PcSensorDescription(key="gpu_power", device_class=SensorDeviceClass.POWER, native_unit_of_measurement=UnitOfPower.WATT, state_class=M, suggested_display_precision=0),
    PcSensorDescription(key="gpu_memory_used", device_class=SensorDeviceClass.DATA_SIZE, native_unit_of_measurement=UnitOfInformation.GIGABYTES, state_class=M, suggested_display_precision=1),
    PcSensorDescription(key="ram_free", device_class=SensorDeviceClass.DATA_SIZE, native_unit_of_measurement=UnitOfInformation.GIGABYTES, state_class=M, icon="mdi:memory", suggested_display_precision=1),
    PcSensorDescription(key="ram_usage", native_unit_of_measurement=PERCENTAGE, state_class=M, icon="mdi:memory", suggested_display_precision=0),
    PcSensorDescription(key="total_power", device_class=SensorDeviceClass.POWER, native_unit_of_measurement=UnitOfPower.WATT, state_class=M, suggested_display_precision=0, attr="total_power_source"),
    PcSensorDescription(key="logged_user", icon="mdi:account"),
    PcSensorDescription(key="last_login", device_class=SensorDeviceClass.TIMESTAMP),
    PcSensorDescription(key="last_boot", device_class=SensorDeviceClass.TIMESTAMP, entity_category=EntityCategory.DIAGNOSTIC),
    PcSensorDescription(key="active_window", icon="mdi:application-outline"),
    PcSensorDescription(key="active_process", icon="mdi:application-cog-outline"),
    PcSensorDescription(key="idle_time", device_class=SensorDeviceClass.DURATION, native_unit_of_measurement=UnitOfTime.SECONDS, state_class=M, suggested_display_precision=0),
)


async def async_setup_entry(hass: HomeAssistant, entry: PcConfigEntry, add: AddEntitiesCallback) -> None:
    hub = entry.runtime_data
    entities: list[SensorEntity] = [
        PcSensor(hub, d) for d in SENSORS if hub.sensor_enabled(SENSOR_TOGGLE[d.key])
    ]
    entities += [PcMacSensor(hub), PcIpSensor(hub)]
    add(entities)


class PcSensor(PcEntity, SensorEntity):
    entity_description: PcSensorDescription

    def __init__(self, hub: PcHub, description: PcSensorDescription) -> None:
        super().__init__(hub, description.key)
        self.entity_description = description
        if description.temperature:
            self._attr_native_unit_of_measurement = (
                UnitOfTemperature.FAHRENHEIT if hub.fahrenheit else UnitOfTemperature.CELSIUS
            )

    async def async_added_to_hass(self) -> None:
        await super().async_added_to_hass()
        if self.entity_description.temperature:
            self._apply_display_unit()

    def _apply_display_unit(self) -> None:
        """HA shows temperatures in its own unit system; make the PC's °C/°F choice the display unit."""
        registry = er.async_get(self.hass)
        if not self.registry_entry:
            return
        unit = self._attr_native_unit_of_measurement
        current = (self.registry_entry.options.get("sensor") or {}).get("unit_of_measurement")
        if current != unit:
            registry.async_update_entity_options(
                self.entity_id, "sensor", {**(self.registry_entry.options.get("sensor") or {}), "unit_of_measurement": unit}
            )

    @property
    def native_value(self) -> Any:
        value = self.hub.state.get(self.key)
        if value is None:
            return None
        if self.entity_description.device_class == SensorDeviceClass.TIMESTAMP:
            return dt_util.parse_datetime(value) if isinstance(value, str) else None
        return value

    @property
    def extra_state_attributes(self) -> dict[str, Any] | None:
        attr = self.entity_description.attr
        if not attr:
            return None
        return {attr.removeprefix("total_power_").removeprefix("gpu_"): self.hub.state.get(attr)}


class PcMacSensor(PcEntity, SensorEntity):
    """MAC for Wake-on-LAN — stays readable while the PC is off."""

    _needs_online = False
    _attr_entity_category = EntityCategory.DIAGNOSTIC
    _attr_icon = "mdi:lan"

    def __init__(self, hub: PcHub) -> None:
        super().__init__(hub, "mac")

    @property
    def native_value(self) -> str | None:
        return self.hub.wol_mac

    @property
    def extra_state_attributes(self) -> dict[str, Any]:
        return {"broadcast": self.hub.wol_broadcast}


class PcIpSensor(PcEntity, SensorEntity):
    _needs_online = False
    _attr_entity_category = EntityCategory.DIAGNOSTIC
    _attr_icon = "mdi:ip-network"

    def __init__(self, hub: PcHub) -> None:
        super().__init__(hub, "ip")

    @property
    def native_value(self) -> str | None:
        return self.hub.info.get("ip")


