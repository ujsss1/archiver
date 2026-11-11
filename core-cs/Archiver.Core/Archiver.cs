// Публичный интерфейс ядра архиватора: всё, что нужно сервису, — ArchiveCodec.Compress и ArchiveCodec.Decompress.
//
// Формат архива: [подпись + метод, 1 байт][CRC32 исходного файла, 4 байта][данные метода]
// Старшие 4 бита первого байта — подпись нашего формата (A = версия 1), младшие 4 бита — номер метода (0–15).
// По подписи отличаем свой архив от чужих данных, по CRC32 — целый архив от повреждённого.

namespace Archiver;

// Настройки сжатия. По умолчанию всё включено, отключать нужно только для замеров «до/после»
public sealed record CompressOptions(bool UseFilters = true)
{
    public static CompressOptions Default { get; } = new();
}

// Что выбрал архиватор: для таблиц замеров и журнала сервиса
public readonly record struct CompressReport(string Kind, string Method, double SampleRatio);

public static class ArchiveCodec
{
    private const byte Signature = 0xA0;
    private const byte SignatureMask = 0xF0;
    private const int HeaderBytes = 5;

    private const byte StoreMethod = 0;
    private const byte MatchMethod = 1;
    private const byte ContextMethod = 2;
    private const byte MatchX86Method = 3; // фильтр адресов x86 + CM match
    private const byte ContextCsvMethod = 4; // столбцы CSV + CM 1-6
    private const byte MatchCsvMethod = 5; // столбцы CSV + CM match

    private const int SamplePartBytes = 85 * 1024;
    private const double MinUsefulRatio = 1.005; // слабее этого сжатие не окупает потраченное время
    private const double CompressedMinUsefulRatio = 1.04; // подобрано всего по двум картинкам, предварительно

    public static byte[] Compress(byte[] data)
    {
        return Compress(data, CompressOptions.Default, out _);
    }

    public static byte[] Compress(byte[] data, CompressOptions options, out CompressReport report)
    {
        var kind = TypeDetector.Detect(data);

        if (kind == FileKind.Empty)
        {
            report = new CompressReport(kind.ToString(), MethodName(StoreMethod), 0);
            return WithHeader(StoreMethod, data, data);
        }

        // Кандидаты: метод + данные, которые он будет сжимать (исходные или после фильтра).
        // Уже сжатым форматам старая модель не нужна: проверяем только, есть ли смысл сжимать вообще.
        var candidates = new List<(byte Method, byte[] Data)>();

        switch (kind)
        {
            case FileKind.Compressed:
                candidates.Add((MatchMethod, data));
                break;

            case FileKind.Csv:
                candidates.Add((ContextMethod, data));
                var columns = options.UseFilters ? CsvColumns.TryEncode(data) : null;

                if (columns != null)
                {
                    candidates.Add((ContextCsvMethod, columns));
                    candidates.Add((MatchCsvMethod, columns));
                }

                break;

            case FileKind.Binary:
                candidates.Add((MatchMethod, data));
                candidates.Add((ContextMethod, data));

                if (options.UseFilters)
                {
                    var filtered = X86Filter.Encode(data);

                    // Страховка: используем фильтр, только если он точно обратим на этом файле
                    if (X86Filter.Decode(filtered).AsSpan().SequenceEqual(data))
                    {
                        candidates.Add((MatchX86Method, filtered));
                    }
                }

                break;

            default:
                candidates.Add((MatchMethod, data));
                candidates.Add((ContextMethod, data));
                break;
        }

        // Пробуем все варианты на кусочке одновременно, каждый на своём ядре: они друг от друга не зависят
        var samples = new byte[candidates.Count][];
        var probes = new byte[candidates.Count][];

        Parallel.For(0, candidates.Count, i =>
        {
            samples[i] = TakeSample(candidates[i].Data);
            probes[i] = Codec.Compress(samples[i], CreateModel(candidates[i].Method));
        });

        // Победителя выбираем по порядку: при равенстве выигрывает тот, кто раньше в списке.
        // Так результат не зависит от того, какое ядро закончило первым.
        var best = 0;
        var bestRatio = 0.0;

        for (var i = 0; i < candidates.Count; i++)
        {
            // Длина данных после фильтра может отличаться, поэтому сравниваем коэффициенты
            var ratio = (double)samples[i].Length / probes[i].Length;

            if (ratio > bestRatio)
            {
                best = i;
                bestRatio = ratio;
            }
        }

        var (bestMethod, bestData) = candidates[best];

        // Проба на уже сжатых форматах завышает результат (PNG: проба 1,03, целый файл 1,002)
        if (bestRatio < (kind == FileKind.Compressed ? CompressedMinUsefulRatio : MinUsefulRatio))
        {
            report = new CompressReport(kind.ToString(), MethodName(StoreMethod), bestRatio);
            return WithHeader(StoreMethod, data, data);
        }

        // Если кусочек и есть весь файл, сжимать второй раз незачем
        var isWholeFile = samples[best].Length == bestData.Length;
        var payload = isWholeFile ? probes[best] : Codec.Compress(bestData, CreateModel(bestMethod));

        // Страховка: архив не должен быть больше исходника (лимит арены — 2 × размер + 1024)
        var method = payload.Length < data.Length ? bestMethod : StoreMethod;
        report = new CompressReport(kind.ToString(), MethodName(method), bestRatio);
        return method == StoreMethod ? WithHeader(StoreMethod, data, data) : WithHeader(method, data, payload);
    }

