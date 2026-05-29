using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace RestaurantOrder.Data;

public static class OrderRepository
{
    private const string Iso = "yyyy-MM-dd HH:mm:ss";

    public static int Create(Order order)
    {
        using var conn = Database.Open();
        using var tx = conn.BeginTransaction();
        InsertOrderRows(conn, order);
        tx.Commit();
        return order.Id;
    }

    /// Result of an atomic place-order: success, or which tracked item ran short.
    public readonly record struct PlaceOutcome(bool Ok, string? ShortItemName);

    /// Atomically reserve stock for tracked items AND record the order in ONE
    /// transaction. Either stock is deducted and the order is saved, or nothing
    /// changes — there is no window where inventory drops without a recorded sale.
    public static PlaceOutcome CreateWithStock(Order order, IEnumerable<(int MenuItemId, int Qty)> deductions)
    {
        using var conn = Database.Open();
        using var tx = conn.BeginTransaction();

        foreach (var (id, qty) in deductions)
        {
            // Conditional atomic decrement — only a tracked row (Est>0) with enough
            // stock is affected, so the check and the write can't race.
            using var upd = conn.CreateCommand();
            upd.CommandText = @"UPDATE MenuItems
                                SET AvailableQty = AvailableQty - $q
                                WHERE Id = $id AND EstimatedAvailableQty > 0 AND AvailableQty >= $q";
            upd.Parameters.AddWithValue("$q", qty);
            upd.Parameters.AddWithValue("$id", id);
            if (upd.ExecuteNonQuery() > 0) continue;

            // rows == 0: distinguish untracked/deleted (allowed) from short (reject).
            using var chk = conn.CreateCommand();
            chk.CommandText = "SELECT EstimatedAvailableQty, Name FROM MenuItems WHERE Id = $id";
            chk.Parameters.AddWithValue("$id", id);
            using var rdr = chk.ExecuteReader();
            if (!rdr.Read()) continue;              // deleted item — name/price live on the order row
            int est = rdr.GetInt32(0);
            string name = rdr.GetString(1);
            rdr.Close();
            if (est <= 0) continue;                 // untracked — unlimited
            tx.Rollback();
            return new PlaceOutcome(false, name);   // tracked but insufficient
        }

        InsertOrderRows(conn, order);
        tx.Commit();
        return new PlaceOutcome(true, null);
    }

