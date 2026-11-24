using System.Diagnostics;
using Inventory.Infrastructure;
using Inventory.Models;
using Inventory.Services.Documents;

namespace Inventory.UI;

/// <summary>Сохранение документов: диалог «Сохранить как» → создание → открытие файла.</summary>
public static class Docs
{
    public const string PdfFilter = "PDF (*.pdf)|*.pdf";
    public const string ExcelFilter = "Excel (*.xlsx)|*.xlsx";

    public static void SaveAndOpen(IWin32Window? owner, string fileName, string filter, Action<string> write)
    {
        using var dlg = new SaveFileDialog { FileName = SafeName(fileName), Filter = filter, OverwritePrompt = true };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return;
        Ui.Safe(owner, () =>
        {
            write(dlg.FileName);
            Log.Info("Сохранён документ " + dlg.FileName);
            Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }, "Не удалось создать документ (если файл открыт в другой программе — закройте его)");
    }

    /// <summary>Убрать из имени файла запрещённые символы.</summary>
    public static string SafeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>Печать QR-наклеек: выбор раскладки → PDF → открыть (печать из просмотрщика).</summary>
    public static void PrintQr(IWin32Window? owner, IReadOnlyList<Item> items)
    {
        if (items.Count == 0) { Ui.Warn(owner, "Выберите вещи."); return; }
        var withNumber = items.Count(i => !string.IsNullOrWhiteSpace(i.InventoryNumber));
        if (withNumber == 0) { Ui.Warn(owner, "У выбранных вещей нет инвентарного номера — в QR кодируется именно он."); return; }
        if (withNumber < items.Count &&
            !Ui.Confirm(owner, $"У {items.Count - withNumber} из {items.Count} вещей нет инв. номера — они будут пропущены. Продолжить?"))
            return;

        using var dlg = new StickerDialog(withNumber);
        if (dlg.ShowDialog(owner) != DialogResult.OK) return;
        var name = withNumber == 1 ? $"QR_{items.First(i => i.InventoryNumber != null).InventoryNumber}" : $"QR_наклейки_{DateTime.Now:yyyy-MM-dd}";
        SaveAndOpen(owner, name + ".pdf", PdfFilter, path => QrStickers.Generate(path, items, dlg.StickerLayout, dlg.Copies));
    }
}

/// <summary>Выбор раскладки наклеек.</summary>
public sealed class StickerDialog : Form
{
    private readonly RadioButton _grid = new() { Text = "Сеткой на листе A4 — 24 наклейки 70×37 мм (3×8)", AutoSize = true, Checked = true };
    private readonly RadioButton _single = new() { Text = "По одной — наклейка 58×40 мм (принтер этикеток)", AutoSize = true };
    private readonly NumericUpDown _copies = new() { Minimum = 1, Maximum = 20, Value = 1, Width = 60 };

    public QrStickers.Layout StickerLayout => _single.Checked ? QrStickers.Layout.Single : QrStickers.Layout.A4Grid;
    public int Copies => (int)_copies.Value;

    public StickerDialog(int count)
    {
        Text = "QR-наклейки";
        Font = Ui.BaseFont;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(460, 220);

        var layouts = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        layouts.Controls.AddRange([_grid, _single]);
        var table = new FieldTable(110) { Padding = new Padding(12, 12, 12, 0) };
        table.Add("Вещей", new Label { Text = count.ToString(), AutoSize = true, Font = Ui.BoldFont, Padding = new Padding(0, 4, 0, 0) });
        table.Add("Раскладка", layouts);
        table.Add("Копий каждой", _copies);
        Controls.Add(table);
        Controls.Add(FieldTable.DialogButtons(this, "Создать PDF", (_, _) => { DialogResult = DialogResult.OK; Close(); }));
    }
}
