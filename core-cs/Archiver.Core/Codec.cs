// Арифметический кодер и простые контекстные модели.

// Модель предсказывает следующий бит: вероятность единицы от 1 до 65535 (из 65536).
interface IModel
{
    int Predict();
    void Update(int bit);
}

// ---------- Блок 1. Арифметический кодер ----------
// Держим отрезок [low, high]. Каждый бит сужает его пропорционально вероятности:
// чем увереннее и вернее предсказание, тем меньше сужение и тем меньше места занимает бит.
static class Codec
{
    public static byte[] Compress(byte[] data, IModel model)
    {
        var output = new byte[data.Length + data.Length / 8 + 1024];
        var size = 0;

        // В начале архива записываем исходный размер, чтобы распаковка знала, когда остановиться.
        // По 7 бит в байте, старший бит = «дальше есть ещё байт»: до 127 Б — 1 байт, до 16 КБ — 2, до 256 МБ — 4
        var remaining = (ulong)data.Length;

        while (remaining >= 0x80)
        {
            output[size++] = (byte)(remaining | 0x80);
            remaining >>= 7;
        }

        output[size++] = (byte)remaining;

        var low = 0u;
        var high = 0xFFFFFFFFu;

        foreach (var value in data)
        {
            for (var shift = 7; shift >= 0; shift--)
            {
                var bit = (value >> shift) & 1;
                var middle = low + (uint)(((ulong)(high - low) * (ulong)model.Predict()) >> 16);

                // Без if: при bit = 1 маска из одних единиц и high = middle, при bit = 0 маска нулевая и low = middle + 1.
                // Процессору не надо угадывать, куда пойдёт условие, а угадывает он здесь часто неверно.
                var mask = 0u - (uint)bit;
                high = (middle & mask) | (high & ~mask);
                low = (low & mask) | ((middle + 1) & ~mask);

                model.Update(bit);

                // Старшие байты границ совпали: они уже точно известны, отправляем их в архив
                while (((low ^ high) & 0xFF000000u) == 0)
                {
                    if (size == output.Length)
                    {
                        Array.Resize(ref output, output.Length * 2);
                    }

                    output[size++] = (byte)(high >> 24);
                    low <<= 8;
                    high = (high << 8) | 255;
                }
            }
        }

        // Дописываем нижнюю границу целиком, этого хватит, чтобы распаковка попала в отрезок
        if (size + 4 > output.Length)
        {
            Array.Resize(ref output, output.Length + 4);
        }

        for (var i = 0; i < 4; i++)
        {
            output[size++] = (byte)(low >> 24);
            low <<= 8;
        }

        Array.Resize(ref output, size);
        return output;
    }

    public static byte[] Decompress(byte[] archive, IModel model)
    {
        // Битый архив может «заявить» любой размер: не даём ему выделить гигантский массив
        const long MaxLength = 1L << 30;

        var length = 0L;
        var position = 0;

        for (var shift = 0; ; shift += 7)
        {
            if (position == archive.Length || shift > 35)
            {
                throw new InvalidDataException("Архив обрезан или размер записан неверно");
            }

            var part = archive[position++];
            length |= (long)(part & 0x7F) << shift;

            if (part < 0x80)
            {
                break;
            }
        }

        if (length > MaxLength)
        {
            throw new InvalidDataException($"Неправдоподобный размер файла в архиве: {length}");
        }

        var result = new byte[length];

        var low = 0u;
        var high = 0xFFFFFFFFu;
        var code = 0u;

        for (var i = 0; i < 4; i++)
        {
            code = (code << 8) | (position < archive.Length ? archive[position++] : (byte)0);
        }

        for (var index = 0L; index < length; index++)
        {
            var value = 0;

            for (var shift = 0; shift < 8; shift++)
            {
                var middle = low + (uint)(((ulong)(high - low) * (ulong)model.Predict()) >> 16);
                var bit = code <= middle ? 1 : 0;

                var mask = 0u - (uint)bit;
                high = (middle & mask) | (high & ~mask);
                low = (low & mask) | ((middle + 1) & ~mask);

                model.Update(bit);
                value = (value << 1) | bit;

                while (((low ^ high) & 0xFF000000u) == 0)
                {
                    low <<= 8;
                    high = (high << 8) | 255;
                    code = (code << 8) | (position < archive.Length ? archive[position++] : (byte)0);
                }
            }

            result[index] = (byte)value;
        }

        return result;
    }
}

// ---------- Блок 2. Простая модель: частоты байтов без контекста ----------
sealed class Order0Model : IModel
{
    private readonly ushort[] probabilities = new ushort[256];
    private int partialByte = 1; // уже известные биты текущего байта с ведущей единицей

    public Order0Model()
    {
        Array.Fill(probabilities, (ushort)32768);
    }

    public int Predict()
    {
        return Math.Clamp((int)probabilities[partialByte], 32, 65535 - 32);
    }

    public void Update(int bit)
    {
        var probability = (int)probabilities[partialByte];
        probability += bit == 1 ? (65535 - probability) >> 5 : -(probability >> 5);
        probabilities[partialByte] = (ushort)probability;

        partialByte = partialByte * 2 + bit;

        if (partialByte >= 256)
        {
            partialByte = 1;
        }
    }
}

// ---------- Блок 3. Контекстная модель со смешиванием ----------
// Несколько предсказателей смотрят на разное число предыдущих байтов (1, 2, 3, 4, 6, плюс без контекста).
// Маленькая нейросеть-смеситель учится на лету, кому из них сейчас верить.
sealed class ContextMixingModel : IModel
{
    private const int HashedCount = 5;
    private const int InputCount = HashedCount + 1;
    private const int TableBits = 23;

