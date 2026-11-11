// Улучшенная контекстная модель. Отличия от ContextMixingModel:
// 1) модель совпадений находит длинные повторы, которых не видят короткие контексты;
// 2) счётчики учатся быстро на редких контекстах и медленно на частых;
// 3) данные одного полубайта лежат рядом в памяти, поэтому процессор реже ждёт память.

sealed class MatchMixModel : IModel
{
    private const int HashedCount = 5;
    private const int InputCount = HashedCount + 2; // контексты + без контекста + совпадения
    private const int TableBits = 22;
    private const int MatchTableBits = 22;
    private const int MinMatchBytes = 6; // по скольким байтам ищем прошлое вхождение
    private const int MaxMatchLength = 65535;
    private const uint CounterStart = 0x80000000u; // вероятность 1/2, счётчик наблюдений 0
    private const int CounterLimit = 127;

    private static readonly ulong[] ContextMasks =
    {
        0xFFUL, 0xFFFFUL, 0xFFFFFFUL, 0xFFFFFFFFUL, 0xFFFFFFFFFFFFUL,
    };

    private static readonly int[] StretchTable = BuildStretchTable();
    private static readonly int[] SquashTable = BuildSquashTable();
    private static readonly int[] RateTable = BuildRateTable();

    // Все контекстные счётчики в одном массиве: у каждого контекста свой участок
    private readonly uint[] counters = new uint[HashedCount << TableBits];
    private readonly uint[] order0 = new uint[256];
    private readonly uint[] matchCounters = new uint[64 * 2];
    private readonly uint[] hashes = new uint[HashedCount];
    private readonly int[] nibbleStarts = new int[HashedCount];
    private readonly int[] slots = new int[HashedCount];
    private readonly int[] inputs = new int[InputCount];
    private readonly int[] weights = new int[4 * 256 * InputCount];
    private readonly int[] matchTable = new int[1 << MatchTableBits];

    private byte[] buffer = new byte[1 << 20];
    private int position;
    private int matchPointer;
    private int matchLength;
    private int matchSlot = -1;

    private ulong history;
    private int partialByte = 1;
    private int partialNibble = 1;
    private int bitCount;
    private int mixedProbability = 2048;
    private int weightStart;

    public MatchMixModel()
    {
        Array.Fill(counters, CounterStart);
        Array.Fill(order0, CounterStart);
        Array.Fill(matchCounters, CounterStart);
        Array.Fill(weights, 14000);
        FindNibbleStarts();
    }

    public int Predict()
    {
        // Здесь и ниже Fast.At — доступ без проверки границ: все индексы берутся по маске таблиц
        for (var i = 0; i < HashedCount; i++)
        {
            var slot = Fast.At(nibbleStarts, i) + partialNibble;
            Fast.At(slots, i) = slot;
            Fast.At(inputs, i) = Fast.At(StretchTable, (int)(Fast.At(counters, slot) >> 20));
        }

        Fast.At(inputs, HashedCount) = Fast.At(StretchTable, (int)(Fast.At(order0, partialByte) >> 20));
        Fast.At(inputs, HashedCount + 1) = PredictFromMatch();

        var lengthGroup = matchLength == 0 ? 0 : matchLength < 16 ? 1 : matchLength < 32 ? 2 : 3;
        weightStart = (lengthGroup * 256 + partialByte) * InputCount;

        var sum = 0L;

        for (var i = 0; i < InputCount; i++)
        {
            sum += (long)Fast.At(weights, weightStart + i) * Fast.At(inputs, i);
        }

        var stretched = (int)Math.Clamp(sum >> 16, -2047L, 2047L);
        mixedProbability = Math.Clamp(Fast.At(SquashTable, stretched + 2048), 1, 4095);
        return mixedProbability << 4;
    }

    private int PredictFromMatch()
    {
        matchSlot = -1;

        if (matchLength == 0)
        {
            return 0;
        }

        var expectedByte = buffer[matchPointer] | 256;

        // Уже известные биты текущего байта разошлись с повтором: совпадение закончилось
        if ((expectedByte >> (8 - bitCount)) != partialByte)
        {
            matchLength = 0;
            return 0;
        }

        var expectedBit = (expectedByte >> (7 - bitCount)) & 1;
        matchSlot = MatchLengthGroup(matchLength) * 2 + expectedBit;
        return Fast.At(StretchTable, (int)(Fast.At(matchCounters, matchSlot) >> 20));
    }

