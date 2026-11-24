using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Inventory.Infrastructure;
using Inventory.Models;
using Inventory.Services.Claude;

namespace Inventory.Services.Recognition;

/// <summary>
/// Локальный ИИ через Ollama (бесплатно, без интернета): vision-модель читает картинку страницы
/// и возвращает JSON по той же схеме, что и Claude. PDF — постранично, результаты склеиваются.
/// </summary>
public enum OllamaState { Ready, NotRunning, NoModel }

public sealed class OllamaRecognizer(string baseUrl, string model) : IInvoiceRecognizer
{
    // Для локальной модели картинка поменьше — быстрее и влезает в память видеокарты.
    public const int ImageSide = 1600;

    // Первая загрузка модели в видеопамять может занять минуту-две.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly string _baseUrl = baseUrl.TrimEnd('/');

    public string Name => RecognitionEngine.Title(RecognitionEngine.Ollama) + ", " + model;

    /// <summary>Запущен ли Ollama и скачана ли модель. Если сервер не отвечает — пробуем его запустить.</summary>
    public static async Task<(bool ok, string? problem)> CheckAsync(string baseUrl, string model, CancellationToken ct = default) =>
        await GetStateAsync(baseUrl, model, ct) switch
        {
            OllamaState.Ready => (true, null),
            OllamaState.NoModel => (false, $"Модель «{model}» не скачана (≈3,3 ГБ из интернета): Сервис → Настройки → «Проверить» — предложит скачать."),
            _ => (false, "Ollama не запущен (установите с ollama.com или запустите «Ollama» из меню Пуск)."),
        };

    public static async Task<OllamaState> GetStateAsync(string baseUrl, string model, CancellationToken ct = default)
    {
        var url = baseUrl.TrimEnd('/');
        var models = await TryListModels(url, ct);
        if (models == null && TryStartServer())
        {
            for (var i = 0; i < 15 && models == null; i++)
            {
                await Task.Delay(1000, ct);
                models = await TryListModels(url, ct);
            }
        }
        if (models == null) return OllamaState.NotRunning;
        var found = models.Any(m => m.Equals(model, StringComparison.OrdinalIgnoreCase)
                                    || m.Equals(model + ":latest", StringComparison.OrdinalIgnoreCase));
        return found ? OllamaState.Ready : OllamaState.NoModel;
    }

    /// <summary>
    /// Скачать модель через Ollama (POST /api/pull, ответ — поток JSON-строк {status, total, completed} или {error}).
    /// Можно прервать и продолжить потом: Ollama докачивает с места остановки.
    /// </summary>
    public static async Task PullAsync(string baseUrl, string model, IProgress<(string status, double? percent)> progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/api/pull")
        {
            Content = JsonContent.Create(new { model, stream = true }),
        };
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new RecognitionException($"Ollama не смог скачать модель: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        string? lastStatus = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var json = JsonNode.Parse(line);
            if (json?["error"]?.GetValue<string>() is { } error) throw new RecognitionException("Ollama: " + error);
            lastStatus = json?["status"]?.GetValue<string>() ?? lastStatus;
            var total = json?["total"]?.GetValue<double>();
            var done = json?["completed"]?.GetValue<double>();
            progress.Report((lastStatus ?? "", total > 0 && done != null ? done / total * 100 : null));
        }
        if (lastStatus != "success") throw new RecognitionException("Скачивание модели прервалось — попробуйте ещё раз, продолжится с места остановки.");
        Log.Info($"Модель {model} скачана");
    }

    private static async Task<List<string>?> TryListModels(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var json = await Http.GetFromJsonAsync<JsonObject>(url + "/api/tags", cts.Token);
            return json?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>() ?? "").ToList() ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static bool TryStartServer()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
        if (!File.Exists(exe)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe, "serve") { CreateNoWindow = true, UseShellExecute = false });
            Log.Info("Запущен ollama serve");
            return true;
        }
        catch (Exception ex) { Log.Error("Не удалось запустить Ollama", ex); return false; }
    }

    public async Task<RecognizedInvoice> RecognizeAsync(string filePath, CancellationToken ct = default)
    {
        var pages = await PageImages.LoadAsync(filePath, ImageSide, ct);
        var results = new List<RecognizedInvoice>();
        for (var i = 0; i < pages.Count; i++)
        {
            var prompt = InvoiceRecognizer.Prompt;
            if (pages.Count > 1)
                prompt += $"\nЭто страница {i + 1} из {pages.Count}. Если на странице нет шапки или итога — ставь null, позиции верни только с этой страницы.";
            results.Add(await AskAsync(prompt, pages[i], ct));
        }
        return Merge(results);
    }

    // Обдумывание (thinking) на локальной видеокарте замедляет ответ в разы, а для накладной не нужно.
    private bool _thinkOff = true;

    private async Task<RecognizedInvoice> AskAsync(string prompt, byte[] jpeg, CancellationToken ct)
    {
        try { return await AskOnceAsync(prompt, jpeg, ct); }
        catch (RecognitionException ex) when (_thinkOff && ex.Message.Contains("think", StringComparison.OrdinalIgnoreCase))
        {
            // Модель без режима обдумывания не принимает параметр think — повторяем без него.
            _thinkOff = false;
            return await AskOnceAsync(prompt, jpeg, ct);
        }
    }

    private async Task<RecognizedInvoice> AskOnceAsync(string prompt, byte[] jpeg, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["format"] = InvoiceRecognizer.Schema(),
            ["options"] = new JsonObject { ["temperature"] = 0, ["num_ctx"] = 8192 },
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = prompt,
                    ["images"] = new JsonArray(Convert.ToBase64String(jpeg)),
                },
            },
        };
        if (_thinkOff) body["think"] = false;

        HttpResponseMessage response;
        try
        {
            response = await Http.PostAsync(_baseUrl + "/api/chat",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        }
        catch (HttpRequestException ex) { throw new RecognitionException("Ollama не отвечает. Он запущен?", ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new RecognitionException("Локальная модель не ответила за 10 минут.", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            var root = TryParse(text);
            if (!response.IsSuccessStatusCode)
            {
                var error = root?["error"]?.GetValue<string>() ?? text;
                Log.Error($"Ollama {(int)response.StatusCode}: {error}");
                throw new RecognitionException("Ошибка локальной модели: " + error);
            }
            var content = root?["message"]?["content"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(content)) throw new RecognitionException("Локальная модель вернула пустой ответ.");
            return InvoiceRecognizer.Parse(content);
        }
    }

    private static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Склейка страниц: шапка — первое найденное значение, позиции — все подряд, итог — последний.</summary>
    internal static RecognizedInvoice Merge(IReadOnlyList<RecognizedInvoice> pages)
    {
        if (pages.Count == 1) return pages[0];
        return new RecognizedInvoice
        {
            Number = pages.Select(p => p.Number).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
            Date = pages.Select(p => p.Date).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
            Supplier = pages.Select(p => p.Supplier).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
            SupplierBin = pages.Select(p => p.SupplierBin).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
            Total = pages.Select(p => p.Total).LastOrDefault(v => v != null),
            Items = pages.SelectMany(p => p.Items).ToList(),
        };
    }
}
