#:sdk Microsoft.NET.Sdk.Web
#:project Archiver.Core/Archiver.Core.csproj
// Сразу полностью оптимизированный код (как в Archiver.Cli): иначе первые секунды .NET работает медленнее,
// и проба времени в ядре ошибочно решит, что файл не успеет сжаться
#:property TieredCompilation=false
// Без native AOT: файловые приложения .NET 10 по умолчанию собираются в AOT, а в этом режиме отключён
// обычный JSON-сериализатор — /v1/health и ответы с ошибками падали бы с 500
#:property PublishAot=false

// HTTP-сервис архиватора по спецификации арены v1.
// Ядро — Archiver.Core (контекстные модели + арифметический кодер),
// этот файл — только обвязка: API + веб-страница.
// Запуск из папки core-cs: dotnet run -c Release archiver-service.cs
// Только Release: в Debug ядро работает в разы медленнее, и большие файлы уходят без сжатия (по прогнозу времени)

using System.Diagnostics;
using System.Text.Json;
using Archiver;

const long BodyLimit = 110L * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    // Файлы арены — до 100 МБ; точную границу (110 МБ) проверяем при чтении тела.
    options.Limits.MaxRequestBodySize = 115L * 1024 * 1024;
});

var app = builder.Build();

var heavyGate = new SemaphoreSlim(1, 1);
string? arenaToken = Environment.GetEnvironmentVariable("ARENA_TOKEN");
if (string.IsNullOrWhiteSpace(arenaToken)) arenaToken = null;

// ---------- CORS для песочницы арены + лог одной строкой ----------
app.Use(async (context, next) =>
{
    var res = context.Response;
    res.Headers["Access-Control-Allow-Origin"] = "*";
    res.Headers["Access-Control-Allow-Private-Network"] = "true";
    res.Headers["Access-Control-Allow-Headers"] = "*";
    res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";

    if (HttpMethods.IsOptions(context.Request.Method))
    {
        res.StatusCode = StatusCodes.Status204NoContent;
        return;
    }

    var sw = Stopwatch.StartNew();
    Exception? failure = null;
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        failure = ex;
        if (!res.HasStarted)
        {
            res.StatusCode = StatusCodes.Status500InternalServerError;
            res.ContentType = "application/json; charset=utf-8";
            await res.WriteAsync(JsonError("internal", "внутренняя ошибка сервера"));
        }
    }
    finally
    {
        sw.Stop();
        var items = context.Items;
        string inSize = items.TryGetValue("in", out var v1) ? v1!.ToString()! : "-";
        string outSize = items.TryGetValue("out", out var v2) ? v2!.ToString()! : "-";
        app.Logger.LogInformation(
            "{Method} {Path} run={Run} file={File} in={In} out={Out} {Ms}ms status={Status}",
            context.Request.Method, context.Request.Path,
            context.Request.Headers["X-Arena-Run"].ToString(),
            context.Request.Headers["X-Arena-File"].ToString(),
            inSize, outSize, sw.ElapsedMilliseconds, res.StatusCode);
        if (failure is not null) app.Logger.LogError(failure, "запрос завершился ошибкой");
    }
});

// ---------- веб-страница (встроена в этот же файл, wwwroot не нужен) ----------
app.MapGet("/", () => Results.Content(Page.Html, "text/html; charset=utf-8"));

// ---------- служебные методы ----------
app.MapGet("/v1/health", () => Results.Json(new { status = "ok" }));

app.MapGet("/v1/info", () => Results.Json(new
{
    name = "archiver",
    version = "1.0.0",
    project = "archiver",
    specVersion = "v1"
}));

// ---------- рабочие методы ----------
// Приведение к Delegate обязательно: без него лямбда считается «сырым» обработчиком (RequestDelegate),
// и возвращённый IResult выбрасывается — сервис отвечал бы 200 с пустым телом (предупреждение ASP0016)
app.MapPost("/v1/compress", (Delegate)((HttpContext ctx) => HandleBytesAsync(ctx, isCompress: true)));
app.MapPost("/v1/decompress", (Delegate)((HttpContext ctx) => HandleBytesAsync(ctx, isCompress: false)));

app.Run();

// ---------- обработчик сжатия и распаковки ----------

async Task<IResult> HandleBytesAsync(HttpContext context, bool isCompress)
{
    // токен, если задан в окружении; не задан — проверка отключена
    if (arenaToken is not null)
    {
        var auth = context.Request.Headers.Authorization.ToString();
        var given = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth["Bearer ".Length..].Trim()
            : null;
        if (given != arenaToken)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized",
                "неверный или отсутствующий токен");
    }

    byte[] input;
    try
    {
        input = await ReadBodyAsync(context.Request, BodyLimit);
    }
    catch (BodyTooLargeException)
    {
        return Error(StatusCodes.Status413PayloadTooLarge, "too_large",
            "тело запроса больше лимита 110 МБ");
    }

    byte[] output;
    try
    {
        // одна тяжёлая операция за раз: ядро занимает все ядра процессора
        await heavyGate.WaitAsync(context.RequestAborted);
        try
        {
            output = await Task.Run(
                () => isCompress ? ArchiveCodec.Compress(input) : ArchiveCodec.Decompress(input),
                context.RequestAborted);
        }
        finally
        {
            heavyGate.Release();
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        return Results.Empty; // клиент сам отключился
    }
    catch (InvalidDataException ex)
    {
        return Error(StatusCodes.Status400BadRequest, "bad_archive",
            string.IsNullOrWhiteSpace(ex.Message) ? "битый архив" : ex.Message);
    }

    context.Items["in"] = input.Length;
    context.Items["out"] = output.Length;
    return Results.Bytes(output, "application/octet-stream");
}

