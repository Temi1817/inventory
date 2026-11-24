using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inventory.Infrastructure;
using Inventory.Models;
using Inventory.Services.Recognition;

namespace Inventory.Services.Claude;

/// <summary>Распознавание товарной накладной (фото или PDF) через Claude vision.</summary>
public sealed class InvoiceRecognizer(ClaudeClient client) : IInvoiceRecognizer
{
    public static readonly string[] SupportedExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".pdf"];
    private const long MaxPdfBytes = 24 * 1024 * 1024;
    private const int MaxImageSide = 2400;

    internal const string Prompt = """
        Это товарная накладная или счёт на оплату из Казахстана. Верни строго JSON без пояснений:
        {number, date (YYYY-MM-DD), supplier, supplier_bin, total, items:[{name, quantity, unit, price, category}]}.
        Если поле не видно, ставь null.

        Уточнения:
        - number — номер документа; supplier — поставщик (продавец), не покупатель; supplier_bin — БИН/ИИН поставщика (12 цифр).
        - total — итоговая сумма к оплате с НДС. Для счёта на оплату number — номер счёта.
        - items — только товарные строки, без строк «Итого», «НДС», «Всего».
        - name — наименование как в документе; unit — единица измерения (шт, м, упак, кг…).
        - price — цена за единицу с НДС; если указана только сумма по строке — раздели её на количество.
        - category — короткая категория по-русски на твой выбор: Ноутбук, Компьютер, Монитор, Принтер, МФУ, Телефон,
          Сетевое оборудование, Периферия, Комплектующие, Кабели, Картриджи, Канцелярия, Мебель, Прочее.
        - Числа — без пробелов и валюты, десятичный разделитель — точка.
        """;

    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public string Name => RecognitionEngine.Title(RecognitionEngine.Claude);

    public async Task<RecognizedInvoice> RecognizeAsync(string filePath, CancellationToken ct = default)
    {
        var fileBlock = await Task.Run(() => BuildFileBlock(filePath), ct).ConfigureAwait(false);
        var body = new JsonObject
        {
            ["max_tokens"] = 16000,
            ["output_config"] = new JsonObject
            {
                ["effort"] = "medium",
                ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = Schema() },
            },
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray { fileBlock, new JsonObject { ["type"] = "text", ["text"] = Prompt } },
                },
            },
        };
        var text = await client.SendAsync(body, ct).ConfigureAwait(false);
        return Parse(text);
    }

    internal static RecognizedInvoice Parse(string json)
    {
        try
        {
            var result = JsonSerializer.Deserialize<RecognizedInvoice>(json, ParseOptions)
                         ?? throw new ClaudeException("Claude вернул пустой результат.");
            result.Items.RemoveAll(i => string.IsNullOrWhiteSpace(i.Name));
            return result;
        }
        catch (JsonException ex)
        {
            throw new ClaudeException("Не удалось разобрать ответ Claude.", false, ex);
        }
    }

    /// <summary>Дата из ответа: ожидаем YYYY-MM-DD, на всякий случай понимаем и ДД.ММ.ГГГГ.</summary>
    public static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string[] formats = ["yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy"];
        return DateTime.TryParseExact(s.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
               && d.Year is > 1990 and < 2100 ? d : null;
    }

    private static JsonObject Nullable(string type, string? description = null)
    {
        var o = new JsonObject { ["anyOf"] = new JsonArray { new JsonObject { ["type"] = type }, new JsonObject { ["type"] = "null" } } };
        if (description != null) o["description"] = description;
        return o;
    }

    internal static JsonObject Schema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("number", "date", "supplier", "supplier_bin", "total", "items"),
        ["properties"] = new JsonObject
        {
            ["number"] = Nullable("string"),
            ["date"] = Nullable("string", "Дата документа в формате YYYY-MM-DD"),
            ["supplier"] = Nullable("string"),
            ["supplier_bin"] = Nullable("string"),
            ["total"] = Nullable("number"),
            ["items"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("name", "quantity", "unit", "price", "category"),
                    ["properties"] = new JsonObject
                    {
                        ["name"] = Nullable("string"),
                        ["quantity"] = Nullable("number"),
                        ["unit"] = Nullable("string"),
                        ["price"] = Nullable("number", "Цена за единицу"),
                        ["category"] = Nullable("string"),
                    },
                },
            },
        },
    };

    /// <summary>Блок content для файла: PDF → document, картинка → image (повёрнута по EXIF, уменьшена).</summary>
    internal static JsonObject BuildFileBlock(string path)
    {
        if (!File.Exists(path)) throw new ClaudeException("Файл не найден: " + path);
        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext == ".pdf")
        {
            var info = new FileInfo(path);
            if (info.Length > MaxPdfBytes) throw new ClaudeException("PDF больше 24 МБ — разбейте его на части.");
            return Source("document", "application/pdf", Convert.ToBase64String(File.ReadAllBytes(path)));
        }
        if (ext is ".gif" or ".webp")
        {
            if (new FileInfo(path).Length > 4_500_000) throw new ClaudeException("Картинка больше 4,5 МБ — сохраните её в JPG.");
            return Source("image", ext == ".gif" ? "image/gif" : "image/webp", Convert.ToBase64String(File.ReadAllBytes(path)));
        }
        return Source("image", "image/jpeg", Convert.ToBase64String(PageImages.PrepareImage(path, MaxImageSide)));
    }

    private static JsonObject Source(string type, string mediaType, string base64) => new()
    {
        ["type"] = type,
        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = base64 },
    };

}
