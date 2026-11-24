using System.Text;
using Inventory.Data;
using Inventory.Models;

namespace Inventory.Services;

/// <summary>Импорт/экспорт сотрудников в CSV (UTF-8, разделитель «;»).</summary>
public static class EmployeeCsv
{
    private static readonly string[] Header =
        ["ФИО", "Компания", "Отдел", "Должность", "Телефон", "Рабочее место", "Заметки", "Работает"];

    public static void Export(string path, IEnumerable<Employee> employees)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(';', Header));
        foreach (var e in employees)
            sb.AppendLine(string.Join(';', new[]
            {
                e.FullName, e.CompanyName, e.Department, e.Position, e.Phone, e.Workplace, e.Notes,
                e.IsActive ? "да" : "нет",
            }.Select(Escape)));
        // BOM — чтобы Excel сразу открыл кириллицу правильно.
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>
    /// Импорт: сотрудник с таким же ФИО обновляется, новый — добавляется.
    /// Возвращает (добавлено, обновлено, пропущено).
    /// </summary>
    public static (int added, int updated, int skipped) Import(string path)
    {
        var rows = Parse(File.ReadAllText(path, Encoding.UTF8));
        if (rows.Count == 0) return (0, 0, 0);

        var start = rows[0].Count > 0 && rows[0][0].Trim().Equals("ФИО", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        int added = 0, updated = 0, skipped = 0;

        foreach (var r in rows.Skip(start))
        {
            string Col(int i) => i < r.Count ? r[i].Trim() : "";
            var name = Col(0);
            if (name.Length == 0) { skipped++; continue; }

            var existing = EmployeeRepo.FindByName(name);
            var e = existing ?? new Employee();
            e.FullName = name;
            e.CompanyId = CompanyRepo.GetOrCreate(Col(1)) ?? e.CompanyId;
            e.Department = Col(2);
            e.Position = Col(3);
            e.Phone = Col(4);
            e.Workplace = Col(5);
            e.Notes = Col(6);
            var active = Col(7).ToLowerInvariant();
            e.IsActive = active is not ("нет" or "no" or "0" or "уволен");

            if (existing == null) { EmployeeRepo.Insert(e); added++; }
            else { EmployeeRepo.Update(e); updated++; }
        }
        return (added, updated, skipped);
    }

    private static string Escape(string? value)
    {
        value ??= "";
        return value.IndexOfAny([';', '"', '\n', '\r']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    /// <summary>Разбор CSV с кавычками и переносами строк внутри полей.</summary>
    internal static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') inQuotes = false;
                else field.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ';') { row.Add(field.ToString()); field.Clear(); }
            else if (ch == '\r') { }
            else if (ch == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row); row = []; }
            else field.Append(ch);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        rows.RemoveAll(r => r.All(string.IsNullOrWhiteSpace));
        return rows;
    }
}
