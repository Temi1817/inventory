using Inventory.Data;
using Inventory.Models;

namespace Inventory.UI.Employees;

/// <summary>Добавление / изменение сотрудника.</summary>
public sealed class EmployeeForm : Form
{
    private readonly Employee _employee;
    private readonly TextBox _name = new();
    private readonly ComboBox _company;
    private readonly ComboBox _department;
    private readonly ComboBox _position;
    private readonly TextBox _phone = new();
    private readonly TextBox _workplace = new();
    private readonly TextBox _notes = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };

    public EmployeeForm(Employee? employee = null)
    {
        _employee = employee ?? new Employee();
        Text = employee == null ? "Новый сотрудник" : "Сотрудник: " + employee.FullName;
        Font = Ui.BaseFont;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(520, 420);

        _company = FieldTable.AutoCombo(CompanyRepo.All().Select(c => (object)c.Name));
        _department = FieldTable.AutoCombo(EmployeeRepo.Departments());
        _position = FieldTable.AutoCombo(EmployeeRepo.Positions());

        var table = new FieldTable(150) { Padding = new Padding(12, 12, 12, 0) };
        table.Add("ФИО *", _name);
        table.Add("Компания", _company);
        table.Add("Отдел", _department);
        table.Add("Должность", _position);
        table.Add("Телефон", _phone);
        table.Add("Рабочее место / ПК", _workplace);
        table.Add("Заметки", _notes, height: 120);

        Controls.Add(table);
        Controls.Add(FieldTable.DialogButtons(this, "Сохранить", (_, _) => Save()));

        _name.Text = _employee.FullName;
        _company.Text = _employee.CompanyName ?? "";
        _department.Text = _employee.Department ?? "";
        _position.Text = _employee.Position ?? "";
        _phone.Text = _employee.Phone ?? "";
        _workplace.Text = _employee.Workplace ?? "";
        _notes.Text = _employee.Notes ?? "";
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            Ui.Warn(this, "Укажите ФИО.");
            _name.Focus();
            return;
        }

        Ui.Safe(this, () =>
        {
            _employee.FullName = _name.Text;
            _employee.CompanyId = CompanyRepo.GetOrCreate(_company.Text);
            _employee.Department = _department.Text;
            _employee.Position = _position.Text;
            _employee.Phone = _phone.Text;
            _employee.Workplace = _workplace.Text;
            _employee.Notes = _notes.Text;

            if (_employee.Id == 0)
            {
                var same = EmployeeRepo.FindByName(_employee.FullName);
                if (same != null && !Ui.Confirm(this, $"Сотрудник «{same.FullName}» уже есть. Всё равно добавить?"))
                    return;
                EmployeeRepo.Insert(_employee);
            }
            else EmployeeRepo.Update(_employee);

            DialogResult = DialogResult.OK;
            Close();
        }, "Не удалось сохранить сотрудника");
    }
}
