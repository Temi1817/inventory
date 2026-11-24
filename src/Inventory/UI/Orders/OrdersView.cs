using System.Diagnostics;
using Inventory.Data;
using Inventory.Infrastructure;
using Inventory.Models;
using Inventory.Services.Claude;
using Inventory.UI.Arrival;
using Inventory.UI.Invoices;

namespace Inventory.UI.Orders;

/// <summary>Раздел «Заказы»: счета на оплату — что купить и для кого; оплата; приход товара по накладной.</summary>
public sealed class OrdersView : UserControl, IView
{
    private readonly DataGridView _grid = new();
    private readonly DataGridView _lines = new();
    private readonly Label _linesTitle = new() { Font = Ui.BoldFont, Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.BottomLeft };
    private readonly TextBox _filter = new() { Width = 230, PlaceholderText = "№ счёта, поставщик, товар, ФИО…" };
    private readonly CheckBox _showClosed = new() { Text = "Показать полученные и отменённые", AutoSize = true, Padding = new Padding(8, 6, 0, 0) };
    private readonly Label _summary = new() { AutoSize = true, ForeColor = Ui.Muted, Padding = new Padding(8, 7, 0, 0) };
    private readonly Button _payButton, _receiveButton, _cancelButton;
    private readonly System.Windows.Forms.Timer _delay = new() { Interval = 250 };

    public OrdersView()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(16);
        AllowDrop = true;

