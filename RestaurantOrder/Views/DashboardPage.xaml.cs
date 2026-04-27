using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RestaurantOrder.Controls;
using RestaurantOrder.Data;
using RestaurantOrder.Services;

namespace RestaurantOrder.Views;

public partial class DashboardPage : UserControl
{
    private string _range = "Today";

    public DashboardPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadAll();
    }

    private void Range_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
        {
            _range = tag;
            LoadAll();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadAll();

    private (DateTime from, DateTime to, DateTime prevFrom, DateTime prevTo, string title) Resolve()
    {
        var now = DateTime.Now;
        DateTime from, to, prevFrom, prevTo;
        string title;
        switch (_range)
        {
            case "Week":
                from = now.Date.AddDays(-6); to = now.Date.AddDays(1);
                prevFrom = from.AddDays(-7); prevTo = from;
                title = $"Daily revenue · {from:dd MMM} – {now:dd MMM}";
                break;
            case "Month":
                from = new DateTime(now.Year, now.Month, 1);
                to = from.AddMonths(1);
                prevFrom = from.AddMonths(-1); prevTo = from;
                title = $"Daily revenue · {now:MMMM yyyy}";
                break;
            case "Year":
                from = new DateTime(now.Year, 1, 1);
                to = from.AddYears(1);
                prevFrom = from.AddYears(-1); prevTo = from;
                title = $"Monthly revenue · {now:yyyy}";
                break;
            default: // Today
                from = now.Date; to = from.AddDays(1);
                prevFrom = from.AddDays(-1); prevTo = from;
                title = $"Hourly revenue · {now:dd MMM yyyy}";
                break;
        }
        return (from, to, prevFrom, prevTo, title);
    }

    private void LoadAll()
    {
        var (from, to, prevFrom, prevTo, title) = Resolve();
        WelcomeText.Text = $"{DateTime.Now:dddd, dd MMM yyyy} · range: {_range.ToLowerInvariant()}";
        TrendTitle.Text = title;

        var sum = AnalyticsRepository.Summary(from, to);
        var prev = AnalyticsRepository.Summary(prevFrom, prevTo);

        RevenueText.Text = Money.Format(sum.revenue, withDecimals: false);
        OrdersText.Text = sum.orderCount.ToString();
        AvgTicketText.Text = Money.Format(sum.avgTicket, withDecimals: false);

        ApplyDelta(RevenueDeltaText, sum.revenue, prev.revenue);
        ApplyDelta(OrdersDeltaText, sum.orderCount, prev.orderCount);

        var allMenu = MenuRepository.GetAll();
        MenuItemsText.Text = allMenu.Count.ToString();
        var avail = allMenu.Count(i => i.IsAvailable);
        MenuItemsAvailText.Text = $"{avail} available";

        // Trend chart
        var trendPoints = new List<BarPoint>();
        if (_range == "Today")
        {
            var hourly = AnalyticsRepository.HourlyBuckets(from, to);
            // Show useful hours only (8am - 11pm) but include all
            foreach (var h in hourly)
                trendPoints.Add(new BarPoint
                {
                    Label = string.Format("{0:00}", h.Hour),
                    Value = (double)h.Revenue
                });
        }
        else if (_range == "Year")
        {
            var byMonth = new double[12];
            for (int m = 0; m < 12; m++)
            {
                var mStart = new DateTime(from.Year, m + 1, 1);
                var mEnd = mStart.AddMonths(1);
                byMonth[m] = (double)AnalyticsRepository.Summary(mStart, mEnd).revenue;
            }
            for (int m = 0; m < 12; m++)
                trendPoints.Add(new BarPoint
                {
                    Label = new DateTime(from.Year, m + 1, 1).ToString("MMM", CultureInfo.InvariantCulture),
                    Value = byMonth[m]
                });
        }
        else
        {
            var daily = AnalyticsRepository.DailyRevenue(from, to);
            foreach (var d in daily)
                trendPoints.Add(new BarPoint
                {
                    Label = d.Day.ToString("dd MMM", CultureInfo.InvariantCulture),
                    Value = (double)d.Revenue
                });
        }
        TrendChart.SetData(trendPoints);

        // Top items
        var top = AnalyticsRepository.TopItems(from, to, 6);
        var topVm = top.Select((t, idx) => new
        {
            Rank = (idx + 1).ToString(),
            Name = t.Name,
            QtyText = $"{t.Quantity} sold",
            RevenueText = Money.Format(t.Revenue, withDecimals: false)
        }).ToList();
        TopItemsList.ItemsSource = topVm;
        EmptyTopItems.Visibility = topVm.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Hourly chart (always show)
        var hourlyAll = AnalyticsRepository.HourlyBuckets(from, to);
        var hourlyPoints = hourlyAll
            .Where(h => h.Hour >= 6 && h.Hour <= 23)
            .Select(h => new BarPoint
            {
                Label = string.Format("{0:00}", h.Hour),
                Value = h.OrderCount
            }).ToList();
        HourlyChart.SetData(hourlyPoints);

        // Category breakdown
        var cats = AnalyticsRepository.CategoryBreakdown(from, to);
        var maxRev = cats.Count > 0 ? (double)cats.Max(c => c.Revenue) : 1.0;
        if (maxRev <= 0) maxRev = 1;
        var catVm = cats.Select(c => new
        {
            Category = c.Category,
            RevenueText = Money.Format(c.Revenue, withDecimals: false),
            FillRatio = new GridLength(Math.Max(0.02, (double)c.Revenue / maxRev), GridUnitType.Star),
            RemainRatio = new GridLength(Math.Max(0.001, 1.0 - ((double)c.Revenue / maxRev)), GridUnitType.Star)
        }).ToList();
        CategoryList.ItemsSource = catVm;
        EmptyCategory.Visibility = catVm.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        StoragePathText.Text = $"💾 Local data file: {Database.DbPath}";
    }

    private static void ApplyDelta(TextBlock label, decimal current, decimal prev)
    {
        if (prev == 0)
        {
            label.Text = current == 0 ? "no orders yet" : "new activity";
            label.Foreground = (Brush)Application.Current.Resources["BrandSubtleTextBrush"];
            return;
        }
        var pct = (double)((current - prev) / prev) * 100.0;
        var positive = pct >= 0;
        var arrow = positive ? "▲" : "▼";
        label.Text = $"{arrow} {Math.Abs(pct):0.0}% vs previous";
        label.Foreground = (Brush)Application.Current.Resources[positive ? "BrandSuccessBrush" : "BrandDangerBrush"];
    }

    private static void ApplyDelta(TextBlock label, int current, int prev)
        => ApplyDelta(label, (decimal)current, (decimal)prev);
}
