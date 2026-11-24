using Inventory.Data;
using Inventory.Infrastructure;
using Inventory.Services.Claude;

namespace Inventory.UI.Arrival;

/// <summary>Раздел «Приход»: перетащить/выбрать накладную или ввести вручную.</summary>
public sealed class ArrivalView : UserControl, IView
{
    private readonly Panel _drop = new() { Dock = DockStyle.Top, Height = 140, AllowDrop = true, Cursor = Cursors.Hand };
    private readonly Label _keyStatus = new() { AutoSize = true, Padding = new Padding(0, 10, 0, 0) };
    private readonly Label _last = new() { AutoSize = true, ForeColor = Color.FromArgb(22, 163, 74), Padding = new Padding(0, 6, 0, 0) };
    private readonly QueuePanel _queue = new();
    private bool _dragOver;

    public ArrivalView()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(16);

        _drop.Paint += PaintDropZone;
        _drop.Resize += (_, _) => _drop.Invalidate();
        _drop.Click += (_, _) => ChooseFile();
        var actions = Ui.Toolbar();
        actions.Padding = new Padding(0, 12, 0, 0);
        actions.Controls.AddRange([
            Ui.Button("Выбрать файл накладной…", (_, _) => ChooseFile(), primary: true),
            Ui.Button("Загрузить пачку: файлы…", (_, _) => ChooseBatchFiles()),
            Ui.Button("Загрузить пачку: папку…", (_, _) => ChooseBatchFolder()),
            Ui.Button("Ввести вручную без файла", (_, _) => OpenInvoice(null)),
        ]);

        var info = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown };
        info.Controls.AddRange([_keyStatus, _last]);

        Controls.Add(_queue);
        Controls.Add(info);
        Controls.Add(actions);
        Controls.Add(_drop);
        Controls.Add(Ui.Title("Приход по накладной"));
        EnableDrop(this);
    }

    /// <summary>Файлы можно бросить в любое место раздела, не только в рамку (события перетаскивания не всплывают).</summary>
    private void EnableDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += OnDragEnter;
        control.DragLeave += (_, _) => { _dragOver = false; _drop.Invalidate(); };
        control.DragDrop += OnDragDrop;
        foreach (Control child in control.Controls) EnableDrop(child);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (GetFiles(e.Data).Count == 0) { e.Effect = DragDropEffects.None; return; }
        e.Effect = DragDropEffects.Copy;
        _dragOver = true;
        _drop.Invalidate();
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        _dragOver = false;
        _drop.Invalidate();
        var files = GetFiles(e.Data);
        // Один файл — сразу в окно проверки; несколько файлов или папка — в очередь.
        var droppedFolder = e.Data?.GetData(DataFormats.FileDrop) is string[] raw && raw.Any(Directory.Exists);
        if (files.Count == 1 && !droppedFolder) BeginInvoke(() => OpenInvoice(files[0]));
        else if (files.Count > 0) BeginInvoke(() => _queue.AddFiles(files));
    }

    public void RefreshData()
    {
        var s = AppSettings.Load();
        var claudeWithoutKey = s.Engine == RecognitionEngine.Claude && s.GetApiKey() == null;
        _keyStatus.Text = claudeWithoutKey
            ? "Выбран Claude API, но ключ не задан — будет использовано распознавание Windows. Сервис → Настройки."
            : $"Распознавание: {RecognitionEngine.Title(s.Engine)}" +
              (s.Engine == RecognitionEngine.Ollama ? $" ({s.OllamaModel}) — бесплатно, без интернета." : ".") +
              " Изменить: Сервис → Настройки.";
        _keyStatus.ForeColor = claudeWithoutKey ? Ui.Danger : Ui.Muted;
        _queue.RefreshData();
    }

    private const int MaxBatch = 2000;

    private void ChooseBatchFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Пачка накладных (можно выбрать много файлов)", Multiselect = true,
            Filter = "Накладные|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.pdf",
        };
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) AddBatch(dlg.FileNames.Where(InvoiceRecognizer.IsSupported).ToList());
    }

    private void ChooseBatchFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Папка со старыми накладными (вложенные папки тоже)", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        List<string> files = [];
        Ui.Safe(FindForm(), () => files = ExpandFolder(dlg.SelectedPath), "Не удалось прочитать папку");
        if (files.Count == 0) { Ui.Warn(FindForm(), "В папке нет фото или PDF."); return; }
        AddBatch(files);
    }

    private void AddBatch(List<string> files)
    {
        if (files.Count == 0) return;
        if (files.Count > MaxBatch) { Ui.Warn(FindForm(), $"Слишком много файлов ({files.Count}). За раз — не больше {MaxBatch}."); return; }
        if (files.Count > 50 && !Ui.Confirm(FindForm(), $"Добавить в очередь {files.Count} файлов?")) return;
        _queue.AddFiles(files);
    }

    private static List<string> ExpandFolder(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(InvoiceRecognizer.IsSupported).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    private static List<string> GetFiles(IDataObject? data)
    {
        if (data?.GetData(DataFormats.FileDrop) is not string[] items) return [];
        try
        {
            return items.SelectMany(p => Directory.Exists(p) ? ExpandFolder(p) : [p])
                .Where(InvoiceRecognizer.IsSupported).Distinct().ToList();
        }
        catch (Exception) { return items.Where(InvoiceRecognizer.IsSupported).ToList(); }
    }

    private void ChooseFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Накладная (фото или PDF)",
            Filter = "Накладные|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.pdf|Все файлы|*.*",
        };
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) OpenInvoice(dlg.FileName);
    }

    private void OpenInvoice(string? file)
    {
        if (file != null && !InvoiceRecognizer.IsSupported(file))
        {
            Ui.Warn(FindForm(), "Поддерживаются фото (JPG, PNG, …) и PDF.");
            return;
        }
        using var form = new InvoiceEditForm(file);
        if (form.ShowDialog(FindForm()) != DialogResult.OK || form.AcceptedInvoiceId is not { } id) return;
        var inv = InvoiceRepo.Get(id);
        _last.Text = $"✓ Принята накладная № {inv?.Number ?? "—"} от {inv?.Date:dd.MM.yyyy}: вещей на складе {inv?.ItemCount ?? 0}. Смотрите «Склад» и «Накладные».";
    }

    private void PaintDropZone(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = _drop.ClientRectangle;
        r.Inflate(-2, -2);
        g.Clear(_dragOver ? Color.FromArgb(219, 234, 254) : Color.FromArgb(248, 250, 252));
        using var pen = new Pen(_dragOver ? Ui.Accent : Color.FromArgb(148, 163, 184), 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        g.DrawRectangle(pen, r);

        using var big = new Font("Segoe UI Semibold", 14f);
        var top = r with { Height = r.Height / 2 + 10 };
        var bottom = r with { Y = r.Y + r.Height / 2 + 14, Height = r.Height / 2 - 14 };
        TextRenderer.DrawText(g, "Перетащите сюда фото или PDF накладной", big, top, Color.FromArgb(51, 65, 85),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom);
        TextRenderer.DrawText(g, "один файл — сразу на проверку, несколько файлов или папку — в очередь  ·  или щёлкните, чтобы выбрать", Ui.BaseFont, bottom, Ui.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
    }
}
