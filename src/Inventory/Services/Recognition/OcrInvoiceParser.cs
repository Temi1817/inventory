using System.Globalization;
using System.Text.RegularExpressions;
using Inventory.Models;

namespace Inventory.Services.Recognition;

/// <summary>
/// Разбор текста накладной (после OCR) по шаблонам: номер, дата, БИН, поставщик, итог, строки товаров.
/// Работает «как получится» — пользователь всё равно проверяет результат.
/// Колонки в строке разделены двумя пробелами (см. WindowsOcrRecognizer.GroupRows).
/// </summary>
public static partial class OcrInvoiceParser
{
    private static readonly string[] Units =
        ["шт", "шт.", "штук", "м", "м.", "метр", "кг", "л", "упак", "упак.", "уп", "уп.", "компл", "компл.", "к-т", "пач", "пачка", "рул", "ед", "ед."];

    private static readonly string[] Months =
        ["январ", "феврал", "март", "апрел", "ма", "июн", "июл", "август", "сентябр", "октябр", "ноябр", "декабр"];

    public static RecognizedInvoice Parse(IReadOnlyList<string> rawRows)
    {
        var rows = rawRows.Select(FixOcrDigits).ToList();
        var text = string.Join("\n", rows);
        return new RecognizedInvoice
        {
            Number = FindNumber(rows),
            Date = FindDate(text)?.ToString("yyyy-MM-dd"),
            SupplierBin = FindBin(text),
            Supplier = FindSupplier(rows),
            Total = FindTotal(rows),
            Items = rows.Select(ParseItemRow).OfType<RecognizedLine>().ToList(),
        };
    }

    /// <summary>
    /// Типичные ошибки OCR в числах: «о»/«O» вместо 0, «з» вместо 3 («400 ооо,оо» → «400 000,00»), «N2» вместо «№».
    /// Меняем только «числоподобные» слова: с цифрой, с запятой/точкой или номер строки в начале.
    /// </summary>
    internal static string FixOcrDigits(string row)
    {
        row = OcrNumberSignRegex().Replace(row, "№");
        // «зо 500,00»: группа тысяч без единой цифры, но сразу перед числом через одиночный пробел.
        row = OcrLeadingGroupRegex().Replace(row, m => FixChars(m.Value));
        return OcrDigitTokenRegex().Replace(row, m =>
        {
            var v = m.Value;
            var looksNumeric = v.Any(char.IsDigit) || v.Contains(',') || v.Contains('.') || m.Index == 0;
            if (!looksNumeric || v.All(c => c is '.' or ',')) return v;
            return FixChars(v);
        });
    }

    private static string FixChars(string v) =>
        new(v.Select(c => c switch { 'о' or 'О' or 'o' or 'O' => '0', 'з' or 'З' => '3', _ => c }).ToArray());

