# rzswitch

EN: lightweight replacement for Razer GameManagerService3. Watches the
foreground window and notifies Synapse via the same SignalR hub contract
(`/GameManagerServiceHub`), so Synapse auto-switches profiles — without the
stock detector service (138+ MB, 25 MB logs, missed detections on
launcher/child processes).

RU: легкая замена службе Razer Game Manager Service 3. Следит за активным
окном и уведомляет Synapse по тому же SignalR-контракту, поэтому Synapse
переключает профили — без штатного детектора (138+ МБ, 25 МБ логов,
пропуски детекта на лаунчерах/дочерних процессах).

## Как это работает

1. Штатный GameManager поднимает Kestrel на `127.0.0.1:<динамический порт>`,
   пишет порт в `HKLM\Software\Razer\Services\GMS3\ServerPort` и раздает
   SignalR-хаб `/GameManagerServiceHub`.
2. Synapse (RazerAppEngine) читает порт из реестра, коннектится и получает
   `OnGameStartedWithPidEvent({pid, signature})` /
   `OnGameStoppedWithPidEvent(...)`, после чего применяет привязанный профиль.
3. `rzswitch` делает то же самое: читает порт из реестра (или слушает
   динамический и сам пишет его в реестр, как оригинал), читает список игр
   из общих `UserGameList_*.xml` (те же файлы, пути которых опубликованы
   в реестре: `UserGameListPath`, `LocalGameListPath`, `GameSettingsListPath`)
   и пушит события по фокусу окна — только когда игра реально foreground.

Детект штатного сервиса пропускает игры, запущенные через лаунчеры
(`Is LaunchHelper / Is child process, so not notifying GameStarted` — видно
в его же логах), и шумит на каждый процесс Edge. Здесь: точный список exe
из вашего UserGameList + переключение только по активному окну.

## Панель управления

При запуске (консоль или сервис) открывается веб-панель в стиле Razer:

- статус GameManager (Razer) и rzswitch: Running/Stopped, тип старта;
- кнопки: выключить/включить GameManager, установить/удалить/старт/стоп
  сервис rzswitch;
- связка: кнопка включения нашего сервиса недоступна, пока активен
  GameManager (конфликт порта/реестра) — проверка и в UI, и на сервере;
- хвост лога с автообновлением.

Действия через `sc.exe` и запись в HKLM требуют прав администратора —
запускайте панель elevated.

## Установка

```bat
:: 1. Остановить и выключить оригинал (admin)
sc stop "Razer Game Manager Service 3"
sc config "Razer Game Manager Service 3" start= disabled

:: 2. Запустить Synapse (он применяет профили)

:: 3. Панель (admin) -> кнопка "Установить сервис", затем "Включить наш сервис"
rzswitch.exe
:: открыть http://127.0.0.1:<порт> (порт виден в логе и в реестре ServerPort)
```

Откат: кнопка «Включить GMS» в панели (`start= demand` + старт) либо
вручную `sc config` обратно; удалить наш сервис — кнопка в панели.

## Сборка

```
dotnet build -c Release
```

.NET 8+ SDK. Один файл `Program.cs`, рантайм-зависимостей кроме
ASP.NET Core нет. Windows-only (HID-детект окна, реестр, службы).

## Статус

Прототип работает: хаб поднимается, игры грузятся из UserGameList,
события START/STOP уходят подписчикам. Проверено против живого Synapse —
см. Issues.

## License

MIT, см. `LICENSE`.
