# HA PC Bridge

Служба Windows, которая публикует состояние компьютера в Home Assistant через MQTT (Mosquitto) и принимает команды: выключение, перезагрузка, сон, блокировка, закрытие активного окна. Включение — Wake-on-LAN из Home Assistant. В комплекте интеграция для HACS с карточкой, из которой настраивается сам ПК.

## Home Assistant: интеграция и карточка (HACS)

1. HACS → ⋮ → **Custom repositories** → `https://github.com/muzdai/hapcbridge`, тип **Integration** → установить **HA PC Bridge** → перезапустить HA.
2. Нужна уже настроенная интеграция **MQTT** в HA. Компьютеры с запущенной службой находятся сами: *Настройки → Устройства и службы → Обнаружено → HA PC Bridge*. Вручную: *Добавить интеграцию → HA PC Bridge* и ввести `deviceId`.
3. Карточка регистрируется интеграцией автоматически: *Изменить панель → Добавить карточку → HA PC Bridge*, выбрать компьютер. YAML:
   ```yaml
   type: custom:hapcbridge-card
   device_id: gaming_pc
   ```

### Объект «Компьютер»

Главная сущность — `switch.<deviceId>_computer`:
- **состояние** `on`/`off` — ПК на связи или нет; включение = Wake-on-LAN, выключение = shutdown через службу;
- **атрибуты** — все включённые датчики (`cpu_load`, `cpu_temp`, `gpu_load`, `ram_free`, `total_power`, `logged_user`, `last_login`, `active_window`, `idle_time`, `locked`…), плюс `ip`, `mac`, `temperature_unit`, `commands` (доступные команды). Пока ПК выключен, живые значения равны `null`;
- **команды** — действие `hapcbridge.command`:
  ```yaml
  action: hapcbridge.command
  target:
    entity_id: switch.gaming_pc_computer
  data:
    command: close_active_window   # shutdown, restart, abort_shutdown, sleep, hibernate, lock, logoff, monitor_off
  ```
- в шаблонах: `{{ state_attr('switch.gaming_pc_computer', 'cpu_temp') }}`, в автоматизациях — триггер «состояние/атрибут».

Отдельные сущности на каждый датчик и кнопку по умолчанию тоже создаются (для графиков, статистики, энергии). Отключить: *Устройства и службы → HA PC Bridge → Настроить → «Отдельные сущности датчиков и кнопок»* — останется только объект «Компьютер».

Что даёт интеграция по сравнению с «голым» MQTT Discovery:
- **Wake-on-LAN встроен**: переключатель *Питание* сам шлёт magic packet, автоматизация и `wake_on_lan:` не нужны.
- **Настройки ПК с карточки** (⚙): частота обновления, °C/°F, трей, задержка выключения, MAC/broadcast для WoL, основная GPU, «прочее железо» для расчёта потребления, включение/выключение каждого датчика и команды. Пока ПК выключен, настройки показываются неактивными.
- Служба получает настройки через `hapcbridge/<id>/config/set`, сохраняет в `config.json` и применяет сразу. Брокер, логин/пароль и `deviceId` удалённо не меняются никогда; запретить удалённые изменения совсем — `"allowRemoteConfig": false` в `config.json`.
- После добавления ПК интеграция выключает у службы собственное MQTT Discovery (`haDiscovery: false`), чтобы сущности не дублировались.
- Действие `hapcbridge.set_config` для автоматизаций, например `{"updateIntervalSeconds": 2}` во время игры.

## Как устроено

```
┌──────────── Windows ────────────┐            ┌──────────── HA ───────────┐
│ HaPcBridge.Service (LocalSystem)│   MQTT     │ Mosquitto                 │
│  • датчики CPU/GPU/PSU/RAM      │◄──────────►│ MQTT Discovery → устройство│
│  • сессия, вход, загрузка       │            │ Автоматизация WoL          │
│  • выключение / сон / logoff    │            └───────────────────────────┘
│        ▲ named pipe             │
│        ▼                        │
│ HaPcBridge.Agent (сессия юзера) │
│  • активное окно, простой, lock │
│  • закрыть окно, lock, монитор  │
│  • значок в трее                │
└─────────────────────────────────┘
```

Служба работает в Session 0 и не видит рабочий стол, поэтому всё, что связано с окнами, делает агент. Служба сама запускает агент в сессии вошедшего пользователя и перезапускает его, если он упал. Принимаются подключения только от `HaPcBridge.Agent.exe` из папки службы.

## Сущности в Home Assistant

| Сущность | Тип | Примечание |
|---|---|---|
| Power | switch | состояние = online/offline службы (LWT); OFF → выключение, ON → WoL из HA |
| CPU load / temperature / power | sensor | %, °C/°F, Вт |
| GPU load / temperature / power / memory used | sensor | NVIDIA через NVML, AMD/Intel через LibreHardwareMonitor (ADL) |
| RAM free | sensor | ГБ |
| RAM usage | sensor | % |
| Total power | sensor | Вт, атрибут `total_power_source` (см. ниже) |
| Logged user, Last login | sensor | пользователь консольной сессии, время входа |
| Last boot | sensor | диагностический |
| Active window, Active process | sensor | заголовок и процесс окна на переднем плане |
| Idle time | sensor | секунды с последнего ввода |
| Locked | binary_sensor | сессия заблокирована |
| MAC address | sensor | диагностический, нужен для WoL; без availability, виден и при выключенном ПК |
| Shutdown, Restart, Abort shutdown, Sleep, Hibernate, Lock, Log off, Monitor off, Close active window | button | |

