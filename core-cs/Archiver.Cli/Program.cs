// Консольная программа для ядра архиватора. Через неё ядро можно вызвать из сервиса на любом языке.
//
//   archiver compress   <вход> <выход>   сжать файл («-» вместо имени: stdin / stdout)
//   archiver decompress <вход> <выход>   распаковать архив
//   archiver bench      <папка> [--no-filters] [--blocks N]   таблица замеров по всем файлам папки
//   archiver selftest   <файл>           проверка: испорченные архивы должны отклоняться
//
// Коды выхода: 0 — успех, 2 — некорректный архив (сервис отвечает 4xx), 1 — прочие ошибки.

using System.Diagnostics;
using System.Text;
using Archiver;

Console.OutputEncoding = Encoding.UTF8;

try
{
    return args switch
    {
        ["compress", var input, var output] => Run(input, output, CompressWithReport),
        ["decompress", var input, var output] => Run(input, output, ArchiveCodec.Decompress),
        ["bench", var folder, .. var rest] => Bench(folder, ParseOptions(rest)),
        ["selftest", var file] => SelfTest(file),
        _ => Usage(),
    };
}
catch (InvalidDataException error)
{
    Console.Error.WriteLine($"Некорректный архив: {error.Message}");
    return 2;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Ошибка: {error.Message}");
    return 1;
}

static int Run(string input, string output, Func<byte[], byte[]> action)
{
    var result = action(Read(input));

    if (output == "-")
    {
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(result);
    }
    else
    {
        File.WriteAllBytes(output, result);
    }

    return 0;
}

// Отчёт пишем в stderr: результат сжатия идёт в stdout или файл, и отчёт ему не мешает
static byte[] CompressWithReport(byte[] data)
{
    var archive = ArchiveCodec.Compress(data, CompressOptions.Default, out var report);
    Console.Error.WriteLine($"тип: {report.Kind}, метод: {report.Method}, блоков: {report.Blocks}, " +
        $"прогноз распаковки: {report.PredictedSeconds:F1} с, {data.Length} → {archive.Length} байт");
    return archive;
}

static byte[] Read(string input)
{
    if (input != "-")
    {
        return File.ReadAllBytes(input);
    }

    using var stdin = Console.OpenStandardInput();
    using var buffer = new MemoryStream();
    stdin.CopyTo(buffer);
    return buffer.ToArray();
}

static CompressOptions ParseOptions(string[] rest)
{
    var blocksIndex = Array.IndexOf(rest, "--blocks");
    int? forceBlocks = blocksIndex >= 0 && blocksIndex + 1 < rest.Length ? int.Parse(rest[blocksIndex + 1]) : null;
    return new CompressOptions(!rest.Contains("--no-filters"), forceBlocks);
}

static int Bench(string folder, CompressOptions options)
{
    var files = Directory.GetFiles(folder);
    Array.Sort(files, StringComparer.Ordinal);

    Console.WriteLine("файл | тип | метод | проба | блоков | прогноз, с | размер | архив | коэф | сжатие, с | распаковка, с | лимит, с | совпало | статус");

    var totalSize = 0L;
    var totalArchive = 0L;

    foreach (var file in files)
    {
        var data = File.ReadAllBytes(file);
        var limit = 10 + 2 * data.Length / 1_000_000.0; // лимит арены: 10 с + 2 с на МБ

        var timer = Stopwatch.StartNew();
        var archive = ArchiveCodec.Compress(data, options, out var report);
        var compressSeconds = timer.Elapsed.TotalSeconds;

        timer.Restart();
        var restored = ArchiveCodec.Decompress(archive);
        var decompressSeconds = timer.Elapsed.TotalSeconds;

        var same = restored.AsSpan().SequenceEqual(data);
        var inLimit = compressSeconds <= limit && decompressSeconds <= limit;
        var status = !same ? "ОШИБКА" : inLimit ? "ok" : "ПРЕВЫШЕНО";

        totalSize += data.Length;
        totalArchive += archive.Length;

        Console.WriteLine($"{Path.GetFileName(file)} | {report.Kind} | {report.Method} | {report.SampleRatio:F3} | {report.Blocks} | {report.PredictedSeconds:F1} | {data.Length} | {archive.Length} | " +
            $"{(double)data.Length / archive.Length:F2} | {compressSeconds:F1} | {decompressSeconds:F1} | {limit:F0} | {same} | {status}");
    }

    Console.WriteLine($"Итог: {(double)totalSize / totalArchive:F3}");
    return 0;
}

// Каждый испорченный архив должен дать InvalidDataException, а не мусор и не падение
static int SelfTest(string file)
{
    var original = File.ReadAllBytes(file);
    var good = ArchiveCodec.Compress(original);
    var randomBytes = new byte[5000];
    new Random(1).NextBytes(randomBytes);

    var flipped = (byte[])good.Clone();
    flipped[flipped.Length / 2] ^= 0x10;

    var badArchives = new (string Name, byte[] Archive)[]
    {
        ("пустой", []),
        ("случайные байты", randomBytes),
        ("обрезан до половины", good[..(good.Length / 2)]),
        ("один бит в середине", flipped),
        ("чужая подпись", [(byte)((good[0] & 0x0F) | 0xB0), .. good[1..]]),
        ("неизвестный метод", [(byte)((good[0] & 0xF0) | 0x0F), .. good[1..]]),
    };

    var failures = 0;

    foreach (var (name, archive) in badArchives)
    {
        try
        {
            ArchiveCodec.Decompress(archive);
            Console.WriteLine($"{name}: ПРОПУЩЕН, распаковался без ошибки!");
            failures++;
        }
        catch (InvalidDataException error)
        {
            Console.WriteLine($"{name}: отклонён ({error.Message})");
        }
        catch (Exception error)
        {
            Console.WriteLine($"{name}: УПАЛ с {error.GetType().Name}: {error.Message}");
            failures++;
        }
    }

    var restored = ArchiveCodec.Decompress(good).AsSpan().SequenceEqual(original);
    Console.WriteLine($"исправный архив: совпало = {restored}");
    return failures == 0 && restored ? 0 : 1;
}

static int Usage()
{
    Console.Error.WriteLine("""
        Использование:
          archiver compress   <вход> <выход>      («-» — stdin/stdout)
          archiver decompress <вход> <выход>
          archiver bench      <папка> [--no-filters]
          archiver selftest   <файл>
        """);
    return 1;
}
