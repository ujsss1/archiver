#!/usr/bin/env bash
set -euo pipefail

# URL сервиса (по умолчанию локальный, можно переопределить)
URL="${1:-http://localhost:5000}"
AUTH=()
if [ -n "${ARENA_TOKEN:-}" ]; then
    AUTH=(-H "Authorization: Bearer $ARENA_TOKEN")
fi

TMP_DIR=$(mktemp -d)
trap 'rm -rf "$TMP_DIR"' EXIT

echo "▶ Запуск QA edge-cases против $URL"

# 1. Проверка пустого файла
echo "  [1/4] Пустой файл (0 байт)..."
: > "$TMP_DIR/empty.bin"
curl -sf "${AUTH[@]}" -X POST -H "Content-Type: application/octet-stream" --data-binary "@$TMP_DIR/empty.bin" "$URL/v1/compress" -o "$TMP_DIR/empty.arc" || { echo "❌ Ошибка сжатия пустого файла"; exit 1; }
curl -sf "${AUTH[@]}" -X POST -H "Content-Type: application/octet-stream" --data-binary "@$TMP_DIR/empty.arc" "$URL/v1/decompress" -o "$TMP_DIR/empty.out" || { echo "❌ Ошибка распаковки пустого файла"; exit 1; }
cmp -s "$TMP_DIR/empty.bin" "$TMP_DIR/empty.out" || { echo "❌ Пустой файл не совпал"; exit 1; }

# 2. Проверка файла из 1 байта
echo "  [2/4] Файл из 1 байта..."
printf 'A' > "$TMP_DIR/one.bin"
curl -sf "${AUTH[@]}" -X POST -H "Content-Type: application/octet-stream" --data-binary "@$TMP_DIR/one.bin" "$URL/v1/compress" -o "$TMP_DIR/one.arc" || { echo "❌ Ошибка сжатия 1 байта"; exit 1; }
curl -sf "${AUTH[@]}" -X POST -H "Content-Type: application/octet-stream" --data-binary "@$TMP_DIR/one.arc" "$URL/v1/decompress" -o "$TMP_DIR/one.out" || { echo "❌ Ошибка распаковки 1 байта"; exit 1; }
cmp -s "$TMP_DIR/one.bin" "$TMP_DIR/one.out" || { echo "❌ Файл в 1 байт не совпал"; exit 1; }

# 3. Проверка случайных данных (256 КБ)
echo "  [3/4] Случайные данные (256 КБ)..."
head -c 262144 /dev/urandom > "$TMP_DIR/rand.bin"
curl -sf "${AUTH[@]}" -X POST -H "Content-Type: application/octet-stream" --data-binary "@$TMP_DIR/rand.bin" "$URL/v1/compress" -o "$TMP_DIR/rand.arc" || { echo "❌ Ошибка сжатия рандома"; exit 1; }

# Проверка лимита размера
ARC_SIZE=$(wc -c < "$TMP_DIR/rand.arc")
SRC_SIZE=$(wc -c < "$TMP_DIR/rand.bin")
LIMIT=$((2 * SRC_SIZE + 1024))
if [ "$ARC_SIZE" -gt "$LIMIT" ]; then
  echo "❌ Слишком большой архив для рандома: $ARC_SIZE > $LIMIT"
  exit 1
fi

curl -sf "${AUTH[@]}" -X POST -H "Content-Type: application/octet-stream" --data-binary "@$TMP_DIR/rand.arc" "$URL/v1/decompress" -o "$TMP_DIR/rand.out" || { echo "❌ Ошибка распаковки рандома"; exit 1; }
cmp -s "$TMP_DIR/rand.bin" "$TMP_DIR/rand.out" || { echo "❌ Случайные данные не совпали"; exit 1; }

# 4. Проверка «соли» (16 байт + текст)
echo "  [4/4] Файл с 'солью'..."
head -c 16 /dev/urandom > "$TMP_DIR/salt.bin"
echo "Hello, Arena!" >> "$TMP_DIR/salt.bin"
curl -sf "${AUTH[@]}" -X POST -H "Content-Type: application/octet-stream" --data-binary "@$TMP_DIR/salt.bin" "$URL/v1/compress" -o "$TMP_DIR/salt.arc" || { echo "❌ Ошибка сжатия соли"; exit 1; }
curl -sf "${AUTH[@]}" -X POST -H "Content-Type: application/octet-stream" --data-binary "@$TMP_DIR/salt.arc" "$URL/v1/decompress" -o "$TMP_DIR/salt.out" || { echo "❌ Ошибка распаковки соли"; exit 1; }
cmp -s "$TMP_DIR/salt.bin" "$TMP_DIR/salt.out" || { echo "❌ Файл с солью не совпал"; exit 1; }

echo "✅ Все edge-cases пройдены!"
