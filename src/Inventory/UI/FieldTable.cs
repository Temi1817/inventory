namespace Inventory.UI;

/// <summary>Таблица «подпись — поле» для форм ввода.</summary>
public sealed class FieldTable : TableLayoutPanel
{
    public FieldTable(int labelWidth = 130)
    {
        ColumnCount = 2;
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Top;
    }

    public T Add<T>(string label, T control, int height = 0) where T : Control
    {
        var row = RowCount++;
        RowStyles.Add(height > 0 ? new RowStyle(SizeType.Absolute, height) : new RowStyle(SizeType.AutoSize));
        var l = new Label
        {
            Text = label, AutoSize = false, Dock = DockStyle.Fill, ForeColor = Ui.Muted,
            TextAlign = height > 0 ? ContentAlignment.TopLeft : ContentAlignment.MiddleLeft,
            Padding = new Padding(0, height > 0 ? 6 : 0, 0, 0),
        };
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(3, 4, 3, 4);
        Controls.Add(l, 0, row);
        Controls.Add(control, 1, row);
        return control;
    }

    /// <summary>Комбобокс со свободным вводом и автодополнением из списка.</summary>
    public static ComboBox AutoCombo(IEnumerable<object> items)
    {
        var cb = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems,
        };
        cb.Items.AddRange(items.ToArray());
        return cb;
    }

    /// <summary>Нижняя панель с кнопками OK/Отмена.</summary>
    public static FlowLayoutPanel DialogButtons(Form form, string okText, EventHandler onOk)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 48,
            Padding = new Padding(12, 8, 12, 8),
        };
        var cancel = Ui.Button("Отмена", (_, _) => form.Close());
        cancel.DialogResult = DialogResult.Cancel;
        var ok = Ui.Button(okText, onOk, primary: true);
        panel.Controls.Add(cancel);
        panel.Controls.Add(ok);
        form.CancelButton = cancel;
        return panel;
    }
}
