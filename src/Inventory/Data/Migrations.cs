using Dapper;
using Inventory.Infrastructure;

namespace Inventory.Data;

/// <summary>
/// Миграции схемы. Каждая версия — отдельный скрипт; уже применённые не трогаются.
/// Новые изменения схемы — только добавлением нового скрипта в конец списка.
/// </summary>
public static class Migrations
{
    private static readonly string[] Scripts = [V1, V2, V3, V4];

    public static int LatestVersion => Scripts.Length;

    public static void Run()
    {
        using var c = Db.Open();
        c.Execute("CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL)");
        var current = c.ExecuteScalar<int?>("SELECT MAX(version) FROM schema_version") ?? 0;

        if (current > Scripts.Length)
            throw new InvalidOperationException(
                $"База данных создана более новой версией программы (схема v{current}). Обновите программу.");

        for (var v = current + 1; v <= Scripts.Length; v++)
        {
            using var tx = c.BeginTransaction();
            c.Execute(Scripts[v - 1], transaction: tx);
            c.Execute("INSERT INTO schema_version(version) VALUES (@v)", new { v }, tx);
            tx.Commit();
            Log.Info($"Применена миграция БД v{v}");
        }
    }

    private const string V1 = """
        CREATE TABLE companies (
            id   INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE
        );

        CREATE TABLE employees (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            full_name  TEXT NOT NULL,
            company_id INTEGER REFERENCES companies(id),
            department TEXT,
            position   TEXT,
            phone      TEXT,
            workplace  TEXT,
            notes      TEXT,
            is_active  INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL
        );

        CREATE TABLE suppliers (
            id    INTEGER PRIMARY KEY AUTOINCREMENT,
            name  TEXT NOT NULL,
            bin   TEXT,
            phone TEXT,
            notes TEXT
        );

        CREATE TABLE invoices (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            number      TEXT,
            date        TEXT,
            supplier_id INTEGER REFERENCES suppliers(id),
            company_id  INTEGER REFERENCES companies(id),
            total       REAL,
            file_path   TEXT,
            notes       TEXT,
            created_at  TEXT NOT NULL
        );

        CREATE TABLE items (
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            invoice_id       INTEGER REFERENCES invoices(id),
            name             TEXT NOT NULL,
            category         TEXT,
            serial_number    TEXT,
            inventory_number TEXT,
            price            REAL,
            warranty_until   TEXT,
            photo_path       TEXT,
            status           TEXT NOT NULL DEFAULT 'На складе'
                             CHECK (status IN ('На складе','Выдан','В ремонте','Списан')),
            employee_id      INTEGER REFERENCES employees(id),
            company_id       INTEGER REFERENCES companies(id),
            notes            TEXT,
            created_at       TEXT NOT NULL
        );
        CREATE UNIQUE INDEX ux_items_inventory_number ON items(inventory_number)
            WHERE inventory_number IS NOT NULL AND inventory_number <> '';
        CREATE INDEX ix_items_employee ON items(employee_id);
        CREATE INDEX ix_items_invoice  ON items(invoice_id);
        CREATE INDEX ix_items_status   ON items(status);

        CREATE TABLE consumables (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            name         TEXT NOT NULL,
            category     TEXT,
            quantity     REAL NOT NULL DEFAULT 0,
            min_quantity REAL NOT NULL DEFAULT 0,
            unit         TEXT,
            notes        TEXT
        );

        CREATE TABLE history (
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            item_id          INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
            date             TEXT NOT NULL,
            action           TEXT NOT NULL
                             CHECK (action IN ('Приход','Выдача','Возврат','Передача','Ремонт','Списание','Изменение')),
            from_employee_id INTEGER REFERENCES employees(id),
            to_employee_id   INTEGER REFERENCES employees(id),
            comment          TEXT
        );
        CREATE INDEX ix_history_item ON history(item_id);
        """;

    /// <summary>v2: журнал движений расходников (приход по накладной, выдача).</summary>
    private const string V2 = """
        CREATE TABLE consumable_moves (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            consumable_id INTEGER NOT NULL REFERENCES consumables(id) ON DELETE CASCADE,
            date          TEXT NOT NULL,
            quantity      REAL NOT NULL,   -- приход > 0, расход < 0
            employee_id   INTEGER REFERENCES employees(id),
            invoice_id    INTEGER REFERENCES invoices(id),
            comment       TEXT
        );
        CREATE INDEX ix_consumable_moves_consumable ON consumable_moves(consumable_id);
        CREATE INDEX ix_invoices_number ON invoices(number);
        """;

    /// <summary>v3: очередь пакетной загрузки «На проверке» (переживает закрытие программы).</summary>
    private const string V3 = """
        CREATE TABLE import_queue (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            file_path     TEXT NOT NULL,      -- относительный путь копии в queue\
            original_name TEXT NOT NULL,
            file_hash     TEXT NOT NULL,      -- SHA-256: не добавлять один файл дважды
            status        TEXT NOT NULL,      -- Ожидает | Распознаётся | Распознано | Ошибка | Принято | Пропущено
            engine        TEXT,
            result_json   TEXT,
            error         TEXT,
            number        TEXT,
            date          TEXT,
            supplier      TEXT,
            item_count    INTEGER,
            invoice_id    INTEGER REFERENCES invoices(id),
            created_at    TEXT NOT NULL,
            updated_at    TEXT NOT NULL
        );
        CREATE INDEX ix_import_queue_status ON import_queue(status);
        CREATE INDEX ix_import_queue_hash ON import_queue(file_hash);
        """;

    /// <summary>v4: заказы по счетам на оплату — что купить и для кого; закрываются накладной.</summary>
    private const string V4 = """
        CREATE TABLE orders (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            number      TEXT,
            date        TEXT,
            supplier_id INTEGER REFERENCES suppliers(id),
            company_id  INTEGER REFERENCES companies(id),
            total       REAL,
            file_path   TEXT,
            notes       TEXT,
            status      TEXT NOT NULL DEFAULT 'Ждёт оплаты'
                        CHECK (status IN ('Ждёт оплаты','Оплачен','Получен','Отменён')),
            paid_at     TEXT,
            invoice_id  INTEGER REFERENCES invoices(id),
            created_at  TEXT NOT NULL
        );
        CREATE INDEX ix_orders_status ON orders(status);

        CREATE TABLE order_lines (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            order_id       INTEGER NOT NULL REFERENCES orders(id) ON DELETE CASCADE,
            line_no        INTEGER NOT NULL,
            name           TEXT NOT NULL,
            category       TEXT,
            quantity       REAL NOT NULL,
            unit           TEXT,
            price          REAL,
            employee_id    INTEGER REFERENCES employees(id),   -- для кого покупаем
            to_consumables INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_order_lines_order ON order_lines(order_id);
        CREATE INDEX ix_order_lines_employee ON order_lines(employee_id);
        """;
}
