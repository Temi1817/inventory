using Inventory.Data;
using Inventory.Infrastructure;
using Inventory.UI;

namespace Inventory;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Одна копия программы: две копии одновременно распознавали бы одну очередь.
        using var mutex = new Mutex(true, @"Local\Inventory.SingleInstance", out var firstInstance);
        if (!firstInstance)
        {
            ActivateRunningInstance();
            return;
        }

        ApplicationConfiguration.Initialize();
        SetDefaultFormIcon();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Ui.Error(null, "Непредвиденная ошибка", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Необработанное исключение", e.ExceptionObject as Exception);

        try
        {
            AppPaths.EnsureCreated();
            Log.Info("Запуск программы");
            Db.Init(AppPaths.Database);
            Migrations.Run();
        }
        catch (Exception ex)
        {
            Ui.Error(null, "Не удалось открыть базу данных. Программа будет закрыта.", ex);
            return;
        }

        Application.Run(new MainForm());
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void ActivateRunningInstance()
    {
        var current = System.Diagnostics.Process.GetCurrentProcess();
        var other = System.Diagnostics.Process.GetProcessesByName(current.ProcessName)
            .FirstOrDefault(p => p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero);
        if (other == null) return;
        ShowWindow(other.MainWindowHandle, 9); // SW_RESTORE
        SetForegroundWindow(other.MainWindowHandle);
    }

    /// <summary>Иконка программы для всех окон (иначе у диалогов стандартная иконка WinForms).</summary>
    private static void SetDefaultFormIcon()
    {
        try
        {
            var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon == null) return;
            var field = typeof(Form).GetField("s_defaultIcon", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                        ?? typeof(Form).GetField("defaultIcon", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            field?.SetValue(null, icon);
        }
        catch (Exception ex) { Log.Error("Не удалось установить иконку окон", ex); }
    }
}