// ---------- вспомогательное ----------

static async Task<byte[]> ReadBodyAsync(HttpRequest request, long limit)
{
    if (request.ContentLength > limit)
        throw new BodyTooLargeException();

    using var ms = new MemoryStream();
    var buffer = new byte[64 * 1024];
    long total = 0;
    int read;
    while ((read = await request.Body.ReadAsync(buffer)) > 0)
    {
        total += read;
        if (total > limit)
            throw new BodyTooLargeException();
        ms.Write(buffer, 0, read);
    }
    return ms.ToArray();
}

static IResult Error(int status, string code, string message) =>
    Results.Json(new { error = new { code, message } }, statusCode: status);

static string JsonError(string code, string message) =>
    JsonSerializer.Serialize(new { error = new { code, message } });

// =====================================================================
// Веб-страница: две кнопки, размеры, коэффициент, время, текст ошибки.
// =====================================================================

// Страница — константа в классе: локальную константу нельзя использовать выше её объявления (ошибка CS0841)
static class Page
{
    public const string Html = """
<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Archiver — собственный архиватор</title>
<style>
  :root { color-scheme: dark; }
  body { font-family: system-ui, sans-serif; background:#0b1220; color:#e6ecf5; margin:0; }
  .wrap { max-width: 720px; margin: 0 auto; padding: 32px 20px 60px; }
  h1 { font-size: 28px; margin: 0 0 4px; }
  .sub { color:#9fb0c9; margin-bottom: 28px; }
  .card { background:#111a2e; border:1px solid #23324f; border-radius: 14px; padding: 20px; margin-bottom: 16px; }
  .row { display:flex; gap: 12px; align-items:center; flex-wrap: wrap; }
  button { background:#3b82f6; color:#fff; border:0; border-radius: 10px; padding: 10px 18px; font-size: 15px; cursor:pointer; }
  button:disabled { opacity:.5; cursor: default; }
  .stats { margin-top: 12px; color:#c8d4e6; font-size: 14px; line-height: 1.6; }
  .err { color:#ff8080; margin-top: 12px; white-space: pre-wrap; }
  input[type=file] { color:#c8d4e6; }
</style>
</head>
<body>
<div class="wrap">
  <h1>Archiver</h1>
  <p class="sub">Собственный архиватор без потерь. Файлы не сохраняются на диск — только память запроса.</p>

  <div class="card">
    <h2>Сжать</h2>
    <div class="row">
      <input type="file" id="fileC">
      <button id="btnC">Сжать</button>
    </div>
    <div class="stats" id="statC"></div>
    <div class="err" id="errC"></div>
  </div>

  <div class="card">
    <h2>Распаковать</h2>
    <div class="row">
      <input type="file" id="fileD">
      <button id="btnD">Распаковать</button>
    </div>
    <div class="stats" id="statD"></div>
    <div class="err" id="errD"></div>
  </div>
</div>

<script>
function fmt(n) {
  if (n < 1024) return n + " Б";
  if (n < 1024 * 1024) return (n / 1024).toFixed(1) + " КБ";
  return (n / 1048576).toFixed(2) + " МБ";
}

async function send(file, url, btn, stat, err, addSuffix, stripArc) {
  err.textContent = "";
  stat.textContent = "";
  if (!file) { err.textContent = "Выберите файл."; return; }
  btn.disabled = true;
  const t0 = performance.now();
  try {
    const res = await fetch(url, { method: "POST", body: file });
    if (!res.ok) {
      let msg = "Ошибка " + res.status;
      try {
        const j = await res.json();
        if (j && j.error && j.error.message) msg = j.error.message;
      } catch (e) {}
      err.textContent = msg;
      return;
    }
    const blob = await res.blob();
    const ms = Math.round(performance.now() - t0);
    let name = file.name;
    if (stripArc && name.endsWith(".arc")) name = name.slice(0, -4);
    else if (addSuffix) name += addSuffix;
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = name;
    a.click();
    URL.revokeObjectURL(a.href);
    const ratio = file.size > 0 ? (blob.size / file.size) : 1;
    stat.textContent = "Было: " + fmt(file.size) + " → стало: " + fmt(blob.size) +
      " · коэффициент: " + ratio.toFixed(3) + " · время: " + ms + " мс";
  } catch (e) {
    err.textContent = "Не удалось связаться с сервисом: " + e;
  } finally {
    btn.disabled = false;
  }
}

document.getElementById("btnC").onclick = () =>
  send(document.getElementById("fileC").files[0], "/v1/compress",
    document.getElementById("btnC"), document.getElementById("statC"),
    document.getElementById("errC"), ".arc", false);

document.getElementById("btnD").onclick = () =>
  send(document.getElementById("fileD").files[0], "/v1/decompress",
    document.getElementById("btnD"), document.getElementById("statD"),
    document.getElementById("errD"), "", true);
</script>
</body>
</html>
""";
}

// Объявления типов — только в конце файла: после них верхнеуровневый код писать нельзя (ошибка CS8803)
sealed class BodyTooLargeException : Exception { }
