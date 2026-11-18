# BUG-API-01: Несоответствие путей API спецификации
**Severity:** Blocker
**Priority:** P0
**Компонент:** API
**Окружение:** Windows, Kestrel (localhost:5000), коммит 243fda2

## Описание
В `archiver-service.cs` реализованы эндпоинты `/v1/compress` и `/v1/decompress`. Спецификация арены требует `POST /v1/compset` и `POST /v1/deccomp`.

## Шаги воспроизведения
1. Запустить сервис.
2. Отправить POST-запрос на `/v1/compset`.

## Фактический результат
HTTP 404 Not Found.
## Ожидаемый результат
HTTP 200 с байтами архива.
