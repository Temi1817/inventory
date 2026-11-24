using System.Globalization;
using Inventory.Data;
using Inventory.Infrastructure;
using Inventory.Models;
using Inventory.Services;
using Inventory.Services.Recognition;

namespace Inventory.UI.Arrival;

public enum EditorMode
{
    /// <summary>Приход по накладной (вещи на склад / сразу выдаются).</summary>
    Invoice,
    /// <summary>Счёт на оплату — заказ: что купить и для кого. На склад ничего не попадает.</summary>
    Order,
}

/// <summary>
/// Проверка и приём накладной (или сохранение счёта на оплату): шапка + позиции слева, превью файла справа.
/// Если есть файл — сразу запускает распознавание.
/// </summary>
public sealed partial class InvoiceEditForm : Form
{
    private readonly string? _file;
    private readonly EditorMode _mode;
    private readonly long? _orderId;
    private readonly TextBox _number = new();
    private readonly DateTimePicker _date = new() { Format = DateTimePickerFormat.Short };
    private readonly ComboBox _supplier;
    private readonly TextBox _bin = new();
    private readonly ComboBox _company;
    private readonly TextBox _total = new();
    private readonly TextBox _notes = new();
    private readonly ComboBox _allRecipient = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(6, 7, 0, 0) };
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Width = 120, Height = 18, Visible = false, Margin = new Padding(6, 8, 0, 0) };
    private readonly Button _recognize;
    private Button _accept = null!;
    private readonly bool _allowSkip;
    private readonly FilePreview _preview = new();
    private CancellationTokenSource? _cts;

    /// <summary>Id принятой накладной (после «Принять»).</summary>
    public long? AcceptedInvoiceId { get; private set; }

    /// <summary>Id сохранённого заказа (режим «Счёт на оплату»).</summary>
    public long? SavedOrderId { get; private set; }

    private bool IsOrder => _mode == EditorMode.Order;

    /// <summary>Приход по заказу: накладная закроет этот заказ, получатели берутся из него.</summary>
    private bool ReceivesOrder => _mode == EditorMode.Invoice && _orderId != null;

    /// <param name="file">Файл (фото/PDF) или null для ручного ввода.</param>
    /// <param name="draft">Готовый черновик (из очереди или из заказа) — тогда не распознаём.</param>
    /// <param name="title">Заголовок окна.</param>
    /// <param name="allowSkip">Показать кнопку «Пропустить» (результат — DialogResult.Ignore).</param>
    /// <param name="mode">Накладная или счёт на оплату.</param>
    /// <param name="orderId">Счёт: какой заказ редактируем. Накладная: какой заказ она закрывает.</param>
    public InvoiceEditForm(string? file, InvoiceDraft? draft = null, string? title = null, bool allowSkip = false,
        EditorMode mode = EditorMode.Invoice, long? orderId = null)
    {
        _file = file;
        _allowSkip = allowSkip;
        _mode = mode;
        _orderId = orderId;
        var kind = IsOrder ? "Счёт на оплату" : ReceivesOrder ? "Приход по заказу" : "Приход";
        Text = title ?? (file == null ? $"{kind}: ручной ввод" : $"{kind}: {Path.GetFileName(file)}");
        Font = Ui.BaseFont;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1100, 650);
        Size = new Size(1440, 860);
        KeyPreview = true;

        _supplier = FieldTable.AutoCombo(InvoiceRepo.SupplierNames());
        _company = FieldTable.AutoCombo(CompanyRepo.All().Select(c => (object)c.Name));
        _supplier.Leave += (_, _) => FillBinFromSupplier();
        _recognize = Ui.Button("Распознать заново", async (_, _) => await RecognizeAsync());
        _recognize.Enabled = file != null;

        BuildLayout();
        _total.TextChanged += (_, _) => UpdateTotals();

        if (draft != null) Fill(draft);
        else SetLines([]);

        Shown += async (_, _) =>
        {
            await _preview.LoadAsync(file);
            if (file != null && draft == null) await RecognizeAsync();
            else if (file == null && draft == null) SetStatus("Введите шапку и позиции вручную.", Ui.Muted);
            else if (ReceivesOrder) SetStatus("Позиции и получатели взяты из счёта — проверьте и нажмите «Принять».", Ui.Muted);
        };
        FormClosing += (_, _) => _cts?.Cancel();
    }

    private void BuildLayout()
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(0, 0, 0, 6) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        void Row(string l1, Control c1, string? l2 = null, Control? c2 = null)
        {
            var r = header.RowCount++;
            header.Controls.Add(Caption(l1), 0, r);
            c1.Dock = DockStyle.Fill;
            header.Controls.Add(c1, 1, r);
            if (c2 == null) { header.SetColumnSpan(c1, 3); return; }
            header.Controls.Add(Caption(l2!), 2, r);
            c2.Dock = DockStyle.Fill;
            header.Controls.Add(c2, 3, r);
        }
        Row(IsOrder ? "№ счёта" : "Номер", _number, "Дата", _date);
        Row("Поставщик", _supplier, "БИН", _bin);
        Row("Компания", _company, "Сумма", _total);
        Row("Заметки", _notes);

        var statusBar = Ui.Toolbar();
        statusBar.Controls.AddRange([_recognize, _progress, _status]);

        BuildLinesGrid();
        var linesPanel = new Panel { Dock = DockStyle.Fill };
        linesPanel.Controls.Add(_lines);
        linesPanel.Controls.Add(_totals);

        // «Кому выдать» / «Для кого» сразу всем строкам.
        var receive = Ui.Toolbar();
        receive.Dock = DockStyle.Bottom;
        receive.Padding = new Padding(0, 8, 0, 0);
        receive.Controls.AddRange([
            new Label { Text = IsOrder ? "Для кого — всем строкам:" : "Кому выдать — всем строкам:", AutoSize = true, Padding = new Padding(0, 6, 6, 0) },
            _allRecipient,
            Ui.Button("Применить", (_, _) => ApplyRecipientToAll()),
            new Label { Width = 16 },
            Ui.Button("Разбить строку по 1 шт.", (_, _) => SplitCurrentLine()),
        ]);

        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 6, 0) };
        left.Controls.Add(linesPanel);
        left.Controls.Add(receive);
        left.Controls.Add(statusBar);
        left.Controls.Add(header);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
        split.Panel1.Controls.Add(left);
        split.Panel2.Padding = new Padding(6, 12, 12, 0);
        split.Panel2.Controls.Add(_preview);
        Load += (_, _) => split.SplitterDistance = (int)(split.Width * 0.6);

        Controls.Add(split);
        var okText = IsOrder ? "Сохранить заказ" : "Принять";
        var buttons = FieldTable.DialogButtons(this, okText, (_, _) => Accept());
        _accept = buttons.Controls.OfType<Button>().Single(b => b.Text == okText);
        if (_allowSkip)
        {
            var skip = Ui.Button("Пропустить", (_, _) => { DialogResult = DialogResult.Ignore; Close(); });
            buttons.Controls.Add(skip);
            new ToolTip().SetToolTip(skip, "Не принимать эту накладную и убрать её из очереди");
        }
        Controls.Add(buttons);
    }

    private static Label Caption(string text) => new()
    {
        Text = text, ForeColor = Ui.Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
    };

    private void ApplyRecipientToAll()
    {
        _lines.EndEdit();
        var key = (_allRecipient.SelectedItem as RecipientOption)?.Key ?? 0;
        foreach (var l in _lineList.Where(l => !l.ToConsumables)) l.EmployeeKey = key;
        _lineList.ResetBindings();
    }

    private void FillBinFromSupplier()
    {
        if (!string.IsNullOrWhiteSpace(_bin.Text)) return;
        var s = InvoiceRepo.FindSupplier(_supplier.Text, null);
        if (s?.Bin != null) _bin.Text = s.Bin;
    }

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
    }

    private async Task RecognizeAsync()
    {
        if (_file == null) return;
        if (_lineList.Count > 0 && !Ui.Confirm(this, "Заменить введённые данные результатом распознавания?")) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _recognize.Enabled = _accept.Enabled = false;
        _progress.Visible = true;
        SetStatus("Готовлю распознавание…", Ui.Muted);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (recognizer, note) = await Recognizers.CreateAsync(AppSettings.Load(), ct);
            if (recognizer == null)
            {
                SetStatus((note ?? "Распознавание недоступно.") + " Заполните вручную.", Ui.Danger);
                return;
            }
            SetStatus($"Распознаю: {recognizer.Name}… " +
                      (recognizer is OllamaRecognizer ? "обычно 10–30 секунд на страницу" : "несколько секунд"), Ui.Muted);
            var recognized = await recognizer.RecognizeAsync(_file, ct);
            if (ct.IsCancellationRequested || IsDisposed) return;

            var draft = InvoiceDrafts.FromRecognized(recognized, _file);
            var matchNote = ReceivesOrder ? MatchWithOrder(draft) : "";
            Fill(draft);
            var next = IsOrder ? "Укажите, для кого каждая позиция, и нажмите «Сохранить заказ»." : "Проверьте и нажмите «Принять».";
            var done = $"Распознано ({recognizer.Name}, {watch.Elapsed.TotalSeconds:0} с), позиций: {recognized.Items.Count}.{matchNote} {next}";
            SetStatus(note == null ? done : note + " " + done, note == null ? Color.FromArgb(22, 163, 74) : Color.FromArgb(217, 119, 6));
            Log.Info($"Распознавание {Path.GetFileName(_file)}: {recognizer.Name}, {watch.Elapsed.TotalSeconds:0.0} с, позиций {recognized.Items.Count}");
        }
        catch (OperationCanceledException) { /* окно закрыли */ }
        catch (RecognitionException ex)
        {
            Log.Error("Распознавание: " + ex.Message, ex.InnerException);
            if (!IsDisposed)
            {
                if (ReceivesOrder && _lineList.Count == 0) Fill(OrderAsDraft());
                SetStatus(ex.Message + (ReceivesOrder ? " Позиции взяты из счёта." : " Можно заполнить вручную."), Ui.Danger);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Распознавание: непредвиденная ошибка", ex);
            if (!IsDisposed) SetStatus("Не удалось распознать: " + ex.Message, Ui.Danger);
        }
        finally
        {
            if (!IsDisposed)
            {
                _progress.Visible = false;
                _recognize.Enabled = true;
                _accept.Enabled = true;
            }
        }
    }

    /// <summary>Перенести из заказа получателей (и пустые поля шапки) в распознанную накладную.</summary>
    private string MatchWithOrder(InvoiceDraft draft)
    {
        var order = OrderRepo.ToDraft(_orderId!.Value);
        draft.SupplierName ??= order.SupplierName;
        draft.SupplierBin ??= order.SupplierBin;
        draft.CompanyName ??= order.CompanyName;
        if (draft.Lines.Count == 0)
        {
            draft.Lines = order.Lines;
            return " Позиций не нашлось — взяты из счёта.";
        }
        var total = draft.Lines.Sum(l => l.Quantity);
        var matched = OrderMatcher.Apply(draft.Lines, order.Lines);
        return $" Сопоставлено со счётом: {matched} шт. из {total:0.##}.";
    }

    /// <summary>Черновик прихода из заказа (если накладной нет или она не распозналась).</summary>
    private InvoiceDraft OrderAsDraft()
    {
        var order = OrderRepo.ToDraft(_orderId!.Value);
        order.Number = null;
        order.Date = DateTime.Today;
        order.SourceFile = _file;
        return order;
    }

    private void Fill(InvoiceDraft d)
    {
        _number.Text = d.Number ?? "";
        _date.Value = d.Date;
        _supplier.Text = d.SupplierName ?? "";
        _bin.Text = d.SupplierBin ?? "";
        if (!string.IsNullOrWhiteSpace(d.CompanyName)) _company.Text = d.CompanyName;
        _total.Text = d.Total?.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU")) ?? "";
        _notes.Text = d.Notes ?? "";
        SetLines(d.Lines);
    }

    internal static decimal? ParseMoney(string? text)
    {
        var t = (text ?? "").Replace(" ", "").Replace(" ", "").Replace(",", ".").Trim();
        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private InvoiceDraft CollectDraft() => new()
    {
        Number = _number.Val(),
        Date = _date.Value.Date,
        SupplierName = _supplier.Val(),
        SupplierBin = _bin.Val(),
        CompanyName = _company.Val(),
        Total = ParseMoney(_total.Text),
        Notes = _notes.Val(),
        SourceFile = _file,
        Lines = _lineList.Where(l => !string.IsNullOrWhiteSpace(l.Name)).ToList(),
        ItemStatus = ItemStatus.InStock,
        OrderId = ReceivesOrder ? _orderId : null,
    };

    /// <summary>«Иванов: 2 шт., Петров: 1 шт., на склад: 3 шт.» для подтверждения.</summary>
    private string RecipientsSummary(InvoiceDraft d)
    {
        var groups = d.Lines.Where(l => !l.ToConsumables)
            .GroupBy(l => l.EmployeeId)
            .Select(g => $"{(g.Key == null ? (IsOrder ? "не указано" : "на склад") : RecipientName(g.Key.Value))}: {g.Sum(l => (int)l.Quantity)} шт.");
        return string.Join("\n", groups);
    }

    private void Accept()
    {
        _lines.EndEdit();
        if (!string.IsNullOrWhiteSpace(_total.Text) && ParseMoney(_total.Text) == null)
        {
            Ui.Warn(this, "Сумма указана неверно.");
            return;
        }
        var d = CollectDraft();
        if (IsOrder) { SaveOrder(d); return; }

        if (InvoiceRepo.Validate(d) is { } error) { Ui.Warn(this, error); return; }
        var dup = InvoiceRepo.FindDuplicate(d.Number, d.SupplierName, d.SupplierBin);
        if (dup != null && !Ui.Confirm(this,
                $"Накладная № {dup.Number} от {dup.Date:dd.MM.yyyy} этого поставщика уже принята ({dup.ItemCount} вещей).\n\nПринять ещё раз?"))
            return;

        var cons = d.Lines.Count(l => l.ToConsumables);
        var orderNote = ReceivesOrder ? $"\n\nСчёт № {OrderRepo.Get(_orderId!.Value)?.Number ?? "—"} будет отмечен как «Получен»." : "";
        if (!Ui.Confirm(this, $"Принять накладную от {d.Date:dd.MM.yyyy}?\n\n{RecipientsSummary(d)}" +
                              (cons > 0 ? $"\nПозиций в расходники: {cons}" : "") + orderNote))
            return;

        Ui.Safe(this, () =>
        {
            AcceptedInvoiceId = InvoiceRepo.Accept(d);
            Log.Info($"Принята накладная № {d.Number} (id {AcceptedInvoiceId})" + (ReceivesOrder ? $" по заказу {_orderId}" : ""));
            DialogResult = DialogResult.OK;
            Close();
        }, "Не удалось принять накладную");
    }

    private void SaveOrder(InvoiceDraft d)
    {
        var sum = d.Lines.Sum(l => l.Sum ?? 0);
        if (!Ui.Confirm(this, $"Сохранить счёт № {d.Number ?? "—"} как заказ?\n\nПозиций: {d.Lines.Count}, на сумму {sum:#,0.##}\n{RecipientsSummary(d)}\n\n" +
                              "На склад ничего не попадёт, пока не придёт товар («Заказы» → «Товар пришёл»)."))
            return;
        Ui.Safe(this, () =>
        {
            SavedOrderId = OrderRepo.Save(d, _orderId);
            Log.Info($"Сохранён заказ по счёту № {d.Number} (id {SavedOrderId})");
            DialogResult = DialogResult.OK;
            Close();
        }, "Не удалось сохранить заказ");
    }
}
