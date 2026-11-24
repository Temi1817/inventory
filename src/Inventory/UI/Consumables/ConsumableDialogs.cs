using Inventory.Data;
using Inventory.Models;

namespace Inventory.UI.Consumables;

/// <summary>Добавить / изменить расходник.</summary>
public sealed class ConsumableForm : Form
{
    private readonly Consumable _item;
    private readonly TextBox _name = new();
    private readonly ComboBox _category;
    private readonly ComboBox _unit = FieldTable.AutoCombo(["шт", "упак", "пачка", "м", "рулон", "кг", "л", "компл"]);
    private readonly NumericUpDown _quantity = Number();
    private readonly NumericUpDown _min = Number();
    private readonly TextBox _notes = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };

    private static NumericUpDown Number() => new() { Maximum = 1_000_000, DecimalPlaces = 2, ThousandsSeparator = true, Width = 140 };

    public ConsumableForm(Consumable? item = null)
    {
        _item = item ?? new Consumable { Unit = "шт" };
        Text = item == null ? "Новый расходник" : "Расходник: " + item.Name;
        Font = Ui.BaseFont;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(480, 360);

        _category = FieldTable.AutoCombo(ConsumableRepo.Categories());
        var table = new FieldTable(140) { Padding = new Padding(12, 12, 12, 0) };
        table.Add("Наименование *", _name);
        table.Add("Категория", _category);
        table.Add("Единица", _unit);
        if (item == null) table.Add("Начальный остаток", _quantity);
        else table.Add("Остаток", new Label { Text = $"{item.Quantity:0.##} {item.Unit} (меняется приходом/выдачей)", AutoSize = true, ForeColor = Ui.Muted, Padding = new Padding(0, 5, 0, 0) });
        table.Add("Минимум", _min);
        table.Add("Заметки", _notes, height: 80);
        Controls.Add(table);
        Controls.Add(FieldTable.DialogButtons(this, "Сохранить", (_, _) => Save()));

        _name.Text = _item.Name;
        _category.Text = _item.Category ?? "";
        _unit.Text = _item.Unit ?? "шт";
        _min.Value = Math.Min(_min.Maximum, _item.MinQuantity);
        _notes.Text = _item.Notes ?? "";
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) { Ui.Warn(this, "Укажите наименование."); return; }
        Ui.Safe(this, () =>
        {
            _item.Name = _name.Text;
            _item.Category = _category.Text;
            _item.Unit = _unit.Text;
            _item.MinQuantity = _min.Value;
            _item.Notes = _notes.Text;
            if (_item.Id == 0)
            {
                _item.Quantity = _quantity.Value;
                ConsumableRepo.Insert(_item);
            }
            else ConsumableRepo.Update(_item);
            DialogResult = DialogResult.OK;
            Close();
        }, "Не удалось сохранить расходник");
    }
}

/// <summary>«+ Приход» или «− Выдал»: количество, кому, дата, комментарий.</summary>
public sealed class ConsumableMoveDialog : Form
{
    private sealed record EmployeeOption(Employee E)
    {
        public override string ToString() => E.FullName;
    }

    private readonly Consumable _item;
    private readonly bool _income;
    private readonly NumericUpDown _quantity = new() { Minimum = 0.01m, Maximum = 1_000_000, DecimalPlaces = 2, Value = 1, Width = 140, ThousandsSeparator = true };
    private readonly ComboBox _employee = new() { DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
    private readonly DateTimePicker _date = new() { Format = DateTimePickerFormat.Short, Value = DateTime.Today };
    private readonly TextBox _comment = new();

    public ConsumableMoveDialog(Consumable item, bool income)
    {
        _item = item;
        _income = income;
        Text = (income ? "Приход: " : "Выдача: ") + item.Name;
        Font = Ui.BaseFont;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(480, income ? 220 : 260);

        var table = new FieldTable(120) { Padding = new Padding(12, 12, 12, 0) };
        table.Add("Остаток", new Label { Text = $"{item.Quantity:0.##} {item.Unit}", AutoSize = true, Font = Ui.BoldFont, Padding = new Padding(0, 5, 0, 0) });
        table.Add($"Количество, {item.Unit}", _quantity);
        if (!income)
        {
            _employee.Items.AddRange(EmployeeRepo.List().Select(e => (object)new EmployeeOption(e)).ToArray());
            table.Add("Кому", _employee);
        }
        table.Add("Дата", _date);
        table.Add("Комментарий", _comment);
        Controls.Add(table);
        Controls.Add(FieldTable.DialogButtons(this, income ? "Оприходовать" : "Выдать", (_, _) => Save()));
        Shown += (_, _) => { _quantity.Select(0, _quantity.Text.Length); _quantity.Focus(); };
    }

    private void Save()
    {
        long? employeeId = null;
        if (!_income && !string.IsNullOrWhiteSpace(_employee.Text))
        {
            employeeId = (_employee.SelectedItem as EmployeeOption)?.E.Id
                         ?? _employee.Items.OfType<EmployeeOption>().FirstOrDefault(o => o.E.FullName.Equals(_employee.Text.Trim(), StringComparison.CurrentCultureIgnoreCase))?.E.Id;
            if (employeeId == null) { Ui.Warn(this, "Выберите сотрудника из списка или оставьте поле пустым."); return; }
        }
        var qty = _income ? _quantity.Value : -_quantity.Value;
        Ui.Safe(this, () =>
        {
            ConsumableRepo.Move(_item.Id, qty, _date.Value.Date, employeeId, _comment.Text);
            DialogResult = DialogResult.OK;
            Close();
        }, "Не удалось сохранить движение");
    }
}
