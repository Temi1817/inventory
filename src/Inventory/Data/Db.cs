using Dapper;
using Microsoft.Data.Sqlite;

namespace Inventory.Data;

/// <summary>Фабрика подключений к SQLite.</summary>
public static class Db
{
    private static string? _connectionString;

    public static void Init(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
        }.ToString();
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public static SqliteConnection Open()
    {
        var connection = new SqliteConnection(
            _connectionString ?? throw new InvalidOperationException("База данных не инициализирована."));
        connection.Open();
        // SQLite LOWER/LIKE не понимают кириллицу — свой регистронезависимый вариант.
        connection.CreateFunction("ulower", (string? s) => s?.ToLowerInvariant());
        return connection;
    }

    /// <summary>Шаблон для LIKE по ulower(...): "%текст%".</summary>
    public static string Like(string text) =>
        "%" + text.Trim().ToLowerInvariant()
            .Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    /// <summary>Пустая строка → NULL.</summary>
    public static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Закрыть все пулы (нужно перед восстановлением бэкапа).</summary>
    public static void ReleaseAll() => SqliteConnection.ClearAllPools();
}
