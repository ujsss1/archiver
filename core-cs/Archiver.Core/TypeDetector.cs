// Определение типа файла и выбор метода сжатия.
// Тип подсказывает, что стоит попробовать, а окончательно решает пробное сжатие кусочка файла:
// подпись может обмануть, а размер результата нет.

enum FileKind
{
    Empty,
    Compressed, // внутри уже сжато: PNG, JPEG, ZIP и подобные
    Csv,
    Json,
    Text,
    Binary,
}

static class TypeDetector
{
    // В раунде арены к файлу спереди добавляют 16 байт соли, поэтому ищем подпись с запасом
    private const int SignatureSearchBytes = 64;
    private const int SaltBytes = 16;
    private const int AnalysisBytes = 64 * 1024;

    private static readonly byte[][] CompressedSignatures =
    {
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, // PNG
        new byte[] { 0xFF, 0xD8, 0xFF }, // JPEG
        new byte[] { 0x47, 0x49, 0x46, 0x38 }, // GIF
        new byte[] { 0x50, 0x4B, 0x03, 0x04 }, // ZIP
        new byte[] { 0x1F, 0x8B, 0x08 }, // GZIP
        new byte[] { 0x42, 0x5A, 0x68 }, // BZIP2
        new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }, // XZ
        new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, // 7Z
        new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 }, // RAR
        new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, // ZSTD
    };

    public static FileKind Detect(byte[] data)
    {
        if (data.Length == 0)
        {
            return FileKind.Empty;
        }

        if (HasCompressedSignature(data))
        {
            return FileKind.Compressed;
        }

        // Содержимое смотрим после возможной соли, иначе случайные байты испортят подсчёт
        var start = data.Length > SaltBytes ? SaltBytes : 0;
        var sample = data.AsSpan(start, Math.Min(data.Length - start, AnalysisBytes));

        if (!LooksLikeText(sample))
        {
            return FileKind.Binary;
        }

        // Соль может быть, а может и не быть, поэтому JSON проверяем с обоих мест
        var fromStart = data.AsSpan(0, Math.Min(data.Length, AnalysisBytes));

        if (LooksLikeJson(fromStart) || LooksLikeJson(sample))
        {
            return FileKind.Json;
        }

        return LooksLikeCsv(sample) ? FileKind.Csv : FileKind.Text;
    }

    private static bool HasCompressedSignature(byte[] data)
    {
        var searchArea = data.AsSpan(0, Math.Min(data.Length, SignatureSearchBytes));

        foreach (var signature in CompressedSignatures)
        {
            if (searchArea.IndexOf(signature) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    // В тексте почти нет управляющих символов, кроме табуляции и переводов строк
    private static bool LooksLikeText(ReadOnlySpan<byte> sample)
    {
        var controlCount = 0;

        foreach (var value in sample)
        {
            if (value < 9 || (value > 13 && value < 32))
            {
                controlCount++;
            }
        }

        return sample.Length > 0 && controlCount * 100 < sample.Length;
    }

    private static bool LooksLikeJson(ReadOnlySpan<byte> sample)
    {
        var index = 0;

        while (index < sample.Length && (sample[index] == ' ' || (sample[index] >= 9 && sample[index] <= 13)))
        {
            index++;
        }

        if (index == sample.Length || (sample[index] != '{' && sample[index] != '['))
        {
            return false;
        }

        // Ключи JSON выглядят как "ключ": значение
        return sample.IndexOf("\":"u8) >= 0;
    }

    // В таблице в каждой строке одинаковое ненулевое число разделителей
    private static bool LooksLikeCsv(ReadOnlySpan<byte> sample)
    {
        foreach (var separator in new[] { (byte)',', (byte)';', (byte)'\t' })
        {
            if (HasSameSeparatorCount(sample, separator))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSameSeparatorCount(ReadOnlySpan<byte> sample, byte separator)
    {
        // Первую строку не считаем: кусочек мог начаться с её середины
        var firstLineEnd = sample.IndexOf((byte)'\n');

        if (firstLineEnd < 0)
        {
            return false;
        }

        var rest = sample[(firstLineEnd + 1)..];
        var expected = -1;
        var lines = 0;

        while (true)
        {
            var lineEnd = rest.IndexOf((byte)'\n');

            if (lineEnd < 0)
            {
                break; // последняя строка кусочка могла обрезаться, её не считаем
            }

            var count = rest[..lineEnd].Count(separator);

            if (count == 0 || (expected >= 0 && count != expected))
            {
                return false;
            }

            expected = count;
            lines++;
            rest = rest[(lineEnd + 1)..];
        }

        return lines >= 5;
    }
}
