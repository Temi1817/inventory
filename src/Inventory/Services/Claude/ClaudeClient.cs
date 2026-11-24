using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inventory.Infrastructure;

namespace Inventory.Services.Claude;

/// <summary>Понятная пользователю ошибка Claude API.</summary>
public sealed class ClaudeException(string message, bool retryable = false, Exception? inner = null)
    : Inventory.Services.Recognition.RecognitionException(message, inner)
{
    public bool Retryable { get; } = retryable;
}

/// <summary>
/// Минимальный клиент Messages API на HttpClient (POST /v1/messages).
/// Повторяет запрос при 408/409/429/5xx и сетевых ошибках, учитывая retry-after.
/// </summary>
public sealed class ClaudeClient
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2026-10-21";
    // Серверный fallback при отказе (stop_reason = refusal): API сам перезапустит запрос на подходящей модели.
    private const string FallbackBeta = "server-side-fallback-2026-07-01";
    private const int MaxAttempts = 4;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly string _apiKey;
    public string Model { get; }

    public ClaudeClient(string apiKey, string model = AppSettings.DefaultModel)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ClaudeException("API-ключ не задан (Сервис → Настройки).");
        _apiKey = apiKey.Trim();
        Model = model;
    }

    /// <summary>Отправить запрос и вернуть склеенный текст ответа (все блоки type=text).</summary>
    public async Task<string> SendAsync(JsonObject body, CancellationToken ct = default, bool strict = true)
    {
        body["model"] = Model;
        body["fallbacks"] = "default";
        var json = body.ToJsonString();

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("x-api-key", _apiKey);
                request.Headers.Add("anthropic-version", ApiVersion);
                request.Headers.Add("anthropic-beta", FallbackBeta);

                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return ExtractText(text, strict);

                var error = ToException(response.StatusCode, text, response.Headers);
                if (!error.Retryable || attempt >= MaxAttempts) throw error;
                Log.Info($"Claude API: {(int)response.StatusCode}, повтор {attempt}/{MaxAttempts - 1}");
                await Task.Delay(RetryDelay(response.Headers.RetryAfter, attempt), ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                Log.Info($"Claude API: сетевая ошибка ({ex.Message}), повтор {attempt}/{MaxAttempts - 1}");
                await Task.Delay(RetryDelay(null, attempt), ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ClaudeException("Нет связи с Claude API. Проверьте интернет.", true, ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                if (attempt >= MaxAttempts) throw new ClaudeException("Claude API не ответил вовремя.", true, ex);
                await Task.Delay(RetryDelay(null, attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Проверка ключа и связи: короткий запрос, ответ не важен.</summary>
    public Task PingAsync(CancellationToken ct = default) => SendAsync(new JsonObject
    {
        ["max_tokens"] = 64,
        ["output_config"] = new JsonObject { ["effort"] = "low" },
        ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Ответь одним словом: ок" } },
    }, ct, strict: false);

    private static TimeSpan RetryDelay(RetryConditionHeaderValue? retryAfter, int attempt)
    {
        if (retryAfter?.Delta is { } delta && delta < TimeSpan.FromMinutes(2)) return delta;
        return TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, attempt - 1)) + Random.Shared.NextDouble());
    }

    private static ClaudeException ToException(HttpStatusCode status, string body, HttpResponseHeaders headers)
    {
        var apiMessage = "";
        try { apiMessage = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? ""; }
        catch (JsonException) { /* тело не JSON */ }
        Log.Error($"Claude API {(int)status}: {body}");

        var code = (int)status;
        var shouldRetryHeader = headers.TryGetValues("x-should-retry", out var v) ? v.FirstOrDefault() : null;
        var retryable = shouldRetryHeader switch
        {
            "true" => true,
            "false" => false,
            _ => code is 408 or 409 or 429 || code >= 500,
        };

        var message = code switch
        {
            401 => "Неверный API-ключ. Проверьте его в Сервис → Настройки.",
            403 => "Ключу запрещён доступ к этой модели или функции.",
            404 => "Модель не найдена: " + apiMessage,
            413 => "Файл слишком большой для отправки.",
            429 => "Слишком много запросов к Claude API — подождите немного.",
            529 => "Claude API перегружен — попробуйте позже.",
            >= 500 => "Сбой на стороне Claude API — попробуйте позже.",
            _ => "Claude API вернул ошибку: " + (apiMessage.Length > 0 ? apiMessage : code.ToString()),
        };
        return new ClaudeException(message, retryable);
    }

    /// <summary>Текст ответа. Проверяет stop_reason: отказ и обрезанный ответ — это ошибки.</summary>
    internal static string ExtractText(string responseJson, bool strict = true)
    {
        var root = JsonNode.Parse(responseJson) ?? throw new ClaudeException("Пустой ответ от Claude API.");
        var stop = root["stop_reason"]?.GetValue<string>();
        if (stop == "refusal")
            throw new ClaudeException("Claude отказался обрабатывать этот документ.");
        if (stop == "max_tokens" && strict)
            throw new ClaudeException("Ответ Claude оборвался (слишком много позиций). Разбейте накладную на части или введите вручную.");

        var sb = new StringBuilder();
        foreach (var block in root["content"]?.AsArray() ?? [])
            if (block?["type"]?.GetValue<string>() == "text")
                sb.Append(block["text"]?.GetValue<string>());
        return sb.ToString();
    }
}
