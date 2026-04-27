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

    public static (decimal revenue, int orderCount, decimal avgTicket) Summary(DateTime from, DateTime to)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT IFNULL(SUM(Total),0), COUNT(*) FROM Orders WHERE CreatedAt >= $f AND CreatedAt < $t";
        cmd.Parameters.AddWithValue("$f", from.ToString(Iso, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$t", to.ToString(Iso, CultureInfo.InvariantCulture));
        using var rdr = cmd.ExecuteReader();
        rdr.Read();
        var rev = (decimal)rdr.GetDouble(0);
        var cnt = rdr.GetInt32(1);
        return (rev, cnt, cnt == 0 ? 0 : rev / cnt);
    }

    public static List<DailyTotal> DailyRevenue(DateTime from, DateTime to)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT substr(CreatedAt,1,10) AS d, SUM(Total), COUNT(*)
                            FROM Orders WHERE CreatedAt >= $f AND CreatedAt < $t
                            GROUP BY d ORDER BY d";
        cmd.Parameters.AddWithValue("$f", from.ToString(Iso, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$t", to.ToString(Iso, CultureInfo.InvariantCulture));
        using var rdr = cmd.ExecuteReader();
        var dict = new Dictionary<DateTime, DailyTotal>();
        while (rdr.Read())
        {
            var d = DateTime.ParseExact(rdr.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            dict[d] = new DailyTotal { Day = d, Revenue = (decimal)rdr.GetDouble(1), OrderCount = rdr.GetInt32(2) };
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
        cmd.CommandText = @"SELECT oi.MenuItemName, SUM(oi.Quantity), SUM(oi.UnitPrice * oi.Quantity)
                            FROM OrderItems oi
                            INNER JOIN Orders o ON o.Id = oi.OrderId
                            WHERE o.CreatedAt >= $f AND o.CreatedAt < $t
                            GROUP BY oi.MenuItemName
                            ORDER BY SUM(oi.Quantity) DESC
                            LIMIT $l";
        cmd.Parameters.AddWithValue("$f", from.ToString(Iso, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$t", to.ToString(Iso, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$l", limit);
        using var rdr = cmd.ExecuteReader();
        var list = new List<TopItem>();
        while (rdr.Read())
            list.Add(new TopItem { Name = rdr.GetString(0), Quantity = rdr.GetInt32(1), Revenue = (decimal)rdr.GetDouble(2) });
        return list;
    }

    public static List<CategoryShare> CategoryBreakdown(DateTime from, DateTime to)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT IFNULL(NULLIF(mi.Category,''),'(uncategorized)'),
                                   SUM(oi.UnitPrice * oi.Quantity), SUM(oi.Quantity)
                            FROM OrderItems oi
                            INNER JOIN Orders o ON o.Id = oi.OrderId
                            LEFT JOIN MenuItems mi ON mi.Id = oi.MenuItemId
                            WHERE o.CreatedAt >= $f AND o.CreatedAt < $t
                            GROUP BY 1
                            ORDER BY 2 DESC";
        cmd.Parameters.AddWithValue("$f", from.ToString(Iso, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$t", to.ToString(Iso, CultureInfo.InvariantCulture));
        using var rdr = cmd.ExecuteReader();
        var list = new List<CategoryShare>();
        while (rdr.Read())
            list.Add(new CategoryShare { Category = rdr.GetString(0), Revenue = (decimal)rdr.GetDouble(1), Quantity = rdr.GetInt32(2) });
        return list;
    }

    public static List<HourlyBucket> HourlyBuckets(DateTime from, DateTime to)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT CAST(substr(CreatedAt, 12, 2) AS INTEGER) AS h,
                                   COUNT(*), SUM(Total)
                            FROM Orders WHERE CreatedAt >= $f AND CreatedAt < $t
                            GROUP BY h ORDER BY h";
        cmd.Parameters.AddWithValue("$f", from.ToString(Iso, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$t", to.ToString(Iso, CultureInfo.InvariantCulture));
        using var rdr = cmd.ExecuteReader();
        var dict = new Dictionary<int, HourlyBucket>();
        while (rdr.Read())
            dict[rdr.GetInt32(0)] = new HourlyBucket { Hour = rdr.GetInt32(0), OrderCount = rdr.GetInt32(1), Revenue = (decimal)rdr.GetDouble(2) };
        var list = new List<HourlyBucket>();
        for (int h = 0; h < 24; h++)
            list.Add(dict.TryGetValue(h, out var v) ? v : new HourlyBucket { Hour = h });
        return list;
    }
}
