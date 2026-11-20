# archiver

Собственный архиватор без потерь: семестровый проект, турнирный трек.

## Цель
Алгоритм сжатия без потерь с лучшим коэффициентом сжатия среди команд.
Тестовые файлы разных типов (текст, таблицы, бинарные файлы, изображения) до ~100 МБ, состав заранее неизвестен.
Распакованный файл должен быть побайтово идентичен исходному.

## Команда

| Участник | Роль |
|---|---|
| [@Pacifist381](https://github.com/Pacifist381) (тимлид) | Разработчик алгоритма |
| [@NORDIC-01](https://github.com/NORDIC-01) | Разработчик ПО |
| [@ujsss1](https://github.com/ujsss1) | DevOps |
| [@koldoval](https://github.com/koldoval) | QA |

## Структура

| Папка | Что внутри |
|---|---|
| `core-cs/` | ядро архиватора на C#: библиотека `Archiver.Core` и консольная программа `Archiver.Cli` ([подробнее](core-cs/README.md)) |
| `docs/` | описание алгоритма ([algorithm.md](docs/algorithm.md)) |
| `scripts/` | `roundtrip.sh` — проверка ядра «сжал → распаковал → сравнил», `service-check.sh` — проверка API сервиса |
| `.github/workflows/` | автопроверка CI |
| `Dockerfile`, `docker-compose.yml` | Docker-образ сервиса и его запуск на сервере |

## Как собрать и запустить

Нужен [.NET SDK 10](https://dotnet.microsoft.com/download) (`dotnet --version` → 10.x).

```bash
dotnet publish core-cs/Archiver.Cli -c Release -o out
out/archiver compress   input.bin  output.arc
out/archiver decompress output.arc restored.bin
```

Остальные команды (`bench`, `selftest`) и подключение ядра к сервису — в [core-cs/README.md](core-cs/README.md).

## Проверки

```bash
bash scripts/roundtrip.sh out/archiver
```

Скрипт генерирует тестовые файлы (пустой, 1 байт, нули, случайные байты, текст, CSV), сжимает и распаковывает каждый и проверяет правила арены:
распакованный файл совпадает с исходным, архив не больше `2 × размер + 1024` байт, на битый архив программа возвращает код 2 (сервис ответит `4xx`).

Проверка запущенного сервиса по сценарию «Проверить API» арены (health, сжатие → распаковка, пустой файл, битый архив → `4xx`):

```bash
bash scripts/service-check.sh http://localhost:8080
```

Если сервис запущен с токеном, передайте его скрипту: `ARENA_TOKEN=<токен> bash scripts/service-check.sh ...` — тогда проверится ещё и ответ `401` без токена.

**CI.** При каждом push и PR в `main` GitHub сам собирает ядро и прогоняет `roundtrip.sh` и `selftest`,
а также собирает Docker-образ сервиса, запускает его и прогоняет `service-check.sh`.
Результат — на вкладке Actions и в PR. PR с красной проверкой в `main` не вливаем.

## HTTP-сервис (API арены)

Сервис в одном файле: core-cs/archiver-service.cs
Ядро подключается как библиотека Archiver.Core, сжатие и распаковка — через ArchiveCodec.

Запуск локально (нужен .NET SDK 10):

    cd core-cs
    dotnet run -c Release archiver-service.cs

Только с `-c Release`: в Debug ядро работает в разы медленнее, и большие файлы уходят без сжатия.

Сервис слушает 0.0.0.0 на порту из переменной ASPNETCORE_HTTP_PORTS (по умолчанию 8080).
Веб-страница: http://localhost:8080

Переменные окружения:
- ASPNETCORE_HTTP_PORTS — порт прослушивания (по умолчанию 8080)
- ARENA_TOKEN — если задан, /v1/compress и /v1/decompress требуют заголовок Authorization: Bearer <токен>; если не задан — проверка отключена

Методы: GET /v1/health, GET /v1/info, POST /v1/compress, POST /v1/decompress.
Тела запросов — только сырые байты, до 110 МБ, поддерживается Transfer-Encoding: chunked.
Пустой файл работает в обе стороны.
Ошибки всегда в формате {"error":{"code":...,"message":...}}:
400 bad_archive, 413 too_large, 401 unauthorized, 500 internal.

Гарантии: тяжёлая операция выполняется одна за раз, /v1/health не блокируется,
на диск ничего не пишется, переадресаций нет, каждый запрос логируется одной строкой
без содержимого файлов.

## Docker

Образ с сервисом (ядро собирается внутри, сборка всегда в Release):

```bash
docker build -t archiver-service .
docker run --rm -p 8080:8080 archiver-service
```

На сервере — через docker compose: автоперезапуск при падении, лимит памяти, токен из файла `.env`:

```bash
cp .env.example .env        # вписать токен арены в .env (файл не коммитим)
docker compose up -d --build
docker compose logs -f
```
