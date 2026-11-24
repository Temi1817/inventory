namespace Inventory.Infrastructure;

/// <summary>Все пути к данным программы: %AppData%\Inventory\...</summary>
public static class AppPaths
{
    public static string Root { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Inventory");

    public static string Database => Path.Combine(Root, "inventory.db");
    public static string Files => Path.Combine(Root, "files");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Queue => Path.Combine(Root, "queue");
    public static string Settings => Path.Combine(Root, "settings.json");

    /// <summary>Подменить корень (для тестов).</summary>
    internal static void UseRoot(string root) => Root = root;

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Files);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Queue);
    }

    /// <summary>Относительный путь из БД → абсолютный путь на диске.</summary>
    public static string ToAbsolute(string relativePath) => Path.Combine(Root, relativePath);
}
