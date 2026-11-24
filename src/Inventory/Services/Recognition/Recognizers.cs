using Inventory.Infrastructure;
using Inventory.Services.Claude;

namespace Inventory.Services.Recognition;

/// <summary>Выбор распознавателя по настройкам; если выбранный недоступен — запасной Windows OCR.</summary>
public static class Recognizers
{
    /// <returns>Распознаватель (или null, если ничего не доступно) и пояснение, почему выбран запасной.</returns>
    public static async Task<(IInvoiceRecognizer? recognizer, string? note)> CreateAsync(AppSettings s, CancellationToken ct = default)
    {
        string? note = null;
        switch (s.Engine)
        {
            case RecognitionEngine.Claude:
                if (s.GetApiKey() is { } key) return (new InvoiceRecognizer(new ClaudeClient(key, s.Model)), null);
                note = "Ключ Claude не задан";
                break;

            case RecognitionEngine.Ollama:
                var (ok, problem) = await OllamaRecognizer.CheckAsync(s.OllamaUrl, s.OllamaModel, ct);
                if (ok) return (new OllamaRecognizer(s.OllamaUrl, s.OllamaModel), null);
                note = problem;
                break;
        }

        if (WindowsOcrRecognizer.IsAvailable)
            return (new WindowsOcrRecognizer(), note == null ? null : note + " — использовано распознавание Windows (точность ниже).");
        return (null, (note ?? "") + " Распознавание Windows для русского языка недоступно.");
    }
}
