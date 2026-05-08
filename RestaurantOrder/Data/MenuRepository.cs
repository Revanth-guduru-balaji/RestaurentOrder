using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace RestaurantOrder.Data;

public static class MenuRepository
{
    private const string SelectColumns = "Id, Name, Category, Price, IsAvailable, EstimatedAvailableQty, AvailableQty";

    public static List<MenuItem> GetAll(bool onlyAvailable = false)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = onlyAvailable
            ? $"SELECT {SelectColumns} FROM MenuItems WHERE IsAvailable = 1 ORDER BY Category, Name"
            : $"SELECT {SelectColumns} FROM MenuItems ORDER BY Category, Name";
        using var rdr = cmd.ExecuteReader();
        var list = new List<MenuItem>();
        while (rdr.Read())
        {
            list.Add(Read(rdr));
        }
        return list;
    }

    public static MenuItem? GetById(int id)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM MenuItems WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var rdr = cmd.ExecuteReader();
        return rdr.Read() ? Read(rdr) : null;
    }

    private static MenuItem Read(Microsoft.Data.Sqlite.SqliteDataReader rdr) => new MenuItem
    {
        Id = rdr.GetInt32(0),
        Name = rdr.GetString(1),
        Category = rdr.GetString(2),
        Price = (decimal)rdr.GetDouble(3),
        IsAvailable = rdr.GetInt32(4) == 1,
        EstimatedAvailableQty = rdr.GetInt32(5),
        AvailableQty = rdr.GetInt32(6),
    };

    public static int Insert(MenuItem item)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO MenuItems (Name, Category, Price, IsAvailable, EstimatedAvailableQty, AvailableQty, StockDate)
                            VALUES ($n,$c,$p,$a,$eq,$aq,$d);
                            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", item.Name);
        cmd.Parameters.AddWithValue("$c", item.Category ?? "");
        cmd.Parameters.AddWithValue("$p", (double)item.Price);
        cmd.Parameters.AddWithValue("$a", item.IsAvailable ? 1 : 0);
        cmd.Parameters.AddWithValue("$eq", item.EstimatedAvailableQty);
        cmd.Parameters.AddWithValue("$aq", item.EstimatedAvailableQty > 0 ? item.EstimatedAvailableQty : 0);
        cmd.Parameters.AddWithValue("$d", System.DateTime.Today.ToString("yyyy-MM-dd"));
        var id = (long)(cmd.ExecuteScalar() ?? 0L);
        item.Id = (int)id;
        item.AvailableQty = item.EstimatedAvailableQty > 0 ? item.EstimatedAvailableQty : 0;
        return item.Id;
    }

    public static void Update(MenuItem item)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE MenuItems
                            SET Name=$n, Category=$c, Price=$p, IsAvailable=$a,
                                EstimatedAvailableQty=$eq, AvailableQty=$aq
                            WHERE Id=$id";
        cmd.Parameters.AddWithValue("$n", item.Name);
        cmd.Parameters.AddWithValue("$c", item.Category ?? "");
        cmd.Parameters.AddWithValue("$p", (double)item.Price);
        cmd.Parameters.AddWithValue("$a", item.IsAvailable ? 1 : 0);
        cmd.Parameters.AddWithValue("$eq", item.EstimatedAvailableQty);
        cmd.Parameters.AddWithValue("$aq", item.AvailableQty);
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.ExecuteNonQuery();
    }

    /// Atomically deduct stock. Returns true if all decrements succeeded
    /// (or items were untracked), false if any item lacked stock.
    public static bool TryDeductStock(System.Collections.Generic.IEnumerable<(int MenuItemId, int Qty)> deductions)
    {
        using var conn = Database.Open();
        using var tx = conn.BeginTransaction();
        foreach (var (id, qty) in deductions)
        {
            using var sel = conn.CreateCommand();
            sel.CommandText = "SELECT EstimatedAvailableQty, AvailableQty FROM MenuItems WHERE Id=$id";
            sel.Parameters.AddWithValue("$id", id);
            using var rdr = sel.ExecuteReader();
            if (!rdr.Read()) continue;
            int est = rdr.GetInt32(0);
            int avail = rdr.GetInt32(1);
            rdr.Close();
            if (est <= 0) continue; // untracked
            if (avail < qty) { tx.Rollback(); return false; }
            using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE MenuItems SET AvailableQty = AvailableQty - $q WHERE Id=$id";
            upd.Parameters.AddWithValue("$q", qty);
            upd.Parameters.AddWithValue("$id", id);
            upd.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }

    public static void Delete(int id)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM MenuItems WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public static int BulkUpsertByName(IEnumerable<MenuItem> items)
    {
        using var conn = Database.Open();
        using var tx = conn.BeginTransaction();
        int affected = 0;
        foreach (var i in items)
        {
            using var sel = conn.CreateCommand();
            sel.CommandText = "SELECT Id FROM MenuItems WHERE Name = $n LIMIT 1";
            sel.Parameters.AddWithValue("$n", i.Name);
            var existingId = sel.ExecuteScalar();
            if (existingId != null && existingId != System.DBNull.Value)
            {
                i.Id = System.Convert.ToInt32(existingId);
                using var upd = conn.CreateCommand();
                upd.CommandText = @"UPDATE MenuItems SET Category=$c, Price=$p, IsAvailable=$a WHERE Id=$id";
                upd.Parameters.AddWithValue("$c", i.Category ?? "");
                upd.Parameters.AddWithValue("$p", (double)i.Price);
                upd.Parameters.AddWithValue("$a", i.IsAvailable ? 1 : 0);
                upd.Parameters.AddWithValue("$id", i.Id);
                upd.ExecuteNonQuery();
            }
            else
            {
                using var ins = conn.CreateCommand();
                ins.CommandText = @"INSERT INTO MenuItems (Name, Category, Price, IsAvailable) VALUES ($n,$c,$p,$a)";
                ins.Parameters.AddWithValue("$n", i.Name);
                ins.Parameters.AddWithValue("$c", i.Category ?? "");
                ins.Parameters.AddWithValue("$p", (double)i.Price);
                ins.Parameters.AddWithValue("$a", i.IsAvailable ? 1 : 0);
                ins.ExecuteNonQuery();
            }
            affected++;
        }
        tx.Commit();
        return affected;
    }
}