Каждый датчик и каждую команду можно отключить в конфиге — отключённые сразу удаляются из HA.

### Общее потребление (Total power)

1. **Если есть блок питания с телеметрией** (Corsair HXi/RMi/AXi, NZXT C-series и т.п., подключённый по USB) — берётся реальное потребление от розетки, `total_power_source: "psu: …"`.
2. **Иначе оценка**: `(CPU package + дискретные GPU) / КПД БП + baselineWatts`, источник `estimate`. Мощность встроенной графики уже входит в CPU package и не добавляется повторно. `baselineWatts` (по умолчанию 60) — плата, память, диски, вентиляторы, сетевая карта; подгони один раз по ваттметру/умной розетке.

Точнее всего — умная розетка с измерением мощности; тогда этот датчик полезен как разбивка «что именно жрёт».

## Установка

1. Скачай `hapcbridge-win-x64.zip` (собранный локально архив) и распакуй, например, в `C:\Program Files\HaPcBridge`.
2. Для температур CPU нужен драйвер **PawnIO** (LibreHardwareMonitor ≥ 0.9.5 перешёл на него со старого WinRing0, который Defender блокирует): поставь установщик PawnIO (релизы на GitHub у namazso; его же ставят свежие LibreHardwareMonitor/FanControl/OpenRGB). Без него температура/мощность CPU будут пустыми, остальное работает.
3. Из админского терминала:
   ```
   HaPcBridge.Service.exe install
   ```
   Создаётся служба `HaPcBridge` (автозапуск с задержкой, перезапуск при сбое) и `%ProgramData%\HaPcBridge\config.json`.
4. Правый клик по значку в трее → **Настройки…** (или двойной клик): брокер, логин/пароль, частота, °C/°F, WoL, датчики и команды. Окно работает без прав администратора — изменения сохраняет служба. Если значок скрыт: `HaPcBridge.Agent.exe --settings`.
5. Удаление: `HaPcBridge.Service.exe uninstall`.

Отладка: остановить службу и запустить `HaPcBridge.Service.exe` из админской консоли — лог в консоли; агент тогда запусти вручную. Логи службы: `%ProgramData%\HaPcBridge\logs`, предупреждения дублируются в журнал событий Windows (источник `HaPcBridge`).

## Настройки

Всё ниже настраивается в окне настроек из трея или с карточки HA. Сам файл `%ProgramData%\HaPcBridge\config.json` доступен только администраторам и SYSTEM; пример со всеми параметрами — [`config.example.json`](config.example.json). Главное:

- `temperatureUnit` — `"C"` или `"F"`
- `trayIcon` — показывать значок в трее
- `sensors.*`, `commands.*` — включить/выключить каждую сущность
- `commands.shutdownDelaySeconds` — Windows покажет предупреждение, выключение можно отменить кнопкой **Abort shutdown** (или `shutdown /a`)
- `primaryGpu` — если видеокарт несколько, часть имени нужной
- `wol.mac` / `wol.broadcastAddress` — если WoL должен идти на конкретную карту (например, встроенную, а не 10G)

Пароль MQTT после первого запуска шифруется (DPAPI, ключ машины), папка конфигурации доступна только SYSTEM и администраторам.

## Wake-on-LAN без интеграции

С интеграцией из HACS этот раздел не нужен. Если работаешь только через MQTT Discovery: выключенный ПК не может принять команду «включись», поэтому:

1. В `configuration.yaml` HA должна быть строка `wake_on_lan:`.
2. Добавь автоматизацию из [`ha/wake_on_lan.yaml`](ha/wake_on_lan.yaml) — она ловит ON на переключателе **Power** любого ПК и шлёт magic packet на MAC, который служба опубликовала (retained), пока ПК был включён.
3. В BIOS и в свойствах сетевой карты Windows должен быть включён WoL, а «Быстрый запуск» Windows лучше отключить.

Сущность MAC получает id `sensor.<deviceId>_mac` (поле `default_entity_id`, HA 2025.10+). Если у тебя id другой — поправь его в автоматизации.

## MQTT-топики

| Топик | Направление | Содержимое |
|---|---|---|
| `hapcbridge/<id>/availability` | → HA | `online` / `offline` (retained, LWT) |
| `hapcbridge/<id>/state` | → HA | JSON со всеми датчиками, каждые `updateIntervalSeconds` |
| `hapcbridge/<id>/info` | → HA | JSON: mac, ip, broadcast, hostname, os, version (retained) |
| `hapcbridge/<id>/command` | ← HA | `shutdown`, `restart`, `abort_shutdown`, `sleep`, `hibernate`, `lock`, `logoff`, `monitor_off`, `close_active_window` |
| `hapcbridge/<id>/power/set` | ← HA | `OFF` → выключение (`ON` обрабатывает HA через WoL) |
| `hapcbridge/<id>/config` | → HA | текущие настройки без раздела `mqtt` (retained) |
| `hapcbridge/<id>/config/set` | ← HA | частичный JSON с настройками |

## Сборка

```
dotnet publish src/HaPcBridge.Service -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
dotnet publish src/HaPcBridge.Agent   -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

Сборка делается локально, GitHub используется только для кода.

Тесты интеграции: `pip install pytest-homeassistant-custom-component && pytest`.

## Дальше по плану

Громкость/медиа, переключение аудиовыхода, уведомления из HA в Windows, запуск приложений по белому списку, яркость/вход монитора (DDC/CI), сущность `update` с самообновлением, окно настроек.
