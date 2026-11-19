# SmartRelay (C# / Windows)

Локальное управление розеткой SmartLife / Tuya **без интернета** — прямо по локальной сети (LAN) через TCP-порт 6668.

## Что нужно
- .NET 6+ (уже включён в проект) или опубликованный `SmartRelay.exe`
- Device ID розетки (gwId / devId, обычно 20 символов)
- Local Key (ключ локального доступа, 16 символов или 32 hex-символа)
- IP-адрес розетки в вашей Wi-Fi сети

**Важно:** Local Key нужно получить один раз (через портал разработчиков Tuya, резервную копию приложения SmartLife или перехват трафика). После этого программа работает автономно, без подключения в интернет.

## Быстрый запуск (исходники)

```bash
dotnet run -c Release -- --id 0123456789abcdef0123 --key abcd1234efgh5678 --ip 192.168.1.42 on
dotnet run -c Release -- --id 0123456789abcdef0123 --key abcd1234efgh5678 --ip 192.168.1.42 off
dotnet run -c Release -- --id 0123456789abcdef0123 --key abcd1234efgh5678 --ip 192.168.1.42 status
dotnet run -c Release -- --id 0123456789abcdef0123 --key abcd1234efgh5678 --ip 192.168.1.42 toggle
```

## Сборка standalone .exe для Windows

```bash
dotnet publish -c Release -r win-x64 --self-contained false
```

Итоговый `SmartRelay.exe` будет в `bin/Release/netX.X/win-x64/publish/`.

## Конфигурационный файл

Создайте `smartrelay.json` рядом с exe (скопируйте из `smartrelay.json.example`) и запускайте без аргументов (действие указывается позиционно):

```
SmartRelay.exe on
SmartRelay.exe status
```

## Параметры командной строки

| Параметр | Описание |
|---|---|
| `--id` | Device ID розетки |
| `--key` | Local Key (16 символов или 32 hex) |
| `--ip` | IP-адрес розетки в локальной сети |
| `--port` | TCP порт (по умолчанию 6668) |
| `--index` | Номер точки данных DP (по умолчанию 1) |
| `--timeout` | Таймаут сети, сек (по умолчанию 5) |
| `--config` | Путь к JSON-конфигу (по умолчанию smartrelay.json) |

Действие указывается последним: `on`, `off`, `status`, `toggle`.

## Как узнать Device ID и Local Key

1. **Device ID** обычно можно найти в приложении SmartLife/Tuya (раздел устройства → параметры) или в роутере.
2. **Local Key** получается через [Tuya IoT Platform](https://iot.tuya.com) (раздел Cloud → Devices → просмотреть ключ) или из локальной копии конфигурации приложения SmartLife.

## Протокол

Реализована ревизия Tuya 3.1 (кадр 55AA) с AES-128-ECB шифрованием, MD5-сигнатурой и CRC32. Весь код на чистом C#, без внешних зависимостей.