        Ui.StyleGrid(_grid);
        _grid.Columns.AddRange(
            Ui.Col(nameof(OrderRow.Number), "№ счёта", 55),
            Ui.Col(nameof(OrderRow.Date), "Дата", 55, "dd.MM.yyyy"),
            Ui.Col(nameof(OrderRow.SupplierName), "Поставщик", 130),
            Ui.Col(nameof(OrderRow.Amount), "Сумма", 65, "#,0.##"),
            Ui.Col(nameof(OrderRow.LineCount), "Позиций", 40),
            Ui.Col(nameof(OrderRow.RecipientsText), "Для кого", 160),
            Ui.Col(nameof(OrderRow.Status), "Статус", 65),
            Ui.Col(nameof(OrderRow.PaidAt), "Оплачен", 55, "dd.MM.yyyy"),
            Ui.Col(nameof(OrderRow.InvoiceNumber), "Накладная", 55));
        _grid.Columns[nameof(OrderRow.Amount)]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSelected(); };
        _grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; } };
        _grid.SelectionChanged += (_, _) => { LoadLines(); UpdateButtons(); };
        _grid.CellFormatting += OnCellFormatting;

        Ui.StyleGrid(_lines);
        _lines.Columns.AddRange(
            Ui.Col(nameof(OrderLineRow.Name), "Что покупаем", 240),
            Ui.Col(nameof(OrderLineRow.Quantity), "Кол-во", 40, "0.##"),
            Ui.Col(nameof(OrderLineRow.Unit), "Ед.", 30),
            Ui.Col(nameof(OrderLineRow.Price), "Цена", 60, "#,0.##"),
            Ui.Col(nameof(OrderLineRow.Sum), "Сумма", 65, "#,0.##"),
            Ui.Col(nameof(OrderLineRow.Recipient), "Для кого", 150));

        _payButton = Ui.Button("Оплачен", (_, _) => TogglePaid());
        _receiveButton = Ui.Button("Товар пришёл…", (_, _) => Receive(), primary: true);
        _cancelButton = Ui.Button("Отменить", (_, _) => CancelOrReopen());

        var toolbar = Ui.Toolbar();
        toolbar.Controls.AddRange([
            Ui.Button("Загрузить счёт на оплату…", (_, _) => NewFromFile(), primary: true),
            Ui.Button("Ввести вручную", (_, _) => NewOrder(null)),
            new Label { Width = 16 },
            Ui.Button("Открыть", (_, _) => OpenSelected()),
            _payButton, _receiveButton, _cancelButton,
            Ui.Button("Удалить", (_, _) => DeleteSelected()),
        ]);
        var filters = Ui.Toolbar();
        filters.Controls.AddRange([_filter, _showClosed, _summary]);
        _delay.Tick += (_, _) => { _delay.Stop(); RefreshData(); };
        _filter.TextChanged += (_, _) => { _delay.Stop(); _delay.Start(); };
        _showClosed.CheckedChanged += (_, _) => RefreshData();

        var linesPanel = new Panel { Dock = DockStyle.Bottom, Height = 220 };
        linesPanel.Controls.Add(_lines);
        linesPanel.Controls.Add(_linesTitle);

        Controls.Add(_grid);
        Controls.Add(linesPanel);
        Controls.Add(filters);
        Controls.Add(toolbar);
        Controls.Add(new Label
        {
            Text = "Перетащите файл счёта сюда — будет новый заказ. Перетащите накладную на строку заказа — товар пришёл по этому счёту.",
            Dock = DockStyle.Top, Height = 24, ForeColor = Ui.Muted,
        });
        Controls.Add(Ui.Title("Заказы (счета на оплату)"));
        EnableDrop(this);
    }

    /// <summary>
    /// Перетаскивание файлов: на строку открытого заказа — накладная по нему, в любое другое место — новый счёт.
    /// AllowDrop ставим всем дочерним контролам: события перетаскивания не всплывают к родителю.
    /// </summary>
    private void EnableDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += OnDragOver;
        control.DragOver += OnDragOver;
        control.DragDrop += OnDragDrop;
        foreach (Control child in control.Controls) EnableDrop(child);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (DroppedFile(e.Data) == null) { e.Effect = DragDropEffects.None; return; }
        e.Effect = DragDropEffects.Copy;
        // Подсветить заказ под курсором — видно, к какому счёту пойдёт накладная.
        if (sender == _grid && OrderAt(e) is { } row && _grid.CurrentCell?.RowIndex != row)
            _grid.CurrentCell = _grid.Rows[row].Cells[0];
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (DroppedFile(e.Data) is not { } file) return;
        var order = sender == _grid && OrderAt(e) is { } row ? _grid.Rows[row].DataBoundItem as OrderRow : null;
        // BeginInvoke — чтобы Проводник не «висел», пока открыто наше окно.
        if (order != null && OrderStatus.IsOpen(order.Status)) BeginInvoke(() => ReceiveFromFile(order, file));
        else BeginInvoke(() => NewOrder(file));
    }

    private int? OrderAt(DragEventArgs e)
    {
        var p = _grid.PointToClient(new Point(e.X, e.Y));
        var hit = _grid.HitTest(p.X, p.Y);
        return hit.RowIndex >= 0 ? hit.RowIndex : null;
    }

    private static string? DroppedFile(IDataObject? data) =>
        data?.GetData(DataFormats.FileDrop) is string[] files ? files.FirstOrDefault(InvoiceRecognizer.IsSupported) : null;

    private OrderRow? Selected => _grid.CurrentRow?.DataBoundItem as OrderRow;

    public void RefreshData() => Ui.Safe(FindForm(), () =>
    {
        var keep = Selected?.Id;
        var rows = OrderRepo.List(_showClosed.Checked, _filter.Text);
        _grid.DataSource = rows;
        if (keep != null)
            foreach (DataGridViewRow r in _grid.Rows)
                if (((OrderRow)r.DataBoundItem!).Id == keep) { _grid.CurrentCell = r.Cells[0]; break; }
        var open = OrderRepo.List(false);
        var unpaid = open.Where(o => o.Status == OrderStatus.AwaitingPayment).ToList();
        _summary.Text = $"Ждут оплаты: {unpaid.Count} на {unpaid.Sum(o => o.Amount ?? 0):#,0}   ·   ждут товара: {open.Count}";
        LoadLines();
        UpdateButtons();
    }, "Не удалось загрузить заказы");

    private void LoadLines()
    {
        if (Selected is not { } o) { _lines.DataSource = null; _linesTitle.Text = "Позиции"; return; }
        _linesTitle.Text = $"Позиции счёта {o.Title}" + (o.SupplierName != null ? $" — {o.SupplierName}" : "");
        _lines.DataSource = OrderRepo.LineRows(o.Id);
    }

    private void UpdateButtons()
    {
        var o = Selected;
        var open = o != null && OrderStatus.IsOpen(o.Status);
        _payButton.Enabled = open;
        _payButton.Text = o?.Status == OrderStatus.Paid ? "Снять оплату" : "Оплачен";
        _receiveButton.Enabled = open;
        _cancelButton.Enabled = o != null && o.Status != OrderStatus.Received;
        _cancelButton.Text = o?.Status == OrderStatus.Cancelled ? "Вернуть в работу" : "Отменить";
    }

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != nameof(OrderRow.Status)) return;
        e.CellStyle!.ForeColor = (e.Value as string) switch
        {
            OrderStatus.AwaitingPayment => Color.FromArgb(217, 119, 6),
            OrderStatus.Paid => Ui.Accent,
            OrderStatus.Received => Color.FromArgb(22, 163, 74),
            _ => Color.Gray,
        };
    }

    private void NewFromFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Счёт на оплату (фото или PDF)",
            Filter = "Счета|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.pdf|Все файлы|*.*",
        };
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) NewOrder(dlg.FileName);
    }

    private void NewOrder(string? file)
    {
        using var form = new InvoiceEditForm(file, mode: EditorMode.Order);
        if (form.ShowDialog(FindForm()) == DialogResult.OK) RefreshData();
    }

    private void OpenSelected()
    {
        if (Selected is not { } o) return;
        if (o.Status == OrderStatus.Received && o.InvoiceId is { } invoiceId)
        {
            InvoiceCard.Open(FindForm(), invoiceId);
            return;
        }
        if (!OrderStatus.IsOpen(o.Status))
        {
            OpenFile(o);
            return;
        }
        var draft = OrderRepo.ToDraft(o.Id);
        using var form = new InvoiceEditForm(draft.SourceFile, draft, $"Счёт на оплату {o.Title}", mode: EditorMode.Order, orderId: o.Id);
        if (form.ShowDialog(FindForm()) == DialogResult.OK) RefreshData();
    }

    private void OpenFile(OrderRow o)
    {
        if (o.FilePath == null || !File.Exists(AppPaths.ToAbsolute(o.FilePath))) { Ui.Info(FindForm(), $"Заказ «{o.Status}», файла счёта нет."); return; }
        Ui.Safe(FindForm(), () => Process.Start(new ProcessStartInfo(AppPaths.ToAbsolute(o.FilePath)) { UseShellExecute = true }));
    }

    private void TogglePaid()
    {
        if (Selected is not { } o) return;
        if (o.Status == OrderStatus.Paid)
        {
            if (!Ui.Confirm(FindForm(), $"Снять отметку об оплате счёта {o.Title}?")) return;
            Ui.Safe(FindForm(), () => OrderRepo.SetPaid(o.Id, null));
        }
        else
        {
            using var d = new PaidDateDialog(o.Title);
            if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
            Ui.Safe(FindForm(), () => OrderRepo.SetPaid(o.Id, d.Date));
        }
        RefreshData();
    }

    /// <summary>Товар пришёл: накладная (файл) или без неё — по позициям счёта.</summary>
    private void Receive()
    {
        if (Selected is not { } o) return;
        var choice = MessageBox.Show(FindForm(),
            $"Товар по счёту {o.Title} пришёл.\n\n«Да» — выбрать файл накладной (распознается и сопоставится со счётом).\n" +
            "«Нет» — накладной нет, принять по позициям счёта.",
            "Приход по заказу", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (choice == DialogResult.Cancel) return;

        string? file = null;
        InvoiceDraft? draft = null;
        if (choice == DialogResult.Yes)
        {
            using var dlg = new OpenFileDialog
            {
                Title = $"Накладная по счёту {o.Title}",
                Filter = "Накладные|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.pdf|Все файлы|*.*",
            };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            file = dlg.FileName;
        }
        else
        {
            draft = OrderRepo.ToDraft(o.Id);
            draft.SourceFile = null;
            draft.Number = null;
            draft.Date = DateTime.Today;
            draft.Notes = $"Без накладной, по счёту {o.Title}";
        }

        ReceiveWith(o, file, draft);
    }

    /// <summary>Накладную перетащили на строку заказа.</summary>
    private void ReceiveFromFile(OrderRow o, string file)
    {
        var choice = MessageBox.Show(FindForm(),
            $"Файл «{Path.GetFileName(file)}» — это накладная по счёту {o.Title}?\n\n" +
            "«Да» — товар по этому счёту пришёл, принять по накладной.\n«Нет» — это новый счёт на оплату.",
            "Перетащили файл", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (choice == DialogResult.Yes) ReceiveWith(o, file, null);
        else if (choice == DialogResult.No) NewOrder(file);
    }

    private void ReceiveWith(OrderRow o, string? file, InvoiceDraft? draft)
    {
        using var form = new InvoiceEditForm(file, draft, $"Приход по счёту {o.Title}", mode: EditorMode.Invoice, orderId: o.Id);
        if (form.ShowDialog(FindForm()) != DialogResult.OK) return;
        RefreshData();
        Ui.Info(FindForm(), $"Товар по счёту {o.Title} принят, заказ закрыт.\nВещи выданы получателям — их видно на страницах сотрудников и на складе.");
    }

    private void CancelOrReopen()
    {
        if (Selected is not { } o) return;
        if (o.Status == OrderStatus.Cancelled)
            Ui.Safe(FindForm(), () => OrderRepo.Reopen(o.Id));
        else
        {
            if (!Ui.Confirm(FindForm(), $"Отменить заказ по счёту {o.Title}?\nОн останется в списке «отменённых», его можно вернуть.")) return;
            Ui.Safe(FindForm(), () => OrderRepo.Cancel(o.Id));
        }
        RefreshData();
    }

    private void DeleteSelected()
    {
        if (Selected is not { } o) return;
        if (o.Status == OrderStatus.Received) { Ui.Warn(FindForm(), "По заказу уже получен товар — удалить нельзя."); return; }
        if (!Ui.Confirm(FindForm(), $"Удалить заказ по счёту {o.Title} вместе с файлом счёта?\nЭто нельзя отменить. Если просто не нужен — лучше «Отменить».")) return;
        Ui.Safe(FindForm(), () => OrderRepo.Delete(o.Id));
        RefreshData();
    }
}

/// <summary>Дата оплаты счёта.</summary>
public sealed class PaidDateDialog : Form
{
    private readonly DateTimePicker _date = new() { Format = DateTimePickerFormat.Short, Value = DateTime.Today };
    public DateTime Date => _date.Value.Date;

    public PaidDateDialog(string title)
    {
        Text = "Оплата счёта " + title;
        Font = Ui.BaseFont;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(360, 120);
        var table = new FieldTable(110) { Padding = new Padding(12, 16, 12, 0) };
        table.Add("Дата оплаты", _date);
        Controls.Add(table);
        Controls.Add(FieldTable.DialogButtons(this, "Оплачен", (_, _) => { DialogResult = DialogResult.OK; Close(); }));
    }
}
