using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace RestaurantOrder.Data;

public static class Database
{
    public static string DbPath { get; private set; } = string.Empty;
    public static string ConnectionString { get; private set; } = string.Empty;

    public static void Initialize()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(appData, "RestaurantOrder");
        Directory.CreateDirectory(dir);
        DbPath = Path.Combine(dir, "restaurant.db");
        ConnectionString = $"Data Source={DbPath}";

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

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
        var today = DateTime.Today.ToString("yyyy-MM-dd");
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
        return conn;
    }
}
