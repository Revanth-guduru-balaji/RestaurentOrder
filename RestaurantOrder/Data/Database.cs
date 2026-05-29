using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace RestaurantOrder.Data;

public static class Database
{
    public static string DbPath { get; private set; } = string.Empty;
    public static string ConnectionString { get; private set; } = string.Empty;

    /// Invariant format for date-only columns (StockDate). Kept Gregorian so it
    /// never diverges from the other date columns under a non-Gregorian OS calendar.
    public const string DateFmt = "yyyy-MM-dd";

    /// A non-fatal notice set during Initialize (e.g. the DB was corrupt and was
    /// reset). The app reads this after startup and surfaces it to the operator.
    public static string? StartupWarning { get; private set; }

    /// Money is stored as REAL (double). Round on read to 2 decimals so the
    /// decimal precision established by PriceBreakdown survives the round trip and
    /// SQL SUM() over REAL can't surface sub-paise IEEE-754 drift (e.g. 90.00000000000001).
    public static decimal ReadMoney(double value) => Math.Round((decimal)value, 2, MidpointRounding.AwayFromZero);

    public static void Initialize(string? dirOverride = null)
    {
        var dir = dirOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RestaurantOrder");
        Directory.CreateDirectory(dir);
        DbPath = Path.Combine(dir, "restaurant.db");
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString();

        try
        {
            InitializeSchema();
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            // A corrupt / non-database file would otherwise crash on every launch.
            // Move it aside (nothing is silently destroyed) and start clean.
            QuarantineDatabase(ex);
            InitializeSchema();
        }
    }

    private static bool IsCorruption(SqliteException ex)
        => ex.SqliteErrorCode == 11 /* SQLITE_CORRUPT */
        || ex.SqliteErrorCode == 26 /* SQLITE_NOTADB */;