    // Группы длин: короткие различаем точно, длинные укрупняем
    private static int MatchLengthGroup(int length)
    {
        if (length < 32)
        {
            return length;
        }

        return Math.Min(32 + ((length - 32) >> 5), 63);
    }

    public void Update(int bit)
    {
        var error = (bit << 12) - mixedProbability;

        for (var i = 0; i < InputCount; i++)
        {
            Fast.At(weights, weightStart + i) += (Fast.At(inputs, i) * error) >> 10;
        }

        for (var i = 0; i < HashedCount; i++)
        {
            UpdateCounter(ref Fast.At(counters, Fast.At(slots, i)), bit);
        }

        UpdateCounter(ref Fast.At(order0, partialByte), bit);

        if (matchSlot >= 0)
        {
            UpdateCounter(ref Fast.At(matchCounters, matchSlot), bit);
        }

        partialByte = partialByte * 2 + bit;
        partialNibble = partialNibble * 2 + bit;
        bitCount++;

        if (bitCount == 8)
        {
            FinishByte((byte)partialByte);
            partialByte = 1;
            partialNibble = 1;
            bitCount = 0;
            FindNibbleStarts();
        }
        else if (bitCount == 4)
        {
            partialNibble = 1;
            FindNibbleStarts();
        }
    }

    // В старших 22 битах вероятность единицы, в младших 10 число наблюдений.
    // Чем больше наблюдений, тем меньше шаг: 1, 1/2, 1/3... до предела CounterLimit.
    private static void UpdateCounter(ref uint counter, int bit)
    {
        var count = (int)(counter & 1023);
        var probability = (int)(counter >> 10);

        if (count < CounterLimit)
        {
            counter++;
        }

        counter += (uint)((((bit << 22) - probability) >> 3) * Fast.At(RateTable, count)) & 0xFFFFFC00u;
    }

    private void FinishByte(byte value)
    {
        if (position == buffer.Length)
        {
            Array.Resize(ref buffer, buffer.Length * 2);
        }

        buffer[position++] = value;
        history = (history << 8) | value;

        for (var i = 0; i < HashedCount; i++)
        {
            var hash = ((history & ContextMasks[i]) + (ulong)i + 1) * 0x9E3779B97F4A7C15UL;
            hashes[i] = (uint)(hash >> 32);
        }

        // Продолжаем текущий повтор или ищем новый по последним MinMatchBytes байтам
        if (matchLength > 0 && buffer[matchPointer] == value)
        {
            matchPointer++;

            if (matchLength < MaxMatchLength)
            {
                matchLength++;
            }
        }
        else
        {
            matchLength = 0;
        }

        if (position < MinMatchBytes)
        {
            return;
        }

        var matchHash = (int)(((history & 0xFFFFFFFFFFFFUL) * 0x9E3779B97F4A7C15UL) >> (64 - MatchTableBits));

        if (matchLength == 0)
        {
            matchPointer = matchTable[matchHash];

            if (matchPointer > 0)
            {
                // Проверяем реальную длину совпадения: разные данные могли дать один хеш
                while (matchLength < 32 && matchPointer - matchLength > 0
                    && buffer[matchPointer - matchLength - 1] == buffer[position - matchLength - 1])
                {
                    matchLength++;
                }

                if (matchLength < MinMatchBytes)
                {
                    matchLength = 0;
                }
            }
        }

        matchTable[matchHash] = position;
    }

    // Один раз на полубайт считаем начало блока из 16 счётчиков: они помещаются в одну строку кэша
    private void FindNibbleStarts()
    {
        var mix = (uint)partialByte * 0x2F0B4C87u;

        for (var i = 0; i < HashedCount; i++)
        {
            var slot = (int)(((hashes[i] ^ mix) * 0x9E3779B1u) >> (32 - TableBits)) & ~15;
            nibbleStarts[i] = (i << TableBits) + slot;
        }
    }

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

    private static int[] BuildSquashTable()
    {
        var table = new int[4096];

        for (var i = 0; i < table.Length; i++)
        {
            table[i] = (int)(4096 / (1 + Math.Exp(-(i - 2048) / 256.0)));
        }

        return table;
    }

    private static int[] BuildRateTable()
    {
        var table = new int[1024];

        for (var i = 0; i < table.Length; i++)
        {
            table[i] = 16384 / (i + i + 3);
        }

        return table;
    }
}
