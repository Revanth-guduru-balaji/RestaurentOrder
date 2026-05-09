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

        tx.Commit();
        return order.Id;
    }

    public static List<Order> GetBetween(DateTime from, DateTime to, string? search = null)
    {
        using var conn = Database.Open();
        var orders = new List<Order>();
        using (var cmd = conn.CreateCommand())
        {
            var where = "o.CreatedAt >= $f AND o.CreatedAt < $t";
            if (!string.IsNullOrWhiteSpace(search))
                where += " AND (CAST(o.Id AS TEXT) LIKE $s OR IFNULL(o.CustomerName,'') LIKE $s)";
            cmd.CommandText = $@"SELECT o.Id, o.CreatedAt,
                                        o.Subtotal, o.TaxAmount, o.DiscountAmount, o.RoundingAmount,
                                        o.Total, o.PaymentMethod, o.CustomerName, o.Notes, o.IsVoided,
                                        IFNULL(s.LineCount,0) AS LineCount,
                                        IFNULL(s.PieceCount,0) AS PieceCount,
                                        o.IsParcel
                                 FROM Orders o
                                 LEFT JOIN (
                                     SELECT OrderId, COUNT(*) AS LineCount, SUM(Quantity) AS PieceCount
                                     FROM OrderItems GROUP BY OrderId
                                 ) s ON s.OrderId = o.Id
                                 WHERE {where}
                                 ORDER BY o.CreatedAt DESC";
            cmd.Parameters.AddWithValue("$f", from.ToString(Iso, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$t", to.ToString(Iso, CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(search))
                cmd.Parameters.AddWithValue("$s", "%" + search + "%");
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                orders.Add(new Order
                {
                    Id = rdr.GetInt32(0),
                    CreatedAt = DateTime.ParseExact(rdr.GetString(1), Iso, CultureInfo.InvariantCulture),
                    Subtotal = (decimal)rdr.GetDouble(2),
                    TaxAmount = (decimal)rdr.GetDouble(3),
                    DiscountAmount = (decimal)rdr.GetDouble(4),
                    RoundingAmount = (decimal)rdr.GetDouble(5),
                    Total = (decimal)rdr.GetDouble(6),
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
                upd.CommandText = @"UPDATE MenuItems
                                    SET AvailableQty = MAX(0, AvailableQty + ($s) * $q)
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
                UnitPrice = (decimal)rdr.GetDouble(4),
                Quantity = rdr.GetInt32(5),
            });
        }
        return list;
    }
}