    // Любая проблема с архивом — InvalidDataException: сервис должен ответить на неё 4xx и не упасть
    public static byte[] Decompress(byte[] archive)
    {
        if (archive.Length < HeaderBytes || (archive[0] & SignatureMask) != Signature)
        {
            throw new InvalidDataException("Это не архив нашего формата");
        }

        var method = (byte)(archive[0] & ~SignatureMask);

        if (method > MatchCsvMethod)
        {
            throw new InvalidDataException($"Неизвестный метод: {method}");
        }

        var expectedCrc = BitConverter.ToUInt32(archive, 1);
        var payload = archive[HeaderBytes..];
        byte[] data;

        try
        {
            data = method == StoreMethod ? payload : Codec.Decompress(payload, CreateModel(method));

            // Отменяем фильтр, если он был
            data = method switch
            {
                MatchX86Method => X86Filter.Decode(data),
                ContextCsvMethod or MatchCsvMethod => CsvColumns.Decode(data),
                _ => data,
            };
        }
        catch (Exception error) when (error is not InvalidDataException)
        {
            // Битые данные могут сломать распаковку где угодно: превращаем это в понятную ошибку
            throw new InvalidDataException($"Архив повреждён: {error.Message}", error);
        }

        if (Crc32.Compute(data) != expectedCrc)
        {
            throw new InvalidDataException("Архив повреждён: контрольная сумма не совпала");
        }

        return data;
    }

    // Название метода, которым сжат архив (для журнала и таблиц)
    public static string MethodName(byte[] archive)
    {
        return archive.Length == 0 ? "неизвестно" : MethodName((byte)(archive[0] & ~SignatureMask));
    }

    private static string MethodName(byte method)
    {
        return method switch
        {
            StoreMethod => "без сжатия",
            MatchMethod => "CM match",
            ContextMethod => "CM 1-6",
            MatchX86Method => "x86 + CM match",
            ContextCsvMethod => "столбцы + CM 1-6",
            MatchCsvMethod => "столбцы + CM match",
            _ => "неизвестно",
        };
    }

    private static IModel CreateModel(byte method)
    {
        return method is MatchMethod or MatchX86Method or MatchCsvMethod ? new MatchMixModel() : new ContextMixingModel();
    }

    private static byte[] WithHeader(byte method, byte[] original, byte[] payload)
    {
        var result = new byte[HeaderBytes + payload.Length];
        result[0] = (byte)(Signature | method);
        BitConverter.TryWriteBytes(result.AsSpan(1), Crc32.Compute(original));
        payload.CopyTo(result, HeaderBytes);
        return result;
    }

    // Три кусочка: начало, середина и конец. Так проще заметить, если файл неоднороден
    private static byte[] TakeSample(byte[] data)
    {
        if (data.Length <= SamplePartBytes * 3)
        {
            return data;
        }

        var sample = new byte[SamplePartBytes * 3];
        data.AsSpan(0, SamplePartBytes).CopyTo(sample);
        data.AsSpan(data.Length / 2 - SamplePartBytes / 2, SamplePartBytes).CopyTo(sample.AsSpan(SamplePartBytes));
        data.AsSpan(data.Length - SamplePartBytes).CopyTo(sample.AsSpan(SamplePartBytes * 2));
        return sample;
    }
}
