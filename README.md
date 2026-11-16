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
| `core/` | версия 0 на Python (выбор лучшего из zlib/bz2/lzma) — оставлена для сравнения |
| `docs/` | описание алгоритма ([algorithm.md](docs/algorithm.md)) |
| `scripts/` | `roundtrip.sh` — проверка «сжал → распаковал → сравнил» |
| `.github/workflows/` | автопроверка CI |
| `Dockerfile` | сборка ядра в Docker-образ |

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

**CI.** При каждом push и PR в `main` GitHub сам собирает ядро, запускает `roundtrip.sh` и `selftest` и собирает Docker-образ.
Результат — на вкладке Actions и в PR. PR с красной проверкой в `main` не вливаем.

## Docker

```bash
docker build -t archiver .
docker run --rm -i archiver compress - - < input.bin > output.arc
docker run --rm -i archiver decompress - - < output.arc > restored.bin
```

Сейчас в образе только ядро. Когда появится сервис с API, он добавится в этот же `Dockerfile`.
