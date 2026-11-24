namespace Inventory.UI;

/// <summary>Раздел главного окна. RefreshData вызывается при каждом показе.</summary>
public interface IView
{
    void RefreshData();
}

/// <summary>Заглушка для разделов следующих этапов.</summary>
public sealed class PlaceholderView : UserControl, IView
{
    public PlaceholderView(string title, string text)
    {
        Dock = DockStyle.Fill;
        Controls.Add(new Label
        {
            Text = text, Dock = DockStyle.Fill, ForeColor = Ui.Muted,
            TextAlign = ContentAlignment.MiddleCenter,
        });
        Controls.Add(Ui.Title(title));
    }

    public void RefreshData() { }
}
