using Inventory.Models;

namespace Inventory.Services.Recognition;

/// <summary>Распознаватель накладных: файл (фото/PDF) → поля накладной.</summary>
public interface IInvoiceRecognizer
{
    /// <summary>Название для статуса в окне («локальный ИИ (Ollama)» и т.п.).</summary>
    string Name { get; }

    Task<RecognizedInvoice> RecognizeAsync(string filePath, CancellationToken ct = default);
}

/// <summary>Понятная пользователю ошибка распознавания (любого движка).</summary>
public class RecognitionException(string message, Exception? inner = null) : Exception(message, inner);
