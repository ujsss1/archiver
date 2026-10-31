"""Архиватор, версия 0.

Идея: сжать данные несколькими готовыми методами и оставить самый маленький результат.
Формат сжатого файла: [1 байт — номер метода][сжатые данные].
"""

import bz2
import lzma
import zlib

# ---------- Блок 1. Список методов ----------
# Номер метода -> (функция сжатия, функция распаковки).
# Номер записывается в первый байт, поэтому менять номера у существующих методов нельзя:
# иначе старые сжатые файлы перестанут распаковываться.
METHODS = {
    # 0: «без сжатия» — запасной вариант для данных, которые не жмутся (jpg, zip)
    0: (lambda data: data, lambda data: data),
    1: (lambda data: zlib.compress(data, 9), zlib.decompress),
    2: (lambda data: bz2.compress(data, 9), bz2.decompress),
    3: (lambda data: lzma.compress(data, preset=9 | lzma.PRESET_EXTREME), lzma.decompress),
}


# ---------- Блок 2. Сжатие ----------
def compress(data: bytes) -> bytes:
    """Сжимает данные всеми методами и возвращает самый маленький вариант."""
    best_id = 0
    best_payload = data

    for method_id, (compress_fn, _) in METHODS.items():
        payload = compress_fn(data)
        if len(payload) < len(best_payload):
            best_id = method_id
            best_payload = payload

    # Первый байт — номер метода, дальше сами сжатые данные
    return bytes([best_id]) + best_payload


# ---------- Блок 3. Распаковка ----------
def decompress(data: bytes) -> bytes:
    """Читает номер метода из первого байта и распаковывает остальное."""
    if len(data) == 0:
        raise ValueError("Пустые данные: это не файл нашего архиватора")

    method_id = data[0]
    if method_id not in METHODS:
        raise ValueError(f"Неизвестный метод сжатия: {method_id}")

    _, decompress_fn = METHODS[method_id]
    return decompress_fn(data[1:])
