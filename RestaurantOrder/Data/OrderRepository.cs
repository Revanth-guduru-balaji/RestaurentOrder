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
            cmd.CommandText = @"INSERT INTO Orders (CreatedAt, Total, PaymentMethod, CustomerName, Notes)
                                VALUES ($t,$tot,$pm,$cn,$nt);
                                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$t", order.CreatedAt.ToString(Iso, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$tot", (double)order.Total);
            cmd.Parameters.AddWithValue("$pm", order.PaymentMethod ?? "Cash");
            cmd.Parameters.AddWithValue("$cn", (object?)order.CustomerName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$nt", (object?)order.Notes ?? DBNull.Value);
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
            var where = "CreatedAt >= $f AND CreatedAt < $t";
            if (!string.IsNullOrWhiteSpace(search))
                where += " AND (CAST(Id AS TEXT) LIKE $s OR IFNULL(CustomerName,'') LIKE $s)";
            cmd.CommandText = $"SELECT Id, CreatedAt, Total, PaymentMethod, CustomerName, Notes FROM Orders WHERE {where} ORDER BY CreatedAt DESC";
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
                    Total = (decimal)rdr.GetDouble(2),
                    PaymentMethod = rdr.GetString(3),
                    CustomerName = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    Notes = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                });
            }
        }
        return orders;
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
