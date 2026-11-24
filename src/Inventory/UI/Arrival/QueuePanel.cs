using Inventory.Data;
using Inventory.Models;
using Inventory.Services;
using Inventory.Services.Recognition;
using Inventory.UI.Invoices;

namespace Inventory.UI.Arrival;

/// <summary>Очередь «На проверке»: фоновое распознавание пачки и поштучная проверка.</summary>
public sealed class QueuePanel : UserControl
{
    private readonly DataGridView _grid = new();
    private readonly Label _summary = new() { AutoSize = true, ForeColor = Ui.Muted, Padding = new Padding(8, 7, 0, 0) };
    private readonly ProgressBar _progress = new() { Width = 220, Height = 16, Margin = new Padding(8, 10, 0, 0), Visible = false };
    private readonly Button _pause;
    private readonly CheckBox _showFinished = new() { Text = "Показать принятые и пропущенные", AutoSize = true, Padding = new Padding(8, 6, 0, 0) };
    private readonly CheckBox _openNext = new() { Text = "После приёма открывать следующую", AutoSize = true, Checked = true, Padding = new Padding(8, 6, 0, 0) };
    private readonly System.Windows.Forms.Timer _refreshDelay = new() { Interval = 300 };

    public QueuePanel()
    {
        Dock = DockStyle.Fill;

        Ui.StyleGrid(_grid);
        _grid.Columns.AddRange(
            Ui.Col(nameof(QueueEntry.OriginalName), "Файл", 140),
            Ui.Col(nameof(QueueEntry.Number), "Номер", 60),
            Ui.Col(nameof(QueueEntry.DateText), "Дата", 55),
            Ui.Col(nameof(QueueEntry.Supplier), "Поставщик", 140),
            Ui.Col(nameof(QueueEntry.ItemCount), "Позиций", 40),
            Ui.Col(nameof(QueueEntry.Status), "Статус", 65),
            Ui.Col(nameof(QueueEntry.Note), "Примечание", 140));
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSelected(); };
        _grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; } };
        _grid.CellFormatting += OnCellFormatting;

        _pause = Ui.Button("Пауза", (_, _) => TogglePause());
        var header = Ui.Toolbar();
        header.Padding = new Padding(0, 4, 0, 4);
        header.Controls.AddRange([
            new Label { Text = "На проверке", Font = Ui.BoldFont, AutoSize = true, Padding = new Padding(0, 6, 0, 0) },
            _progress, _summary, _pause,
        ]);

        var actions = Ui.Toolbar();
        actions.Controls.AddRange([
            Ui.Button("Открыть / проверить", (_, _) => OpenSelected(), primary: true),
            Ui.Button("Распознать заново", (_, _) => Requeue()),
            Ui.Button("Ввести вручную", (_, _) => OpenSelected(manual: true)),
            Ui.Button("Пропустить", (_, _) => SkipSelected()),
            _showFinished, _openNext,
        ]);
        _showFinished.CheckedChanged += (_, _) => RefreshData();

        Controls.Add(_grid);
        Controls.Add(actions);
        Controls.Add(header);

        // События обработчика приходят из фонового потока — обновляем экран не чаще 3 раз в секунду.
        _refreshDelay.Tick += (_, _) => { _refreshDelay.Stop(); RefreshData(); };
        QueueProcessor.Instance.Changed += OnProcessorChanged;
        Disposed += (_, _) => QueueProcessor.Instance.Changed -= OnProcessorChanged;
    }

    private void OnProcessorChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!_refreshDelay.Enabled) _refreshDelay.Start(); }); }
        catch (InvalidOperationException) { /* окно закрывается */ }
    }

    /// <summary>Добавить файлы в очередь и запустить распознавание.</summary>
    public void AddFiles(IReadOnlyCollection<string> files)
    {
        if (files.Count == 0) return;
        Ui.Safe(FindForm(), () =>
        {
            Cursor = Cursors.WaitCursor;
            var (added, duplicates) = ImportQueueRepo.Add(files);
            Cursor = Cursors.Default;
            if (duplicates > 0)
                Ui.Info(FindForm(), $"Добавлено в очередь: {added}.\nПропущено {duplicates} — эти файлы уже загружались раньше.");
            if (added > 0) QueueProcessor.Instance.Start();
            RefreshData();
        }, "Не удалось добавить файлы в очередь");
        Cursor = Cursors.Default;
    }

    public void RefreshData() => Ui.Safe(FindForm(), () =>
    {
        var keepId = (_grid.CurrentRow?.DataBoundItem as QueueEntry)?.Id;
        var rows = ImportQueueRepo.List(_showFinished.Checked);
        _grid.DataSource = rows;
        if (keepId != null)
            foreach (DataGridViewRow r in _grid.Rows)
                if (((QueueEntry)r.DataBoundItem!).Id == keepId) { _grid.CurrentCell = r.Cells[0]; break; }
        UpdateSummary();
    }, "Не удалось загрузить очередь");

    private void UpdateSummary()
    {
        var c = ImportQueueRepo.Counts();
        int N(string s) => c.TryGetValue(s, out var n) ? n : 0;
        var pending = N(QueueStatus.Pending) + N(QueueStatus.Processing);
        var done = N(QueueStatus.Done);
        var errors = N(QueueStatus.Error);
        var p = QueueProcessor.Instance;

        var text = $"Ждут проверки: {done}   ·   ошибок: {errors}   ·   в очереди на распознавание: {pending}";
        if (p.IsRunning && p.CurrentFile != null)
        {
            text += $"\nРаспознаётся: {p.CurrentFile}";
            if (p.AverageSeconds is { } avg && pending > 0)
                text += $"   ·   осталось примерно {FormatEta(avg * pending)}";
        }
        if (p.EngineNote != null && p.IsRunning) text += "\n" + p.EngineNote;
        _summary.Text = text;

        var total = pending + done + errors;
        _progress.Visible = pending > 0;
        _progress.Maximum = Math.Max(1, total);
        _progress.Value = Math.Min(_progress.Maximum, total - pending);
        _pause.Visible = pending > 0;
        _pause.Text = p.IsRunning && !p.IsPaused ? "Пауза" : "Продолжить";
    }

    private static string FormatEta(double seconds) =>
        seconds < 90 ? $"{Math.Ceiling(seconds / 10) * 10:0} с" : seconds < 5400 ? $"{Math.Ceiling(seconds / 60):0} мин" : $"{seconds / 3600:0.#} ч";

    private void TogglePause()
    {
        var p = QueueProcessor.Instance;
        if (p.IsRunning && !p.IsPaused) p.Pause();
        else p.Start();
        UpdateSummary();
    }

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != nameof(QueueEntry.Status)) return;
        e.CellStyle!.ForeColor = (e.Value as string) switch
        {
            QueueStatus.Done => Color.FromArgb(22, 163, 74),
            QueueStatus.Error => Ui.Danger,
            QueueStatus.Processing => Ui.Accent,
            QueueStatus.Accepted or QueueStatus.Skipped => Color.Gray,
            _ => Ui.Muted,
        };
    }

    private QueueEntry? Selected => _grid.CurrentRow?.DataBoundItem as QueueEntry;

    private void Requeue()
    {
        if (Selected is not { } e) return;
        if (e.Status is not (QueueStatus.Error or QueueStatus.Done))
        {
            Ui.Warn(FindForm(), "Распознать заново можно файл с ошибкой или уже распознанный.");
            return;
        }
        ImportQueueRepo.Requeue(e.Id);
        QueueProcessor.Instance.Start();
        RefreshData();
    }

    private void SkipSelected()
    {
        if (Selected is not { } e || e.Status is QueueStatus.Accepted or QueueStatus.Skipped) return;
        if (e.Status == QueueStatus.Processing) { Ui.Warn(FindForm(), "Файл сейчас распознаётся — подождите."); return; }
        if (!Ui.Confirm(FindForm(), $"Пропустить «{e.OriginalName}»?\nНакладная не будет принята и уйдёт из очереди.")) return;
        Ui.Safe(FindForm(), () => ImportQueueRepo.MarkSkipped(e.Id));
        RefreshData();
    }

    private void OpenSelected(bool manual = false)
    {
        if (Selected is not { } e) return;
        Open(e, manual);
    }

    private void Open(QueueEntry e, bool manual)
    {
        var owner = FindForm();
        switch (e.Status)
        {
            case QueueStatus.Accepted when e.InvoiceId is { } acceptedId:
                InvoiceCard.Open(owner, acceptedId);
                return;
            case QueueStatus.Skipped:
                Ui.Info(owner, "Эта накладная пропущена.");
                return;
            case QueueStatus.Processing:
                Ui.Info(owner, "Файл ещё распознаётся — подождите немного.");
                return;
            case QueueStatus.Pending when !manual:
                Ui.Info(owner, "Файл ещё в очереди на распознавание. Можно ввести его вручную: «Ввести вручную».");
                return;
        }
        if (!File.Exists(e.AbsolutePath)) { Ui.Warn(owner, "Файл очереди не найден на диске."); return; }

        var recognized = manual ? null : ImportQueueRepo.GetResult(e);
        var draft = recognized != null
            ? InvoiceDrafts.FromRecognized(recognized, e.AbsolutePath)
            : new InvoiceDraft { SourceFile = e.AbsolutePath };
        var waiting = ImportQueueRepo.Counts().GetValueOrDefault(QueueStatus.Done);
        var title = $"Проверка накладной ({waiting} ждут проверки): {e.OriginalName}";

        DialogResult result;
        long? invoiceId;
        using (var form = new InvoiceEditForm(e.AbsolutePath, draft, title, allowSkip: true))
        {
            result = form.ShowDialog(owner);
            invoiceId = form.AcceptedInvoiceId;
        }

        if (result == DialogResult.OK && invoiceId != null)
            Ui.Safe(owner, () => ImportQueueRepo.MarkAccepted(e.Id, invoiceId.Value));
        else if (result == DialogResult.Ignore)
        {
            if (!Ui.Confirm(owner, $"Пропустить «{e.OriginalName}»? Накладная не будет принята.")) { RefreshData(); return; }
            Ui.Safe(owner, () => ImportQueueRepo.MarkSkipped(e.Id));
        }
        else { RefreshData(); return; }

        RefreshData();
        if (_openNext.Checked && ImportQueueRepo.List(false).FirstOrDefault(x => x.Status == QueueStatus.Done) is { } next)
            BeginInvoke(() => Open(next, false));
    }
}
