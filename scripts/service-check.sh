#!/usr/bin/env bash
# Проверка HTTP-сервиса по сценарию «Проверить API» арены (раздел 7 спецификации):
# health, сжатие → распаковка нескольких файлов, пустой файл, битый архив → 4xx без падения.
#
# Запуск:  scripts/service-check.sh <адрес сервиса> [папка для временных файлов]
# Пример:  scripts/service-check.sh http://localhost:8080
#
# Если задана переменная ARENA_TOKEN, запросы идут с токеном и дополнительно проверяется,
# что без токена сервис отвечает 401.
#
# Код выхода: 0 — всё прошло, 1 — есть ошибки.

set -u

if [ $# -lt 1 ]; then
    echo "Использование: $0 <адрес сервиса> [рабочая папка]"
    exit 1
fi

BASE="$1"
WORK="${2:-$(mktemp -d)}"
mkdir -p "$WORK"

TOKEN="${ARENA_TOKEN:-}"
AUTH=()
if [ -n "$TOKEN" ]; then
    AUTH=(-H "Authorization: Bearer $TOKEN")
fi

failures=0

# ---------- Ждём, пока сервис поднимется (до 60 с) ----------

for _ in $(seq 1 60); do
    if curl -s -m 3 -o /dev/null "$BASE/v1/health"; then
        break
    fi
    sleep 1
done

# ---------- health и info ----------

health=$(curl -s -m 3 "$BASE/v1/health")
if [ "$health" = '{"status":"ok"}' ]; then
    echo "health: ok"
else
    echo "health: ОШИБКА — ответ «$health», ожидался {\"status\":\"ok\"}"
    failures=$((failures + 1))
fi

info_code=$(curl -s -o /dev/null -w "%{http_code}" "$BASE/v1/info")
if [ "$info_code" = "200" ]; then
    echo "info: ok"
else
    echo "info: ОШИБКА — код $info_code"
    failures=$((failures + 1))
fi

# ---------- Тестовые файлы: как в проверке арены (текст, двоичный, нулевые байты) + пустой ----------

: > "$WORK/empty.bin"
head -c 200000 /dev/zero > "$WORK/zeros.bin"
head -c 65536 /dev/urandom > "$WORK/random.bin"
awk 'BEGIN { for (i = 1; i <= 5000; i++) printf "Строка %d: проверка сервиса архиватора, the quick brown fox.\n", i }' > "$WORK/text.txt"

for file in empty.bin zeros.bin random.bin text.txt; do
    src="$WORK/$file"
    arc="$WORK/$file.arc"
    out="$WORK/$file.out"

    c_code=$(curl -s "${AUTH[@]}" -o "$arc" -w "%{http_code}" --data-binary @"$src" \
        -H "Content-Type: application/octet-stream" "$BASE/v1/compress")
    d_code=$(curl -s "${AUTH[@]}" -o "$out" -w "%{http_code}" --data-binary @"$arc" \
        -H "Content-Type: application/octet-stream" "$BASE/v1/decompress")

    size=$(wc -c < "$src")
    arc_size=$(wc -c < "$arc")
    max_size=$((2 * size + 1024))    # правило арены: архив не больше 2 × размер + 1024 байт

    if [ "$c_code" != "200" ] || [ "$d_code" != "200" ]; then
        echo "$file: ОШИБКА — коды ответов compress $c_code, decompress $d_code"
        failures=$((failures + 1))
    elif ! cmp -s "$src" "$out"; then
        echo "$file: ОШИБКА — распакованный файл не совпал с исходным"
        failures=$((failures + 1))
    elif [ "$arc_size" -gt "$max_size" ]; then
        echo "$file: ОШИБКА — архив $arc_size байт больше $max_size"
        failures=$((failures + 1))
    else
        echo "$file: ok ($size → $arc_size байт)"
    fi
done

# ---------- Битый архив: 4xx с JSON-ошибкой, сервис не падает ----------

head -c 3000 /dev/urandom > "$WORK/bad.arc"
bad_code=$(curl -s "${AUTH[@]}" -o "$WORK/bad.json" -w "%{http_code}" --data-binary @"$WORK/bad.arc" "$BASE/v1/decompress")

if [[ "$bad_code" == 4* ]] && grep -q '"error"' "$WORK/bad.json"; then
    echo "битый архив: ok (код $bad_code)"
else
    echo "битый архив: ОШИБКА — код $bad_code, тело: $(head -c 200 "$WORK/bad.json")"
    failures=$((failures + 1))
fi

if [ "$(curl -s -m 3 "$BASE/v1/health")" = '{"status":"ok"}' ]; then
    echo "сервис жив после битого архива: ok"
else
    echo "сервис жив после битого архива: ОШИБКА — health не отвечает"
    failures=$((failures + 1))
fi

# ---------- Токен: без него — 401 (только если токен задан) ----------

if [ -n "$TOKEN" ]; then
    no_token=$(curl -s -o /dev/null -w "%{http_code}" --data-binary @"$WORK/text.txt" "$BASE/v1/compress")
    if [ "$no_token" = "401" ]; then
        echo "запрос без токена: ok (401)"
    else
        echo "запрос без токена: ОШИБКА — код $no_token, ожидался 401"
        failures=$((failures + 1))
    fi
fi

# ---------- Итог ----------

echo
if [ "$failures" -eq 0 ]; then
    echo "Все проверки сервиса пройдены"
    exit 0
fi

echo "Ошибок: $failures"
exit 1