    // Inserts the Orders header + OrderItems rows on an already-open connection,
    // enlisting in whatever transaction is active. Sets order.Id.
    private static void InsertOrderRows(SqliteConnection conn, Order order)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO Orders
                                (CreatedAt, Subtotal, TaxAmount, DiscountAmount, RoundingAmount,
                                 Total, PaymentMethod, CustomerName, Notes, IsVoided, IsParcel)
                                VALUES ($t,$sub,$tax,$disc,$rnd,$tot,$pm,$cn,$nt,0,$pa);
                                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$t", order.CreatedAt.ToString(Iso, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$sub", (double)order.Subtotal);
            cmd.Parameters.AddWithValue("$tax", (double)order.TaxAmount);
            cmd.Parameters.AddWithValue("$disc", (double)order.DiscountAmount);
            cmd.Parameters.AddWithValue("$rnd", (double)order.RoundingAmount);
            cmd.Parameters.AddWithValue("$tot", (double)order.Total);
            cmd.Parameters.AddWithValue("$pm", order.PaymentMethod ?? "Cash");
            cmd.Parameters.AddWithValue("$cn", (object?)order.CustomerName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$nt", (object?)order.Notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pa", order.IsParcel ? 1 : 0);
            order.Id = Convert.ToInt32((long)(cmd.ExecuteScalar() ?? 0L));
        }

        foreach (var it in order.Items)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO OrderItems (OrderId, MenuItemId, MenuItemName, UnitPrice, Quantity)
                                VALUES ($o,$mi,$mn,$up,$q)";
            cmd.Parameters.AddWithValue("$o", order.Id);
            cmd.Parameters.AddWithValue("$mi", it.MenuItemId);
            cmd.Parameters.AddWithValue("$mn", it.MenuItemName);
            cmd.Parameters.AddWithValue("$up", (double)it.UnitPrice);
            cmd.Parameters.AddWithValue("$q", it.Quantity);
            cmd.ExecuteNonQuery();
        }
    }

    public static List<Order> GetBetween(DateTime from, DateTime to, string? search = null)
    {
        using var conn = Database.Open();
        var orders = new List<Order>();
        using (var cmd = conn.CreateCommand())
        {
            var where = "o.CreatedAt >= $f AND o.CreatedAt < $t";
            if (!string.IsNullOrWhiteSpace(search))
                where += @" AND (CAST(o.Id AS TEXT) LIKE $s ESCAPE '\' OR IFNULL(o.CustomerName,'') LIKE $s ESCAPE '\')";
            cmd.CommandText = $@"SELECT o.Id, o.CreatedAt,
                                        o.Subtotal, o.TaxAmount, o.DiscountAmount, o.RoundingAmount,
                                        o.Total, o.PaymentMethod, o.CustomerName, o.Notes, o.IsVoided,
                                        IFNULL(s.LineCount,0) AS LineCount,
                                        IFNULL(s.PieceCount,0) AS PieceCount,
                                        o.IsParcel
                                 FROM Orders o
                                 LEFT JOIN (
                                     -- Aggregate ONLY the items for orders in the date
                                     -- window, so this never scans the whole OrderItems
                                     -- table as history grows.
                                     SELECT oi.OrderId, COUNT(*) AS LineCount, SUM(oi.Quantity) AS PieceCount
                                     FROM OrderItems oi
                                     INNER JOIN Orders o2 ON o2.Id = oi.OrderId
                                     WHERE o2.CreatedAt >= $f AND o2.CreatedAt < $t
                                     GROUP BY oi.OrderId
                                 ) s ON s.OrderId = o.Id
                                 WHERE {where}
                                 ORDER BY o.CreatedAt DESC";
            cmd.Parameters.AddWithValue("$f", from.ToString(Iso, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$t", to.ToString(Iso, CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(search))
            {
                // Escape LIKE metacharacters so a literal % or _ in the search box
                // doesn't act as a wildcard (paired with ESCAPE '\' in the clause).
                var escaped = search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
                cmd.Parameters.AddWithValue("$s", "%" + escaped + "%");
            }
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                // Skip a row with a malformed timestamp rather than throwing and
                // taking down the entire history view.
                if (!DateTime.TryParseExact(rdr.GetString(1), Iso, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var createdAt))
                    continue;
                orders.Add(new Order
                {
                    Id = rdr.GetInt32(0),
                    CreatedAt = createdAt,
                    Subtotal = Database.ReadMoney(rdr.GetDouble(2)),
                    TaxAmount = Database.ReadMoney(rdr.GetDouble(3)),
                    DiscountAmount = Database.ReadMoney(rdr.GetDouble(4)),
                    RoundingAmount = Database.ReadMoney(rdr.GetDouble(5)),
                    Total = Database.ReadMoney(rdr.GetDouble(6)),
                    PaymentMethod = rdr.GetString(7),
                    CustomerName = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                    Notes = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                    IsVoided = rdr.GetInt32(10) == 1,
                    LineCount = rdr.GetInt32(11),
                    PieceCount = rdr.GetInt32(12),
                    IsParcel = rdr.GetInt32(13) == 1,
                });
            }
        }
        return orders;
    }

    public static void SetVoided(int orderId, bool voided)
    {
        using var conn = Database.Open();
        using var tx = conn.BeginTransaction();

        // Read current state so we don't double-apply stock adjustments
        // (idempotent: voiding a voided order does nothing).
        bool wasVoided;
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT IsVoided FROM Orders WHERE Id=$id";
            read.Parameters.AddWithValue("$id", orderId);
            var v = read.ExecuteScalar();
            if (v == null || v == DBNull.Value) { tx.Rollback(); return; }
            wasVoided = Convert.ToInt32(v) == 1;
        }
        if (wasVoided == voided) { tx.Rollback(); return; }

        // Voiding (false→true): add the order's quantities back to stock.
        // Restoring (true→false): re-deduct.
        int sign = voided ? +1 : -1;
        using (var items = conn.CreateCommand())
        {
            items.CommandText = "SELECT MenuItemId, Quantity FROM OrderItems WHERE OrderId=$id";
            items.Parameters.AddWithValue("$id", orderId);
            using var rdr = items.ExecuteReader();
            var deltas = new List<(int id, int qty)>();
            while (rdr.Read()) deltas.Add((rdr.GetInt32(0), rdr.GetInt32(1)));
            rdr.Close();

            foreach (var (id, qty) in deltas)
            {
                // Only adjust tracked items (EstimatedAvailableQty > 0).
                using var upd = conn.CreateCommand();
                // Clamp BOTH ends: never below 0, never above the daily estimate.
                // The upper clamp stops a cross-day void (after the overnight reset
                // refilled AvailableQty) from inflating stock above what exists.
                upd.CommandText = @"UPDATE MenuItems
                                    SET AvailableQty = MIN(EstimatedAvailableQty, MAX(0, AvailableQty + ($s) * $q))
                                    WHERE Id = $mi AND EstimatedAvailableQty > 0";
                upd.Parameters.AddWithValue("$s", sign);
                upd.Parameters.AddWithValue("$q", qty);
                upd.Parameters.AddWithValue("$mi", id);
                upd.ExecuteNonQuery();
            }
        }

        using (var setFlag = conn.CreateCommand())
        {
            setFlag.CommandText = "UPDATE Orders SET IsVoided=$v WHERE Id=$id";
            setFlag.Parameters.AddWithValue("$v", voided ? 1 : 0);
            setFlag.Parameters.AddWithValue("$id", orderId);
            setFlag.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public static void UpdatePaymentMethod(int orderId, string method)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Orders SET PaymentMethod=$pm WHERE Id=$id";
        cmd.Parameters.AddWithValue("$pm", method ?? "Cash");
        cmd.Parameters.AddWithValue("$id", orderId);
        cmd.ExecuteNonQuery();
    }

    public static void UpdateIsParcel(int orderId, bool isParcel)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Orders SET IsParcel=$p WHERE Id=$id";
        cmd.Parameters.AddWithValue("$p", isParcel ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", orderId);
        cmd.ExecuteNonQuery();
    }

    public static void LogReprint(int orderId)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO ReprintLog (OrderId, CreatedAt) VALUES ($id, $t)";
        cmd.Parameters.AddWithValue("$id", orderId);
        cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString(Iso, CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public static int CountReprintsBetween(DateTime from, DateTime to)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ReprintLog WHERE CreatedAt >= $f AND CreatedAt < $t";
        cmd.Parameters.AddWithValue("$f", from.ToString(Iso, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$t", to.ToString(Iso, CultureInfo.InvariantCulture));
        return Convert.ToInt32((long)(cmd.ExecuteScalar() ?? 0L));
    }

    public static List<OrderItem> GetItems(int orderId)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, OrderId, MenuItemId, MenuItemName, UnitPrice, Quantity FROM OrderItems WHERE OrderId=$o ORDER BY Id";
        cmd.Parameters.AddWithValue("$o", orderId);
        using var rdr = cmd.ExecuteReader();
        var list = new List<OrderItem>();
        while (rdr.Read())
        {
            list.Add(new OrderItem
            {
                Id = rdr.GetInt32(0),
                OrderId = rdr.GetInt32(1),
                MenuItemId = rdr.GetInt32(2),
                MenuItemName = rdr.GetString(3),
                UnitPrice = Database.ReadMoney(rdr.GetDouble(4)),
                Quantity = rdr.GetInt32(5),
            });
        }
        return list;
    }
}
