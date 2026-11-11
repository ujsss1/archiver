// Доступ к элементу массива без проверки границ.
// Использовать ТОЛЬКО там, где индекс заведомо в пределах массива (взят по маске или из таблицы нужного размера):
// при ошибке здесь не будет исключения, программа молча прочитает или испортит чужую память.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Контрольная сумма CRC32: если в архиве изменится хоть один бит, сумма почти наверняка станет другой
static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var value in data)
        {
            crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    // Для каждого байта заранее считаем, как он «перемешивает» сумму (стандартный многочлен 0xEDB88320)
    private static uint[] BuildTable()
    {
        var table = new uint[256];

        for (var i = 0u; i < 256; i++)
        {
            var value = i;

            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}

static class Fast
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T At<T>(T[] array, int index)
    {
        // В отладочной сборке проверка остаётся, в Release её нет
        System.Diagnostics.Debug.Assert((uint)index < (uint)array.Length);
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), (nint)(uint)index);
    }
}
