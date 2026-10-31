"""Проверка архиватора: сжать -> распаковать -> сравнить, и вывести коэффициенты.

Запуск: python check.py файл1 файл2 ...
"""

import sys
import time
from pathlib import Path

from compressor import compress, decompress

METHOD_NAMES = {0: "без сжатия", 1: "zlib", 2: "bz2", 3: "lzma"}

total_before = 0
total_after = 0

print(f"{'файл':<25}{'до':>12}{'после':>12}{'коэф.':>8}  {'метод':<12}{'время':>7}")

for path in sys.argv[1:]:
    original = Path(path).read_bytes()

    start = time.perf_counter()
    packed = compress(original)
    restored = decompress(packed)
    elapsed = time.perf_counter() - start

    # Главная проверка: файл должен вернуться байт в байт
    if restored != original:
        print(f"ОШИБКА: {path} распаковался не так, как был!")
        sys.exit(1)

    total_before += len(original)
    total_after += len(packed)
    ratio = len(original) / len(packed)
    method = METHOD_NAMES[packed[0]]
    name = Path(path).name[:24]
    print(f"{name:<25}{len(original):>12}{len(packed):>12}{ratio:>8.2f}  {method:<12}{elapsed:>6.2f}с")

if total_after > 0:
    print(f"\nИТОГО: {total_before} -> {total_after} байт, коэффициент {total_before / total_after:.2f}")
