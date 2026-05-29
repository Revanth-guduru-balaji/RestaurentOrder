using System;
using System.Collections.Generic;
using System.Globalization;

namespace RestaurantOrder.Data;

public class DailyTotal
{
    public DateTime Day { get; set; }
    public decimal Revenue { get; set; }
    public int OrderCount { get; set; }
}

public class TopItem
{
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal Revenue { get; set; }
}

public class CategoryShare
{
    public string Category { get; set; } = string.Empty;
    public decimal Revenue { get; set; }
    public int Quantity { get; set; }
}

public class HourlyBucket
{
    public int Hour { get; set; }
    public int OrderCount { get; set; }
    public decimal Revenue { get; set; }
}

public static class AnalyticsRepository
{
    private const string Iso = "yyyy-MM-dd HH:mm:ss";

    // Voided orders are excluded from every analytics query — they show in
    // Order History tagged VOIDED but never count toward revenue, order count,
    // avg ticket, top items, hourly buckets, or category breakdown.
    private const string VoidFilter = "IsVoided = 0";

    public static (decimal revenue, int orderCount, decimal avgTicket) Summary(DateTime from, DateTime to)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"SELECT IFNULL(SUM(Total),0), COUNT(*) FROM Orders
                             WHERE CreatedAt >= $f AND CreatedAt < $t AND {VoidFilter}";
        cmd.Parameters.AddWithValue("$f", from.ToString(Iso, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$t", to.ToString(Iso, CultureInfo.InvariantCulture));
        using var rdr = cmd.ExecuteReader();
        rdr.Read();
        var rev = Database.ReadMoney(rdr.GetDouble(0));
        var cnt = rdr.GetInt32(1);
        return (rev, cnt, cnt == 0 ? 0 : rev / cnt);
    }

    public static List<DailyTotal> DailyRevenue(DateTime from, DateTime to)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"SELECT substr(CreatedAt,1,10) AS d, SUM(Total), COUNT(*)
                             FROM Orders WHERE CreatedAt >= $f AND CreatedAt < $t AND {VoidFilter}
                             GROUP BY d ORDER BY d";
        cmd.Parameters.AddWithValue("$f", from.ToString(Iso, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$t", to.ToString(Iso, CultureInfo.InvariantCulture));
        using var rdr = cmd.ExecuteReader();
        var dict = new Dictionary<DateTime, DailyTotal>();
        while (rdr.Read())
        {
            var d = DateTime.ParseExact(rdr.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            dict[d] = new DailyTotal { Day = d, Revenue = Database.ReadMoney(rdr.GetDouble(1)), OrderCount = rdr.GetInt32(2) };
        }
        var list = new List<DailyTotal>();
        for (var d = from.Date; d < to.Date; d = d.AddDays(1))
            list.Add(dict.TryGetValue(d, out var v) ? v : new DailyTotal { Day = d });
        return list;
    }

    public static List<TopItem> TopItems(DateTime from, DateTime to, int limit = 5)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        // Revenue is each line's gross scaled by the order's net/gross ratio
        // (Total/Subtotal), so per-item revenue reconciles with headline revenue
        // even when orders carry tax/discount/rounding. Legacy rows with no stored
        // Subtotal fall back to gross (ratio 1).
        cmd.CommandText = $@"SELECT oi.MenuItemName, SUM(oi.Quantity),
                                    SUM((oi.UnitPrice * oi.Quantity) *
                                        (CASE WHEN o.Subtotal > 0 THEN o.Total / o.Subtotal ELSE 1 END))
                             FROM OrderItems oi
                             INNER JOIN Orders o ON o.Id = oi.OrderId
                             WHERE o.CreatedAt >= $f AND o.CreatedAt < $t AND o.{VoidFilter}
                             GROUP BY oi.MenuItemName
                             ORDER BY SUM(oi.Quantity) DESC
                             LIMIT $l";
        cmd.Parameters.AddWithValue("$f", from.ToString(Iso, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$t", to.ToString(Iso, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$l", limit);
        using var rdr = cmd.ExecuteReader();
        var list = new List<TopItem>();
        while (rdr.Read())
            list.Add(new TopItem { Name = rdr.GetString(0), Quantity = rdr.GetInt32(1), Revenue = Database.ReadMoney(rdr.GetDouble(2)) });
        return list;
    }

    public static List<CategoryShare> CategoryBreakdown(DateTime from, DateTime to)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        // Net-allocated revenue (see TopItems) so category totals reconcile with
        // headline revenue rather than summing gross line amounts.
        cmd.CommandText = $@"SELECT IFNULL(NULLIF(mi.Category,''),'(uncategorized)'),
                                    SUM((oi.UnitPrice * oi.Quantity) *
                                        (CASE WHEN o.Subtotal > 0 THEN o.Total / o.Subtotal ELSE 1 END)),
                                    SUM(oi.Quantity)
                             FROM OrderItems oi
                             INNER JOIN Orders o ON o.Id = oi.OrderId
                             LEFT JOIN MenuItems mi ON mi.Id = oi.MenuItemId
                             WHERE o.CreatedAt >= $f AND o.CreatedAt < $t AND o.{VoidFilter}
                             GROUP BY 1
                             ORDER BY 2 DESC";
        cmd.Parameters.AddWithValue("$f", from.ToString(Iso, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$t", to.ToString(Iso, CultureInfo.InvariantCulture));
        using var rdr = cmd.ExecuteReader();
        var list = new List<CategoryShare>();
        while (rdr.Read())
            list.Add(new CategoryShare { Category = rdr.GetString(0), Revenue = Database.ReadMoney(rdr.GetDouble(1)), Quantity = rdr.GetInt32(2) });
        return list;
    }

    /// Revenue per calendar month (index 0 = Jan) for a year, in ONE query —
    /// replaces 12 separate Summary round trips.
    public static decimal[] MonthlyRevenue(int year)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"SELECT CAST(substr(CreatedAt, 6, 2) AS INTEGER) AS m, SUM(Total)
                             FROM Orders
                             WHERE CreatedAt >= $f AND CreatedAt < $t AND {VoidFilter}
                             GROUP BY m";
        cmd.Parameters.AddWithValue("$f", new DateTime(year, 1, 1).ToString(Iso, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$t", new DateTime(year + 1, 1, 1).ToString(Iso, CultureInfo.InvariantCulture));
        var result = new decimal[12];
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            int m = rdr.GetInt32(0); // 1..12
            if (m >= 1 && m <= 12) result[m - 1] = Database.ReadMoney(rdr.GetDouble(1));
        }
        return result;
    }

    public static List<HourlyBucket> HourlyBuckets(DateTime from, DateTime to)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"SELECT CAST(substr(CreatedAt, 12, 2) AS INTEGER) AS h,
                                    COUNT(*), SUM(Total)
                             FROM Orders WHERE CreatedAt >= $f AND CreatedAt < $t AND {VoidFilter}
                             GROUP BY h ORDER BY h";
        cmd.Parameters.AddWithValue("$f", from.ToString(Iso, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$t", to.ToString(Iso, CultureInfo.InvariantCulture));
        using var rdr = cmd.ExecuteReader();
        var dict = new Dictionary<int, HourlyBucket>();
        while (rdr.Read())
            dict[rdr.GetInt32(0)] = new HourlyBucket { Hour = rdr.GetInt32(0), OrderCount = rdr.GetInt32(1), Revenue = Database.ReadMoney(rdr.GetDouble(2)) };
        var list = new List<HourlyBucket>();
        for (int h = 0; h < 24; h++)
            list.Add(dict.TryGetValue(h, out var v) ? v : new HourlyBucket { Hour = h });
        return list;
    }
}
