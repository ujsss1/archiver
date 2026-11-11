# Ядро архиватора (C#)

Собственный алгоритм сжатия без потерь: контекстные модели с арифметическим кодированием,
детектор типа файла и обратимые фильтры (x86 для EXE/DLL, столбцы для CSV). Готовые библиотеки сжатия не используются.

Подробнее об алгоритме — в [docs/algorithm.md](../docs/algorithm.md).

## Что нужно
.NET SDK 10 (`dotnet --version` → 10.x).

## Как подключить к сервису

### Вариант 1: сервис на C# — библиотека

Добавить ссылку на проект `Archiver.Core/Archiver.Core.csproj` и вызывать:

```csharp
using Archiver;

byte[] archive = ArchiveCodec.Compress(fileBytes);    // POST /v1/compress
byte[] original = ArchiveCodec.Decompress(archive);   // POST /v1/decompress
```

- На некорректный или повреждённый архив `Decompress` бросает `InvalidDataException` — сервис отвечает `400` с телом `{"error":{"code":"bad_archive","message":"..."}}`.
- Функции не хранят общего состояния: можно вызывать из нескольких запросов одновременно.

### Вариант 2: сервис на любом языке — консольная программа

Собрать:

```bash
dotnet publish Archiver.Cli -c Release -o out
```

Вызывать (`-` означает stdin/stdout):

```bash
out/archiver compress   input.bin  output.arc
out/archiver decompress input.arc  output.bin
```

| Код выхода | Значение | Ответ сервиса |
|---|---|---|
| 0 | успех | `200` + байты результата |
| 2 | некорректный архив | `400` + JSON с ошибкой |
| 1 | другая ошибка | `500` + JSON с ошибкой |

## Ресурсы
- Скорость: около 1 с на МБ на сжатие и столько же на распаковку (лимит арены — 2 с на МБ). Медленнее, если ноутбук работает от батареи.
- Память: до ~1 ГБ на файле 100 МБ.

## Проверка

```bash
dotnet run --project Archiver.Cli -c Release -- bench    <папка с файлами>   # таблица коэффициентов и времени
dotnet run --project Archiver.Cli -c Release -- selftest <файл>              # испорченные архивы должны отклоняться
```
