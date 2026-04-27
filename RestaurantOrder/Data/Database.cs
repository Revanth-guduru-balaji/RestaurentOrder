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
                IsAvailable INTEGER NOT NULL DEFAULT 1
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

        SeedIfEmpty(conn);
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
