using System.ComponentModel;
using System.Globalization;
using Inventory.Data;
using Inventory.Models;

namespace Inventory.UI.Arrival;

/// <summary>Таблица позиций накладной (редактируемая).</summary>
public sealed partial class InvoiceEditForm
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly DataGridView _lines = new();
    private readonly Label _totals = new() { Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft };
    private BindingList<InvoiceLine> _lineList = [];
    private AutoCompleteStringCollection? _categories;
    private List<RecipientOption> _recipients = [];

    /// <summary>Вариант в списке «Кому выдать»: Key = id сотрудника, 0 — никому (склад).</summary>
    private sealed record RecipientOption(long Key, string Name)
    {
        public override string ToString() => Name;
    }

    private string RecipientName(long id) => _recipients.FirstOrDefault(r => r.Key == id)?.Name ?? "?";

    private void LoadRecipients()
    {
        var none = IsOrder ? "— не указано —" : "— на склад —";
        // Уволенные тоже в списке (могли быть указаны в старом заказе), но помечены.
        _recipients = [new RecipientOption(0, none), .. EmployeeRepo.List(includeInactive: true)
            .OrderBy(e => !e.IsActive).ThenBy(e => e.FullName)
            .Select(e => new RecipientOption(e.Id, e.FullName + (e.IsActive ? "" : " (уволен)")))];
        _allRecipient.Items.Clear();
        _allRecipient.Items.AddRange(_recipients.Where(r => !r.Name.EndsWith("(уволен)")).Cast<object>().ToArray());
        _allRecipient.SelectedIndex = 0;
    }

    private void BuildLinesGrid()
    {
        LoadRecipients();
        Ui.StyleGrid(_lines);
        _lines.ReadOnly = false;
        _lines.AllowUserToAddRows = true;
        _lines.AllowUserToDeleteRows = true;
        _lines.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _lines.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;

        var money = "#,0.##";
        _lines.Columns.AddRange(
            Ui.Col(nameof(InvoiceLine.Name), "Наименование", 210),
            Ui.Col(nameof(InvoiceLine.Category), "Категория", 90),
            Ui.Col(nameof(InvoiceLine.Quantity), "Кол-во", 58, "0.###"),
            Ui.Col(nameof(InvoiceLine.Unit), "Ед.", 35),
            Ui.Col(nameof(InvoiceLine.Price), "Цена", 65, money),
            Ui.Col(nameof(InvoiceLine.Sum), "Сумма", 70, money),
            new DataGridViewCheckBoxColumn
            {
                DataPropertyName = nameof(InvoiceLine.ToConsumables), HeaderText = "Расходник", ToolTipText = "Не заводить карточки, а добавить в остаток расходников",
                Name = nameof(InvoiceLine.ToConsumables), FillWeight = 80,
            },
            new DataGridViewComboBoxColumn
            {
                DataPropertyName = nameof(InvoiceLine.EmployeeKey), Name = nameof(InvoiceLine.EmployeeKey),
                HeaderText = IsOrder ? "Для кого" : "Кому выдать", FillWeight = 130,
                DataSource = _recipients, ValueMember = nameof(RecipientOption.Key), DisplayMember = nameof(RecipientOption.Name),
                DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox, FlatStyle = FlatStyle.Flat,
                ToolTipText = IsOrder ? "Для кого покупаем эту позицию" : "Кому сразу выдать вещи из этой строки (пусто — на склад)",
            });
        foreach (DataGridViewColumn c in _lines.Columns) c.ReadOnly = false;
        _lines.Columns[nameof(InvoiceLine.Sum)]!.ReadOnly = true;
        _lines.Columns[nameof(InvoiceLine.Sum)]!.DefaultCellStyle.ForeColor = Ui.Muted;
        foreach (var name in new[] { nameof(InvoiceLine.Quantity), nameof(InvoiceLine.Price), nameof(InvoiceLine.Sum) })
            _lines.Columns[name]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _lines.DefaultCellStyle.FormatProvider = Ru;

        _lines.CellParsing += OnCellParsing;
        _lines.DataError += (_, e) =>
        {
            e.ThrowException = false;
            SetStatus("Неверное значение в ячейке — для чисел используйте цифры, например 1,5.", Ui.Danger);
        };
        _lines.CellValueChanged += (_, e) => { if (e.RowIndex >= 0) _lines.InvalidateRow(e.RowIndex); UpdateTotals(); };
        _lines.UserDeletedRow += (_, _) => UpdateTotals();
        // Галочка применяется сразу, без ухода из ячейки.
        _lines.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_lines.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
                _lines.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        // Правый клик по строке: «Разбить по 1 шт.».
        var menu = new ContextMenuStrip();
        menu.Items.Add("Разбить строку по 1 шт. (разным получателям)", null, (_, _) => SplitCurrentLine());
        _lines.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.RowIndex >= _lineList.Count) return;
            _lines.CurrentCell = _lines.Rows[e.RowIndex].Cells[Math.Max(0, e.ColumnIndex)];
            menu.Show(Cursor.Position);
        };
        // Список получателей открывается с одного клика.
        _lines.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 || _lines.Columns[e.ColumnIndex].Name != nameof(InvoiceLine.EmployeeKey)) return;
            _lines.BeginEdit(true);
            if (_lines.EditingControl is ComboBox cb) cb.DroppedDown = true;
        };
        _lines.EditingControlShowing += OnEditingControlShowing;
        _lines.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _lineList.Count) return;
            var line = _lineList[e.RowIndex];
            if (line.ToConsumables) e.CellStyle!.BackColor = Color.FromArgb(254, 249, 195);
            // Расходники не выдаются через накладную — получатель для них не используется.
            if (line.ToConsumables && _lines.Columns[e.ColumnIndex].Name == nameof(InvoiceLine.EmployeeKey))
                e.CellStyle!.ForeColor = Color.Silver;
        };
    }

    /// <summary>«Клавиатура × 2» → две строки по 1 шт., чтобы назначить разных получателей.</summary>
    private void SplitCurrentLine()
    {
        _lines.EndEdit();
        var index = _lines.CurrentCell?.RowIndex ?? -1;
        if (index < 0 || index >= _lineList.Count)
        {
            Ui.Warn(this, "Выберите строку в таблице (щёлкните по ней).");
            return;
        }
        var line = _lineList[index];
        if (!Services.InvoiceDrafts.CanSplit(line))
        {
            Ui.Warn(this, line.ToConsumables
                ? "Расходник не разбивается — он идёт в остаток целиком."
                : "Разбить можно строку с целым количеством больше 1.");
            return;
        }
        var parts = Services.InvoiceDrafts.SplitByOne(line);
        var all = _lineList.ToList();
        all.RemoveAt(index);
        all.InsertRange(index, parts);
        SetLines(all);
        _lines.CurrentCell = _lines.Rows[index].Cells[nameof(InvoiceLine.EmployeeKey)];
        SetStatus($"Строка разбита на {parts.Count} — выберите получателя для каждой.", Ui.Muted);
    }

    private void SetLines(IEnumerable<InvoiceLine> lines)
    {
        _lineList = new BindingList<InvoiceLine>(lines.ToList()) { AllowNew = true };
        _lineList.ListChanged += (_, _) => UpdateTotals();
        _lines.DataSource = _lineList;
        UpdateTotals();
    }

    private void OnCellParsing(object? sender, DataGridViewCellParsingEventArgs e)
    {
        var name = _lines.Columns[e.ColumnIndex].Name;
        if (name is not (nameof(InvoiceLine.Quantity) or nameof(InvoiceLine.Price))) return;
        var text = e.Value?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            e.Value = name == nameof(InvoiceLine.Price) ? null : 1m;
            e.ParsingApplied = true;
            return;
        }
        if (ParseMoney(text) is { } v)
        {
            e.Value = v;
            e.ParsingApplied = true;
        }
    }

    private void OnEditingControlShowing(object? sender, DataGridViewEditingControlShowingEventArgs e)
    {
        if (e.Control is not TextBox tb) return;
        if (_lines.CurrentCell?.OwningColumn?.Name == nameof(InvoiceLine.Category))
        {
            if (_categories == null)
            {
                _categories = [];
                _categories.AddRange(ItemRepo.Categories().Concat(
                    ["Ноутбук", "Компьютер", "Монитор", "Принтер", "МФУ", "Телефон", "Сетевое оборудование",
                     "Периферия", "Комплектующие", "Кабели", "Картриджи", "Канцелярия", "Мебель", "Прочее"])
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            }
            tb.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            tb.AutoCompleteSource = AutoCompleteSource.CustomSource;
            tb.AutoCompleteCustomSource = _categories;
        }
        else tb.AutoCompleteMode = AutoCompleteMode.None;
    }

    private void UpdateTotals()
    {
        var lines = _lineList.Where(l => !string.IsNullOrWhiteSpace(l.Name)).ToList();
        var cards = lines.Where(l => !l.ToConsumables).Sum(l => l.Quantity);
        var sum = lines.Sum(l => l.Sum ?? 0);
        var text = $"Позиций: {lines.Count}   ·   {(IsOrder ? "штук" : "карточек будет")}: {cards:0.##}   ·   сумма по строкам: {sum.ToString("#,0.##", Ru)}";

        var total = ParseMoney(_total.Text);
        var mismatch = total != null && sum > 0 && Math.Abs(total.Value - sum) > 1;
        if (mismatch) text += $"   ·   ≠ сумме накладной {total!.Value.ToString("#,0.##", Ru)}";
        _totals.Text = text;
        _totals.ForeColor = mismatch ? Ui.Danger : Ui.Muted;
    }
}
