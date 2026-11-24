using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Inventory.Infrastructure;

/// <summary>
/// Настройки программы: settings.json в папке данных.
/// API-ключ хранится зашифрованным DPAPI (CurrentUser) — расшифровать может только этот пользователь Windows.
/// </summary>
public sealed class AppSettings
{
    public const string DefaultModel = "claude-sonnet-5-5";
    public const string DefaultOllamaModel = "qwen3-vl:4b-instruct";
    public const string DefaultOllamaUrl = "http://localhost:11434";

    /// <summary>Чем распознавать накладные: ollama (локальный ИИ, бесплатно), ocr (Windows OCR), claude (платный API).</summary>
    public string Engine { get; set; } = RecognitionEngine.Ollama;
    public string OllamaUrl { get; set; } = DefaultOllamaUrl;
    public string OllamaModel { get; set; } = DefaultOllamaModel;

    /// <summary>ФИО того, кто выдаёт вещи, — для подписи «Выдал» в акте. Пусто — в акте пустая линия.</summary>
    public string? IssuerName { get; set; }

    public string? ApiKeyProtected { get; set; }
    public string Model { get; set; } = DefaultModel;

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Inventory.ClaudeApiKey.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings)) ?? new AppSettings();
        }
        catch (Exception ex) { Log.Error("Не удалось прочитать settings.json — используются настройки по умолчанию", ex); }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        var tmp = AppPaths.Settings + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, AppPaths.Settings, overwrite: true);
    }

    /// <summary>Расшифрованный ключ или null (не задан / не расшифровывается).</summary>
    public string? GetApiKey()
    {
        if (string.IsNullOrEmpty(ApiKeyProtected)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ApiKeyProtected), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось расшифровать API-ключ (другой пользователь Windows?)", ex);
            return null;
        }
    }

    public void SetApiKey(string? key)
    {
        key = key?.Trim();
        ApiKeyProtected = string.IsNullOrEmpty(key)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));
    }
}

public static class RecognitionEngine
{
    public const string Ollama = "ollama";
    public const string WindowsOcr = "ocr";
    public const string Claude = "claude";

    public static string Title(string engine) => engine switch
    {
        Ollama => "локальный ИИ (Ollama)",
        WindowsOcr => "распознавание текста Windows",
        Claude => "Claude API",
        _ => engine,
    };
}