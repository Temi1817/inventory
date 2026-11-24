using Inventory.Models;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using Color = MigraDoc.DocumentObjectModel.Color;

namespace Inventory.Services.Documents;

/// <summary>Акт выдачи имущества сотруднику (PDF).</summary>
public static class ActPdf
{
    private static readonly Color HeaderFill = new(240, 240, 240);
    private static readonly Color Muted = new(110, 110, 110);

    /// <param name="issuer">Кто выдаёт (Сервис → Настройки); пусто — место для ФИО от руки.</param>
    public static void Generate(string path, Employee employee, IReadOnlyList<Item> items, DateTime date, string? company, string? issuer = null)
    {
        if (items.Count == 0) throw new ArgumentException("Нет вещей для акта.");
        var doc = Pdf.NewDocument();
        var sec = doc.AddSection();
        sec.PageSetup = doc.DefaultPageSetup.Clone();
        sec.PageSetup.PageFormat = PageFormat.A4;
        sec.PageSetup.LeftMargin = sec.PageSetup.RightMargin = Pdf.Mm(18);
        sec.PageSetup.TopMargin = sec.PageSetup.BottomMargin = Pdf.Mm(18);

        if (!string.IsNullOrWhiteSpace(company))
        {
            var c = sec.AddParagraph(company);
            c.Format.Alignment = ParagraphAlignment.Right;
            c.Format.Font.Bold = true;
        }
        var title = sec.AddParagraph("АКТ ВЫДАЧИ ИМУЩЕСТВА");
        title.Format.Alignment = ParagraphAlignment.Center;
        title.Format.Font.Size = 15;
        title.Format.Font.Bold = true;
        title.Format.SpaceBefore = Pdf.Mm(4);
        var dateP = sec.AddParagraph($"от {date:dd.MM.yyyy}");
        dateP.Format.Alignment = ParagraphAlignment.Center;
        dateP.Format.SpaceAfter = Pdf.Mm(6);

        var who = sec.AddParagraph("Получатель: ");
        who.AddFormattedText(employee.FullName, TextFormat.Bold);
        var details = string.Join(", ", new[] { employee.Position, employee.Department }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (details.Length > 0) who.AddText($" ({details})");
        who.Format.SpaceAfter = Pdf.Mm(3);
        var intro = sec.AddParagraph("Нижеперечисленное имущество передано получателю во временное пользование для выполнения служебных обязанностей:");
        intro.Format.SpaceAfter = Pdf.Mm(3);

        var table = sec.AddTable();
        table.Borders.Width = 0.7;
        table.LeftPadding = table.RightPadding = Pdf.Mm(1.5);
        table.TopPadding = table.BottomPadding = Pdf.Mm(1.2);
        foreach (var w in new[] { 10.0, 80, 30, 38, 16 }) table.AddColumn(Pdf.Mm(w));

        var head = table.AddRow();
        head.HeadingFormat = true;
        head.Shading.Color = HeaderFill;
        head.Format.Font.Bold = true;
        head.Format.Font.Size = 10;
        string[] titles = ["№", "Наименование", "Инв. номер", "Серийный номер", "Кол-во"];
        for (var i = 0; i < titles.Length; i++) head.Cells[i].AddParagraph(titles[i]);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var row = table.AddRow();
            row.VerticalAlignment = VerticalAlignment.Center;
            Cell(row.Cells[0], (i + 1).ToString(), ParagraphAlignment.Center);
            Cell(row.Cells[1], item.Name);
            Cell(row.Cells[2], item.InventoryNumber ?? "—");
            Cell(row.Cells[3], item.SerialNumber ?? "—");
            Cell(row.Cells[4], "1", ParagraphAlignment.Center);
        }
        var total = table.AddRow();
        total.Format.Font.Bold = true;
        total.Cells[0].MergeRight = 3;
        Cell(total.Cells[0], "Итого:", ParagraphAlignment.Right);
        Cell(total.Cells[4], items.Count.ToString(), ParagraphAlignment.Center);

        var note = sec.AddParagraph("Получатель обязуется обеспечить сохранность имущества и вернуть его по требованию.");
        note.Format.Font.Size = 10;
        note.Format.Font.Color = Muted;
        note.Format.SpaceBefore = Pdf.Mm(3);

        // Подписи — таблица без рамок в две колонки.
        var signs = sec.AddTable();
        signs.AddColumn(Pdf.Mm(84));
        signs.AddColumn(Pdf.Mm(10));
        signs.AddColumn(Pdf.Mm(84));
        var spacer = signs.AddRow();
        spacer.Height = Pdf.Mm(24);
        var line = signs.AddRow();
        Signature(line.Cells[0], "Выдал", string.IsNullOrWhiteSpace(issuer) ? "______________" : issuer.Trim());
        Signature(line.Cells[2], "Получил", ShortName(employee.FullName));

        var footer = sec.Footers.Primary.AddParagraph();
        footer.Format.Alignment = ParagraphAlignment.Center;
        footer.Format.Font.Size = 9;
        footer.Format.Font.Color = Muted;
        footer.AddText("Стр. ");
        footer.AddPageField();
        footer.AddText(" из ");
        footer.AddNumPagesField();

        Pdf.Save(doc, path);
    }

    private static void Cell(Cell cell, string text, ParagraphAlignment align = ParagraphAlignment.Left) =>
        cell.AddParagraph(text).Format.Alignment = align;

    private static void Signature(Cell cell, string role, string name)
    {
        var p = cell.AddParagraph(role + ": __________________ ");
        p.AddFormattedText(name, TextFormat.Bold);
        var hint = cell.AddParagraph("(подпись)");
        hint.Format.Font.Size = 8;
        hint.Format.Font.Color = Muted;
        hint.Format.LeftIndent = Pdf.Mm(24);
    }

    /// <summary>«Иванов Иван Иванович» → «Иванов И.И.»</summary>
    internal static string ShortName(string fullName)
    {
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return fullName.Trim();
        return parts[0] + " " + string.Concat(parts.Skip(1).Take(2).Select(p => char.ToUpper(p[0]) + "."));
    }
}