    private static void QuarantineDatabase(Exception ex)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = DbPath + suffix;
            if (!File.Exists(path)) continue;
            try { File.Move(path, $"{DbPath}.corrupt-{stamp}{suffix}", overwrite: true); }
            catch { try { File.Delete(path); } catch { } }
        }
        StartupWarning = "The database file was unreadable and has been reset to an empty menu. "
                       + $"A backup of the old file was saved next to it. ({ex.GetType().Name})";
    }

    private static void InitializeSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS MenuItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Category TEXT NOT NULL DEFAULT '',
                Price REAL NOT NULL,
                IsAvailable INTEGER NOT NULL DEFAULT 1,
                EstimatedAvailableQty INTEGER NOT NULL DEFAULT 0,
                AvailableQty INTEGER NOT NULL DEFAULT 0,
                StockDate TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS Orders (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CreatedAt TEXT NOT NULL,
                Total REAL NOT NULL,
                PaymentMethod TEXT NOT NULL DEFAULT 'Cash',
                CustomerName TEXT,
                Notes TEXT
            );
            CREATE TABLE IF NOT EXISTS OrderItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                OrderId INTEGER NOT NULL,
                MenuItemId INTEGER NOT NULL,
                MenuItemName TEXT NOT NULL,
                UnitPrice REAL NOT NULL,
                Quantity INTEGER NOT NULL,
                FOREIGN KEY(OrderId) REFERENCES Orders(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_Orders_CreatedAt ON Orders(CreatedAt);
            CREATE INDEX IF NOT EXISTS IX_OrderItems_OrderId ON OrderItems(OrderId);
        ";
        cmd.ExecuteNonQuery();

        AddColumnIfMissing(conn, "MenuItems", "EstimatedAvailableQty", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "MenuItems", "AvailableQty", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "MenuItems", "StockDate", "TEXT NOT NULL DEFAULT ''");

        AddColumnIfMissing(conn, "Orders", "Subtotal", "REAL NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "Orders", "TaxAmount", "REAL NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "Orders", "DiscountAmount", "REAL NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "Orders", "RoundingAmount", "REAL NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "Orders", "IsVoided", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "Orders", "IsParcel", "INTEGER NOT NULL DEFAULT 0");

        // ReprintLog tracks every reprint so the Order History KPI is accurate.
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = @"
            CREATE TABLE IF NOT EXISTS ReprintLog (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                OrderId INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                FOREIGN KEY(OrderId) REFERENCES Orders(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_ReprintLog_OrderId ON ReprintLog(OrderId);
            CREATE INDEX IF NOT EXISTS IX_ReprintLog_CreatedAt ON ReprintLog(CreatedAt);

            CREATE TABLE IF NOT EXISTS Drafts (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                CustomerName TEXT,
                PaymentMethod TEXT NOT NULL DEFAULT 'Cash',
                DiscountAmount REAL NOT NULL DEFAULT 0,
                IsParcel INTEGER NOT NULL DEFAULT 0,
                Notes TEXT
            );
            CREATE TABLE IF NOT EXISTS DraftItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DraftId INTEGER NOT NULL,
                MenuItemId INTEGER NOT NULL,
                MenuItemName TEXT NOT NULL,
                UnitPrice REAL NOT NULL,
                Quantity INTEGER NOT NULL,
                FOREIGN KEY(DraftId) REFERENCES Drafts(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_DraftItems_DraftId ON DraftItems(DraftId);
        ";
        cmd2.ExecuteNonQuery();

        SeedIfEmpty(conn);
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string definition)
    {
        using var pragma = conn.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({table})";
        using var rdr = pragma.ExecuteReader();
        while (rdr.Read())
        {
            if (string.Equals(rdr.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        rdr.Close();
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    /// Resets AvailableQty to EstimatedAvailableQty for items whose StockDate isn't today.
    /// Items with EstimatedAvailableQty == 0 are treated as untracked (skipped).
    public static int ResetDailyStockIfNeeded()
    {
        var today = DateTime.Today.ToString(DateFmt, CultureInfo.InvariantCulture);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE MenuItems
                            SET AvailableQty = EstimatedAvailableQty,
                                StockDate = $d
                            WHERE EstimatedAvailableQty > 0 AND StockDate <> $d";
        cmd.Parameters.AddWithValue("$d", today);
        return cmd.ExecuteNonQuery();
    }

    private static void SeedIfEmpty(SqliteConnection conn)
    {
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM MenuItems";
        var count = (long)(check.ExecuteScalar() ?? 0L);
        if (count > 0) return;

        var samples = new (string Name, string Category, decimal Price)[]
        {
            ("Idly (1 pc)", "Breakfast", 10m),
            ("Idly Plate (3 pcs)", "Breakfast", 25m),
            ("Plain Dosa", "Breakfast", 40m),
            ("Masala Dosa", "Breakfast", 60m),
            ("Vada (2 pcs)", "Breakfast", 30m),
            ("Pongal", "Breakfast", 50m),
            ("Filter Coffee", "Beverages", 20m),
            ("Tea", "Beverages", 15m),
            ("Veg Meals", "Lunch", 120m),
            ("Curd Rice", "Lunch", 80m),
        };
        using var tx = conn.BeginTransaction();
        using var ins = conn.CreateCommand();
        ins.CommandText = "INSERT INTO MenuItems (Name, Category, Price, IsAvailable) VALUES ($n,$c,$p,1)";
        var pn = ins.CreateParameter(); pn.ParameterName = "$n"; ins.Parameters.Add(pn);
        var pc = ins.CreateParameter(); pc.ParameterName = "$c"; ins.Parameters.Add(pc);
        var pp = ins.CreateParameter(); pp.ParameterName = "$p"; ins.Parameters.Add(pp);
        foreach (var s in samples)
        {
            pn.Value = s.Name;
            pc.Value = s.Category;
            pp.Value = (double)s.Price;
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public static SqliteConnection Open()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        // busy_timeout : wait briefly instead of throwing "database is locked" when
        //                the app contends with itself (draft autosave vs place-order).
        // foreign_keys : enforce the declared ON DELETE CASCADE relationships.
        // journal_mode=WAL : better read/write concurrency and crash durability.
        pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }
}