    // Сколько младших битов истории берёт каждый контекст
    private static readonly ulong[] ContextMasks =
    {
        0xFFUL, 0xFFFFUL, 0xFFFFFFUL, 0xFFFFFFFFUL, 0xFFFFFFFFFFFFUL,
    };

    private static readonly int[] StretchTable = BuildStretchTable();
    private static readonly int[] SquashTable = BuildSquashTable();

    // Все таблицы контекстов в одном массиве: у контекста i участок с (i << TableBits)
    private readonly ushort[] tables = new ushort[HashedCount << TableBits];
    private readonly ushort[] order0 = new ushort[256];
    private readonly uint[] hashes = new uint[HashedCount];
    private readonly int[] nibbleStarts = new int[HashedCount];
    private readonly int[] slots = new int[HashedCount];
    private readonly int[] inputs = new int[InputCount];
    private readonly int[] weights = new int[256 * InputCount];

    private ulong history;
    private int partialByte = 1;
    private int partialNibble = 1; // известные биты текущего полубайта с ведущей единицей: от 1 до 15
    private int bitCount;
    private int mixedProbability = 2048; // 12 бит

    public ContextMixingModel()
    {
        Array.Fill(tables, (ushort)32768);
        Array.Fill(order0, (ushort)32768);
        Array.Fill(weights, 16000);
        FindNibbleStarts();
    }

    public int Predict()
    {
        // Fast.At — доступ без проверки границ: индексы берутся по маске таблиц.
        // Счётчики одного полубайта лежат рядом (блок из 16), поэтому в память ходим раз на 4 бита, а не на каждый
        for (var i = 0; i < HashedCount; i++)
        {
            var slot = Fast.At(nibbleStarts, i) + partialNibble;
            Fast.At(slots, i) = slot;
            Fast.At(inputs, i) = Fast.At(StretchTable, Fast.At(tables, slot) >> 4);
        }

        Fast.At(inputs, HashedCount) = Fast.At(StretchTable, Fast.At(order0, partialByte) >> 4);

        // Смеситель: взвешенная сумма предсказаний, набор весов зависит от известных битов байта
        var weightStart = partialByte * InputCount;
        var sum = 0L;

        for (var i = 0; i < InputCount; i++)
        {
            sum += (long)Fast.At(weights, weightStart + i) * Fast.At(inputs, i);
        }

        var stretched = (int)Math.Clamp(sum >> 16, -2047L, 2047L);
        mixedProbability = Math.Clamp(Fast.At(SquashTable, stretched + 2048), 1, 4095);
        return mixedProbability << 4;
    }

    public void Update(int bit)
    {
        // Обучение смесителя: сдвигаем веса в сторону тех, кто оказался прав
        var error = (bit << 12) - mixedProbability;
        var weightStart = partialByte * InputCount;

        for (var i = 0; i < InputCount; i++)
        {
            Fast.At(weights, weightStart + i) += (Fast.At(inputs, i) * error) >> 10;
        }

        for (var i = 0; i < HashedCount; i++)
        {
            ref var cell = ref Fast.At(tables, Fast.At(slots, i));
            cell = (ushort)(bit == 1 ? cell + ((65535 - cell) >> 4) : cell - (cell >> 4));
        }

        ref var zeroCell = ref Fast.At(order0, partialByte);
        zeroCell = (ushort)(bit == 1 ? zeroCell + ((65535 - zeroCell) >> 5) : zeroCell - (zeroCell >> 5));

        partialByte = partialByte * 2 + bit;
        partialNibble = partialNibble * 2 + bit;
        bitCount++;

        if (bitCount == 8)
        {
            history = (history << 8) | (byte)partialByte;
            partialByte = 1;
            partialNibble = 1;
            bitCount = 0;

            for (var i = 0; i < HashedCount; i++)
            {
                var hash = ((history & ContextMasks[i]) + (ulong)i + 1) * 0x9E3779B97F4A7C15UL;
                hashes[i] = (uint)(hash >> 32);
            }

            FindNibbleStarts();
        }
        else if (bitCount == 4)
        {
            partialNibble = 1;
            FindNibbleStarts();
        }
    }

    // Начало блока из 16 счётчиков для текущего полубайта: зависит от контекста и уже известных битов байта
    private void FindNibbleStarts()
    {
        var mix = (uint)partialByte * 0x2F0B4C87u;

        for (var i = 0; i < HashedCount; i++)
        {
            var slot = (int)(((hashes[i] ^ mix) * 0x9E3779B1u) >> (32 - TableBits)) & ~15;
            nibbleStarts[i] = (i << TableBits) + slot;
        }
    }

    // stretch(p) = ln(p / (1 - p)): переводит вероятность в удобную для смешивания шкалу
    private static int[] BuildStretchTable()
    {
        var table = new int[4096];

        for (var i = 0; i < table.Length; i++)
        {
            var probability = (i + 0.5) / 4096;
            table[i] = (int)Math.Clamp(Math.Round(Math.Log(probability / (1 - probability)) * 256), -2047, 2047);
        }

        return table;
    }

    // squash — обратное преобразование: из шкалы смешивания обратно в вероятность
    private static int[] BuildSquashTable()
    {
        var table = new int[4096];

        for (var i = 0; i < table.Length; i++)
        {
            table[i] = (int)(4096 / (1 + Math.Exp(-(i - 2048) / 256.0)));
        }

        return table;
    }
}
