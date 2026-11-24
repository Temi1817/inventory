using Inventory.Data;
using Inventory.Models;

namespace Inventory.UI.Moves;

/// <summary>
/// Диалог движения: (сотрудник с поиском по ФИО) + дата + комментарий.
/// </summary>
public sealed class MoveDialog : Form
{
    private sealed record EmployeeOption(Employee E)
    {
        public override string ToString() =>
            E.FullName + (string.IsNullOrEmpty(E.Department) ? "" : $"   ·  {E.Department}") +
            (string.IsNullOrEmpty(E.CompanyName) ? "" : $"   ·  {E.CompanyName}");
    }

    private readonly TextBox _search = new() { PlaceholderText = "Начните вводить ФИО…" };
    private readonly ListBox _employees = new() { IntegralHeight = false, Height = 220 };
    private readonly DateTimePicker _date = new() { Format = DateTimePickerFormat.Short, Value = DateTime.Today };
    private readonly TextBox _comment = new();
    private readonly List<EmployeeOption> _allEmployees = [];
    private readonly bool _chooseEmployee;

    public long? EmployeeId { get; private set; }
    public DateTime Date => _date.Value.Date;
    public string? Comment => _comment.Val();

    /// <param name="title">Заголовок окна (Выдача, Возврат…).</param>
    /// <param name="itemsText">Что перемещаем — показывается сверху.</param>
    /// <param name="chooseEmployee">Нужно ли выбирать сотрудника.</param>
    /// <param name="excludeEmployeeId">Кого не показывать в списке (текущий владелец при передаче).</param>
    /// <param name="fixedEmployee">Сотрудник уже известен — только показать.</param>
    public MoveDialog(string title, string itemsText, string okText, bool chooseEmployee = false,
        long? excludeEmployeeId = null, Employee? fixedEmployee = null, string commentLabel = "Комментарий")
    {
        _chooseEmployee = chooseEmployee;
        Text = title;
        Font = Ui.BaseFont;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(560, chooseEmployee ? 470 : 230);

        var table = new FieldTable(110) { Padding = new Padding(12, 12, 12, 0) };
        table.Add("Вещи", new Label { Text = itemsText, AutoSize = true, MaximumSize = new Size(410, 0), Font = Ui.BoldFont });
        if (fixedEmployee != null)
            table.Add("Сотрудник", new Label { Text = fixedEmployee.FullName, AutoSize = true, Font = Ui.BoldFont });
        if (chooseEmployee)
        {
            table.Add("Сотрудник", _search);
            table.Add("", _employees);
            foreach (var e in EmployeeRepo.List().Where(e => e.Id != excludeEmployeeId))
                _allEmployees.Add(new EmployeeOption(e));
            FilterEmployees();
            _search.TextChanged += (_, _) => FilterEmployees();
            _search.KeyDown += OnSearchKeyDown;
            _employees.DoubleClick += (_, _) => Ok();
        }
        table.Add("Дата", _date);
        table.Add(commentLabel, _comment);

        Controls.Add(table);
        Controls.Add(FieldTable.DialogButtons(this, okText, (_, _) => Ok()));
        Shown += (_, _) => (chooseEmployee ? _search : _comment).Focus();
        // Высота по содержимому (список вещей бывает многострочным).
        Load += (_, _) =>
        {
            PerformLayout();
            ClientSize = new Size(ClientSize.Width, table.Height + 60);
        };
    }

    private void FilterEmployees()
    {
        var q = _search.Text.Trim();
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _employees.BeginUpdate();
        _employees.Items.Clear();
        foreach (var opt in _allEmployees.Where(o =>
                     words.All(w => o.E.FullName.Contains(w, StringComparison.CurrentCultureIgnoreCase))))
            _employees.Items.Add(opt);
        _employees.EndUpdate();
        if (_employees.Items.Count > 0) _employees.SelectedIndex = 0;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_employees.Items.Count == 0) return;
        if (e.KeyCode == Keys.Down) { _employees.SelectedIndex = Math.Min(_employees.SelectedIndex + 1, _employees.Items.Count - 1); e.Handled = true; }
        if (e.KeyCode == Keys.Up) { _employees.SelectedIndex = Math.Max(_employees.SelectedIndex - 1, 0); e.Handled = true; }
        if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Ok(); }
    }

    private void Ok()
    {
        if (_chooseEmployee)
        {
            if (_employees.SelectedItem is not EmployeeOption opt)
            {
                Ui.Warn(this, "Выберите сотрудника из списка.");
                _search.Focus();
                return;
            }
            EmployeeId = opt.E.Id;
        }
        if (Date > DateTime.Today && !Ui.Confirm(this, "Дата в будущем. Продолжить?")) return;
        DialogResult = DialogResult.OK;
        Close();
    }
}