    internal static string? FindNumber(IReadOnlyList<string> rows)
    {
        // Сначала «Накладная … № X», затем любой «№ X» (кроме «№ п/п»).
        foreach (var preferDoc in new[] { true, false })
            foreach (var row in rows)
            {
                if (preferDoc && !row.Contains("наклад", StringComparison.OrdinalIgnoreCase)
                              && !row.Contains("документ", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (Match m in NumberRegex().Matches(row))
                {
                    var v = m.Groups[1].Value.Trim('.', ',');
                    if (v.StartsWith("п/", StringComparison.OrdinalIgnoreCase) || v.Length == 0) continue;
                    return v;
                }
            }
        return null;
    }

    internal static DateTime? FindDate(string text)
    {
        var m = DateRegex().Match(text);
        if (m.Success && DateTime.TryParseExact(m.Value.Replace('/', '.').Replace('-', '.'),
                ["dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;

        var w = WordDateRegex().Match(text);
        if (!w.Success) return null;
        var month = Array.FindIndex(Months, p => w.Groups[2].Value.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (month < 0 || !int.TryParse(w.Groups[1].Value, out var day) || !int.TryParse(w.Groups[3].Value, out var year)) return null;
        try { return new DateTime(year, month + 1, day); } catch (ArgumentOutOfRangeException) { return null; }
    }

    internal static string? FindBin(string text)
    {
        var m = BinRegex().Match(text);
        var digits = m.Success ? new string(m.Groups[1].Value.Where(char.IsDigit).ToArray()) : null;
        if (digits is { Length: 12 }) return digits;
        var any = Regex.Match(text, @"(?<!\d)\d{12}(?!\d)");
        return any.Success ? any.Value : null;
    }

    internal static string? FindSupplier(IReadOnlyList<string> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var k = KeywordRegex().Match(row);
            if (!k.Success) continue;
            var rest = Clean(row[(k.Index + k.Length)..]);
            if (rest.Length < 3 && i + 1 < rows.Count) rest = Clean(rows[i + 1]);
            if (rest.Length >= 3) return rest;
        }
        var org = OrgRegex().Match(string.Join("\n", rows));
        return org.Success ? Clean(org.Value) : null;

        static string Clean(string s)
        {
            s = Regex.Replace(s, @"(БИН|ИИН|BIN|РНН).*$", "", RegexOptions.IgnoreCase);
            return s.Trim(' ', ':', '-', '—', ',', ';', '|');
        }
    }

    internal static decimal? FindTotal(IReadOnlyList<string> rows)
    {
        decimal? best = null;
        foreach (var row in rows.Where(r => TotalRegex().IsMatch(r)))
        {
            var nums = Numbers(row);
            if (nums.Count > 0) best = nums.Max();
        }
        return best;
    }

    /// <summary>Строка товара: начинается с порядкового номера, дальше наименование и числа.</summary>
    internal static RecognizedLine? ParseItemRow(string row)
    {
        var m = ItemStartRegex().Match(row);
        if (!m.Success || TotalRegex().IsMatch(row)) return null;
        var body = row[m.Length..];

        // Наименование — до первой «числовой» колонки или единицы измерения.
        var columns = body.Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var nameParts = new List<string>();
        string? unit = null;
        var tail = new List<string>();
        foreach (var col in columns)
        {
            if (unit == null && Units.Contains(col.ToLowerInvariant())) { unit = col.TrimEnd('.'); continue; }
            if (tail.Count == 0 && !IsNumeric(col)) { nameParts.Add(col); continue; }
            tail.Add(col);
        }
        var name = string.Join(" ", nameParts).Trim();
        if (name.Length < 2 || !name.Any(char.IsLetter)) return null;

        var nums = tail.SelectMany(Numbers).ToList();
        var (qty, price) = GuessQtyPrice(nums);
        return new RecognizedLine { Name = name, Unit = unit ?? "шт", Quantity = qty, Price = price };
    }

    /// <summary>Ищем тройку «кол-во × цена ≈ сумма» среди чисел строки.</summary>
    internal static (decimal? qty, decimal? price) GuessQtyPrice(List<decimal> n)
    {
        for (var i = 0; i < n.Count; i++)
            for (var j = i + 1; j < n.Count; j++)
                for (var k = j + 1; k < n.Count; k++)
                {
                    var product = n[i] * n[j];
                    if (n[i] > 0 && n[j] > 0 && Math.Abs(product - n[k]) <= Math.Max(1m, n[k] * 0.005m))
                        return (n[i], n[j]);
                }
        return n.Count switch
        {
            0 => (null, null),
            1 => (n[0] == Math.Floor(n[0]) && n[0] < 1000 ? n[0] : null, null),
            _ => (n[0] == Math.Floor(n[0]) && n[0] < 1000 ? n[0] : null, n[1]),
        };
    }

    private static bool IsNumeric(string col) => NumberTokenRegex().IsMatch(col.Replace(" ", ""));

    /// <summary>Числа в тексте: «1 250 000,50» → 1250000.50 (пробел внутри числа — разделитель тысяч).</summary>
    internal static List<decimal> Numbers(string text)
    {
        var result = new List<decimal>();
        foreach (var col in text.Split("  ", StringSplitOptions.RemoveEmptyEntries))
            foreach (Match m in AmountRegex().Matches(col))
            {
                var v = m.Value.Replace(" ", "").Replace(" ", "").Replace(",", ".");
                if (decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) result.Add(d);
            }
        return result;
    }

    [GeneratedRegex(@"№\s*([A-Za-zА-Яа-я0-9][\w\-/.]*)")] private static partial Regex NumberRegex();
    [GeneratedRegex(@"(?<=^|\s)(?:N[2°ºo]|No\.?)(?=\s)")] private static partial Regex OcrNumberSignRegex();
    [GeneratedRegex(@"(?<=^|\s)[0-9оОoOзЗ]+(?:[.,][0-9оОoOзЗ]+)*(?=[\s:;]|$)")] private static partial Regex OcrDigitTokenRegex();
    [GeneratedRegex(@"(?<=^|\s)[0-9оОoOзЗ]{1,3}(?= [0-9оОoOзЗ]{3}(?:[.,\s]|$))")] private static partial Regex OcrLeadingGroupRegex();
    [GeneratedRegex(@"(?<!\d)(\d{1,2}[./-]\d{1,2}[./-](\d{4}|\d{2}))(?!\d)")] private static partial Regex DateRegex();
    [GeneratedRegex(@"(?<!\d)(\d{1,2})\s+([А-Яа-я]+)\s+(\d{4})", RegexOptions.IgnoreCase)] private static partial Regex WordDateRegex();
    [GeneratedRegex(@"(?:БИН|ИИН|BIN)\W{0,10}(\d[\d ]{10,16}\d)", RegexOptions.IgnoreCase)] private static partial Regex BinRegex();
    [GeneratedRegex(@"(отправитель|поставщик|продавец)\)?\s*[:\-—]?", RegexOptions.IgnoreCase)] private static partial Regex KeywordRegex();
    [GeneratedRegex(@"\b(ТОО|ИП|АО|ООО)\s*[«""]?[^\n,]{2,60}")] private static partial Regex OrgRegex();
    [GeneratedRegex(@"(итого|всего|к оплате)", RegexOptions.IgnoreCase)] private static partial Regex TotalRegex();
    [GeneratedRegex(@"^\s*\d{1,3}[.)]?\s+")] private static partial Regex ItemStartRegex();
    [GeneratedRegex(@"^\d+([.,]\d+)?$")] private static partial Regex NumberTokenRegex();
    [GeneratedRegex(@"\d{1,3}(?:[  ]\d{3})+(?:[.,]\d+)?|\d+(?:[.,]\d+)?")] private static partial Regex AmountRegex();
}
