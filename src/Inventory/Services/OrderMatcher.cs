using System.Text.RegularExpressions;
using Inventory.Models;

namespace Inventory.Services;

/// <summary>
/// Сопоставление позиций накладной с позициями заказа (счёта): переносит «для кого», категорию и признак расходника.
/// Названия в счёте и накладной обычно похожи, но не всегда одинаковы — сравниваем по словам.
/// Учитывается количество: «Клавиатура × 2» в накладной при двух строках счёта (Иванову и Петрову)
/// делится на две строки с разными получателями.
/// </summary>
public static partial class OrderMatcher
{
    public const double MinSimilarity = 0.4;

    /// <summary>Меняет список строк накладной на месте. Возвращает, сколько штук получили данные из счёта.</summary>
    public static int Apply(List<InvoiceLine> invoiceLines, IReadOnlyList<InvoiceLine> orderLines)
    {
        var left = orderLines.Select(o => o.Quantity).ToArray();   // сколько штук строки счёта ещё не сопоставлено
        var result = new List<InvoiceLine>();
        var unmatched = new List<InvoiceLine>();
        var matchedQty = 0m;

        foreach (var line in invoiceLines)
        {
            var remaining = line.Quantity;
            var parts = new List<InvoiceLine>();
            while (remaining > 0)
            {
                var best = BestMatch(line.Name, orderLines, left);
                if (best < 0) break;
                var take = Math.Min(remaining, left[best]);
                var part = line.Clone();
                part.Quantity = take;
                CopyFrom(part, orderLines[best]);
                parts.Add(part);
                left[best] -= take;
                remaining -= take;
                matchedQty += take;
            }
            if (remaining > 0)
            {
                var rest = line.Clone();
                rest.Quantity = remaining;
                parts.Add(rest);
                if (parts.Count == 1) unmatched.Add(rest);
            }
            result.AddRange(parts);
        }

        // Названия совсем разные, но строк поровну — сопоставляем несопоставленные по порядку.
        if (unmatched.Count > 0 && invoiceLines.Count == orderLines.Count)
        {
            var free = Enumerable.Range(0, orderLines.Count).Where(i => left[i] == orderLines[i].Quantity).ToList();
            for (var k = 0; k < unmatched.Count && k < free.Count; k++)
            {
                CopyFrom(unmatched[k], orderLines[free[k]]);
                matchedQty += unmatched[k].Quantity;
            }
        }

        invoiceLines.Clear();
        invoiceLines.AddRange(result);
        return (int)matchedQty;
    }

    private static int BestMatch(string name, IReadOnlyList<InvoiceLine> orderLines, decimal[] left)
    {
        var best = -1;
        var bestScore = 0.0;
        for (var i = 0; i < orderLines.Count; i++)
        {
            if (left[i] <= 0) continue;
            var score = Similarity(name, orderLines[i].Name);
            if (score > bestScore) { bestScore = score; best = i; }
        }
        return bestScore >= MinSimilarity ? best : -1;
    }

    private static void CopyFrom(InvoiceLine target, InvoiceLine order)
    {
        target.EmployeeId = order.EmployeeId;
        if (string.IsNullOrWhiteSpace(target.Category)) target.Category = order.Category;
        target.ToConsumables = order.ToConsumables;
    }

    /// <summary>Похожесть названий 0..1 (доля общих слов).</summary>
    internal static double Similarity(string a, string b)
    {
        var x = Words(a);
        var y = Words(b);
        if (x.Count == 0 || y.Count == 0) return 0;
        var common = x.Intersect(y).Count();
        return (double)common / Math.Min(x.Count, y.Count) * 0.7 + (double)common / x.Union(y).Count() * 0.3;
    }

    private static HashSet<string> Words(string s) =>
        WordRegex().Matches(s.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length > 1 || char.IsDigit(w[0])).ToHashSet();

    [GeneratedRegex(@"[\p{L}\p{N}]+")] private static partial Regex WordRegex();
}
