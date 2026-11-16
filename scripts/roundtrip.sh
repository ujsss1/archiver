#!/usr/bin/env bash
# Проверка ядра через консольную программу: сжал → распаковал → сравнил с исходником.
# Проверяет то же, что арена: побайтовое совпадение, размер архива и ответ на битый архив.
#
# Запуск:  scripts/roundtrip.sh <путь к программе archiver> [папка для временных файлов]
# Пример:  scripts/roundtrip.sh out/archiver
#
# Код выхода: 0 — всё прошло, 1 — есть ошибки.

set -u

if [ $# -lt 1 ]; then
    echo "Использование: $0 <путь к archiver> [рабочая папка]"
    exit 1
fi

ARCHIVER="$1"
WORK="${2:-$(mktemp -d)}"
mkdir -p "$WORK"

failures=0

# ---------- Тестовые файлы (генерируем, чтобы не хранить двоичные файлы в репозитории) ----------

: > "$WORK/empty.bin"                                   # пустой файл
printf 'A' > "$WORK/one-byte.bin"                       # один байт
head -c 1000000 /dev/zero > "$WORK/zeros.bin"           # нулевые байты, 1 МБ
head -c 65536 /dev/urandom > "$WORK/random.bin"         # случайные байты (не сжимаются), 64 КБ

# Текст, ~2,9 МБ
awk 'BEGIN { for (i = 1; i <= 20000; i++) printf "Строка %d: съешь же ещё этих мягких французских булок, да выпей чаю. The quick brown fox.\n", i }' \
    > "$WORK/text.txt"

# Таблица CSV, ~0,7 МБ
awk 'BEGIN { srand(1); print "id,city,amount,date"; for (i = 1; i <= 20000; i++) printf "%d,Город%d,%d.%02d,2026-%02d-%02d\n", i, i % 50, int(rand() * 10000), i % 100, i % 12 + 1, i % 28 + 1 }' \
    > "$WORK/table.csv"

# ---------- Сжатие и распаковка каждого файла ----------

# Заголовок выровнен вручную: printf считает ширину в байтах, а русская буква занимает 2 байта
echo "файл               размер      архив     коэф  результат"

for file in empty.bin one-byte.bin zeros.bin random.bin text.txt table.csv; do
    src="$WORK/$file"
    arc="$WORK/$file.arc"
    out="$WORK/$file.out"

    if ! "$ARCHIVER" compress "$src" "$arc" 2>/dev/null; then
        echo "$file: ОШИБКА — сжатие завершилось с ошибкой"
        failures=$((failures + 1))
        continue
    fi

    if ! "$ARCHIVER" decompress "$arc" "$out" 2>/dev/null; then
        echo "$file: ОШИБКА — распаковка завершилась с ошибкой"
        failures=$((failures + 1))
        continue
    fi

    size=$(wc -c < "$src")
    arc_size=$(wc -c < "$arc")
    max_size=$((2 * size + 1024))    # правило арены: архив не больше 2 × размер + 1024 байт

    if ! cmp -s "$src" "$out"; then
        result="ОШИБКА — распакованный файл не совпал с исходным"
        failures=$((failures + 1))
    elif [ "$arc_size" -gt "$max_size" ]; then
        result="ОШИБКА — архив больше $max_size байт"
        failures=$((failures + 1))
    else
        result="ok"
    fi

    # Коэффициент считаем через awk: в bash нет дробных чисел
    ratio=$(awk -v a="$size" -v b="$arc_size" 'BEGIN { if (b > 0) printf "%.2f", a / b; else print "-" }')
    printf "%-14s %10s %10s %8s  %s\n" "$file" "$size" "$arc_size" "$ratio" "$result"
done

# ---------- Битый архив: программа должна вернуть код 2 (сервис ответит 4xx), а не упасть ----------

head -c 5000 /dev/urandom > "$WORK/bad.arc"
"$ARCHIVER" decompress "$WORK/bad.arc" "$WORK/bad.out" 2>/dev/null
code=$?

if [ "$code" -eq 2 ]; then
    echo "битый архив: ok (код выхода 2)"
else
    echo "битый архив: ОШИБКА — код выхода $code, ожидался 2"
    failures=$((failures + 1))
fi

# ---------- Итог ----------

echo
if [ "$failures" -eq 0 ]; then
    echo "Все проверки пройдены"
    exit 0
fi

echo "Ошибок: $failures"
exit 1
