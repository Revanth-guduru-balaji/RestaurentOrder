using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RestaurantOrder.Controls;
using RestaurantOrder.Data;
using RestaurantOrder.Services;

namespace RestaurantOrder.Views;

public partial class OrderHistoryPage : UserControl
{
    private List<Order> _orders = new();
    private OrderRowVm? _selected;
    private bool _suppressReload;
    private readonly ObservableCollection<object> _items = new();

    public OrderHistoryPage()
    {
        InitializeComponent();
        OrdersList.ItemsSource = _items;
        Loaded += (_, _) =>
        {
            _suppressReload = true;
            ToDate.SelectedDate = DateTime.Today;
            FromDate.SelectedDate = DateTime.Today.AddDays(-6);
            _suppressReload = false;
            Reload();
        };
        // F4 = focus search, F2 = reprint selected
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F4)
            {
                SearchBox.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.F2 && _selected != null)
            {
                Reprint_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        };
    }

    // -----------------------------------------------------------------
    // Range controls
    // -----------------------------------------------------------------
    private void DateRange_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressReload) return;
        Reload();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Reload();

    private void Today_Click(object sender, RoutedEventArgs e) => SetDates(DateTime.Today, DateTime.Today);
    private void Week_Click(object sender, RoutedEventArgs e) => SetDates(DateTime.Today.AddDays(-6), DateTime.Today);
    private void Month_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Today;
        SetDates(new DateTime(now.Year, now.Month, 1), now);
    }

    private void SetDates(DateTime from, DateTime to)
    {
        _suppressReload = true;
        FromDate.SelectedDate = from;
        ToDate.SelectedDate = to;
        _suppressReload = false;
        Reload();
    }

    // -----------------------------------------------------------------
    // Reload pipeline: fetch -> KPIs -> sparkline -> list (with day dividers)
    // -----------------------------------------------------------------
    private void Reload()
    {
        var from = (FromDate.SelectedDate ?? DateTime.Today.AddDays(-6)).Date;
        var to = (ToDate.SelectedDate ?? DateTime.Today).Date.AddDays(1);
        var search = (SearchBox.Text ?? "").Trim();

        _orders = OrderRepository.GetBetween(from, to, search);

        UpdateKpis();
        UpdateSparkline(from, to);
        BuildList();
        ClearDetail();
    }

    private void UpdateKpis()
    {
        var live = _orders.Where(o => !o.IsVoided).ToList();
        int n = live.Count;
        int voided = _orders.Count - n;
        decimal revenue = live.Sum(o => o.Total);
        decimal avg = n > 0 ? revenue / n : 0;
        KpiOrdersValue.Text = n.ToString("N0", CultureInfo.InvariantCulture);
        KpiOrdersSub.Text = voided > 0
            ? $"in selected range · {voided} voided"
            : "in selected range";
        KpiRevenueValue.Text = "₹ " + revenue.ToString("N0", CultureInfo.InvariantCulture);
        KpiRevenueSub.Text = $"₹ {avg:N0} avg";
        KpiAvgValue.Text = Money.Format(avg);
        CountText.Text = $"{n} orders · {Money.Format(revenue)}";

        // Reprints KPI for the active range
        var from = (FromDate.SelectedDate ?? DateTime.Today.AddDays(-6)).Date;
        var to = (ToDate.SelectedDate ?? DateTime.Today).Date.AddDays(1);
        int reprints = OrderRepository.CountReprintsBetween(from, to);
        KpiReprintsValue.Text = reprints.ToString("N0", CultureInfo.InvariantCulture);
        KpiReprintsSub.Text = n > 0
            ? $"{(reprints * 100.0 / n):0.#}% of orders"
            : "0% of orders";
    }

    private void UpdateSparkline(DateTime from, DateTime to)
    {
        // One bucket per day in range
        var byDay = _orders
            .GroupBy(o => o.CreatedAt.Date)
            .ToDictionary(g => g.Key, g => g.Count());

        var points = new List<BarPoint>();
        var cursor = from.Date;
        var end = to.Date.AddDays(-1); // inclusive
        while (cursor <= end)
        {
            int v = byDay.TryGetValue(cursor, out var c) ? c : 0;
            points.Add(new BarPoint { Label = cursor.ToString("dd MMM"), Value = v });
            cursor = cursor.AddDays(1);
        }

        Spark.SetData(points);

        if (points.Count > 0 && points.Any(p => p.Value > 0))
        {
            var peak = points.OrderByDescending(p => p.Value).First();
            SparkPeak.Text = $"{peak.Label} · {peak.Value:N0} peak";
        }
        else
        {
            SparkPeak.Text = "no orders";
        }
    }

    private void BuildList()
    {
        _items.Clear();
        if (_orders.Count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            return;
        }
        EmptyState.Visibility = Visibility.Collapsed;

        // Group by day (already sorted DESC by CreatedAt)
        DateTime? curDay = null;
        decimal dayTotal = 0;
        int dayCount = 0;
        DayDividerVm? curDivider = null;

        foreach (var o in _orders)
        {
            var day = o.CreatedAt.Date;
            if (curDay == null || day != curDay)
            {
                if (curDivider != null)
                    curDivider.Set(dayCount, dayTotal);
                curDivider = new DayDividerVm(day);
                _items.Add(curDivider);
                curDay = day;
                dayTotal = 0;
                dayCount = 0;
            }
            if (!o.IsVoided)
            {
                dayTotal += o.Total;
                dayCount++;
            }
            _items.Add(new OrderRowVm(o, () => SelectOrder(o.Id)));
        }
        if (curDivider != null)
            curDivider.Set(dayCount, dayTotal);
    }

    // -----------------------------------------------------------------
    // Selection + detail pane
    // -----------------------------------------------------------------
    private void SelectOrder(int id)
    {
        var order = _orders.FirstOrDefault(o => o.Id == id);
        if (order == null) return;
        if (order.Items.Count == 0) order.Items = OrderRepository.GetItems(order.Id);

        // Update row backgrounds
        _selected = null;
        foreach (var x in _items.OfType<OrderRowVm>())
        {
            x.IsSelected = x.Source.Id == id;
            x.Refresh();
            if (x.IsSelected) _selected = x;
        }
        // Force re-bind by replacing items (cheap, list is small)
        var snapshot = _items.ToList();
        _items.Clear();
        foreach (var s in snapshot) _items.Add(s);

        // Header
        DetailTitle.Text = $"Order {OrderRowVm.IdString(order.Id)}";
        StatusBadgeClosed.Visibility = order.IsVoided ? Visibility.Collapsed : Visibility.Visible;
        StatusBadgeVoided.Visibility = order.IsVoided ? Visibility.Visible : Visibility.Collapsed;
        VoidButtonText.Text = order.IsVoided ? "Restore order" : "Void order";
        BuildDetailMeta(order);
        BuildDetailItems(order);
        BuildDetailTotals(order);
        ReprintButton.IsEnabled = !order.IsVoided;
        VoidButton.IsEnabled = true;
    }

    private void ClearDetail()
    {
        _selected = null;
        DetailTitle.Text = "Order details";
        DetailMetaPanel.Children.Clear();
        DetailItems.Children.Clear();
        DetailTotalsPanel.Children.Clear();
        StatusBadgeClosed.Visibility = Visibility.Visible;
        StatusBadgeVoided.Visibility = Visibility.Collapsed;
        VoidButtonText.Text = "Void order";
        ReprintButton.IsEnabled = false;
        VoidButton.IsEnabled = false;
    }

    private void BuildDetailMeta(Order order)
    {
        DetailMetaPanel.Children.Clear();
        AddMeta("Date", order.CreatedAt.ToString("ddd, dd MMM · hh:mm tt"));
        AddMeta("Customer", string.IsNullOrWhiteSpace(order.CustomerName) ? "Walk-in" : order.CustomerName!);
        AddMeta("Pay", order.PaymentMethod);
    }

    private void AddMeta(string key, string value)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 14, 4)
        };
        sp.Children.Add(new TextBlock
        {
            Text = key,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["BrandSubtleTextBrush"],
            Margin = new Thickness(0, 0, 6, 0)
        });
        sp.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)Application.Current.Resources["BrandTextBrush"]
        });
        DetailMetaPanel.Children.Add(sp);
    }

    private void BuildDetailItems(Order order)
    {
        DetailItems.Children.Clear();
        foreach (var it in order.Items)
        {
            var grid = new Grid { Margin = new Thickness(18, 8, 18, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // qb badge — 28x28 soft surface with bold qty
            var qb = new Border
            {
                Background = (Brush)Application.Current.Resources["BrandSoftSurfaceBrush"],
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(6),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            qb.Child = new TextBlock
            {
                Text = it.Quantity.ToString(),
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["BrandSubtleTextDarkBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(qb, 0);
            grid.Children.Add(qb);

            var info = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = it.MenuItemName,
                FontWeight = FontWeights.Medium,
                FontSize = 14,
                Foreground = (Brush)Application.Current.Resources["BrandTextBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = it.MenuItemName
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{Money.Format(it.UnitPrice)} each",
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["BrandSubtleTextBrush"],
                Margin = new Thickness(0, 1, 0, 0)
            });
            Grid.SetColumn(info, 1);
            grid.Children.Add(info);

            var lt = new TextBlock
            {
                Text = Money.Format(it.LineTotal),
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Foreground = (Brush)Application.Current.Resources["BrandTextBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(lt, 2);
            grid.Children.Add(lt);

            DetailItems.Children.Add(grid);
        }
    }

    private void BuildDetailTotals(Order order)
    {
        DetailTotalsPanel.Children.Clear();
        AddTotalRow("Items", order.PieceCount.ToString(CultureInfo.InvariantCulture), grand: false);
        decimal subtotal = order.Subtotal > 0m ? order.Subtotal : order.Total;
        AddTotalRow("Subtotal", Money.Format(subtotal), grand: false);
        if (order.TaxAmount > 0m)
            AddTotalRow("Tax", Money.Format(order.TaxAmount), grand: false);
        if (order.DiscountAmount > 0m)
            AddTotalRow("Discount", "− " + Money.Format(order.DiscountAmount), grand: false);
        if (order.RoundingAmount != 0m)
            AddTotalRow("Rounding",
                (order.RoundingAmount >= 0 ? "+ " : "− ") + Money.Format(Math.Abs(order.RoundingAmount)),
                grand: false);
        AddTotalRow("Total", Money.Format(order.Total), grand: true);
    }

    private void AddTotalRow(string label, string value, bool grand)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, grand ? 8 : 2, 0, 2)
        };
        if (grand)
        {
            grid.Margin = new Thickness(0, 6, 0, 0);
            var sep = new Border
            {
                Height = 1,
                Background = (Brush)Application.Current.Resources["BrandBorderBrush"],
                Margin = new Thickness(0, 0, 0, 8),
                VerticalAlignment = VerticalAlignment.Top
            };
            DetailTotalsPanel.Children.Add(sep);
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = new TextBlock
        {
            Text = label,
            FontSize = grand ? 16 : 13,
            FontWeight = grand ? FontWeights.Bold : FontWeights.Regular,
            Foreground = (Brush)Application.Current.Resources[grand ? "BrandTextBrush" : "BrandSubtleTextDarkBrush"]
        };
        var r = new TextBlock
        {
            Text = value,
            FontSize = grand ? 18 : 13,
            FontWeight = grand ? FontWeights.Bold : FontWeights.Regular,
            Foreground = grand
                ? (Brush)Application.Current.Resources["BrandPrimaryBrush"]
                : (Brush)Application.Current.Resources["BrandTextBrush"],
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(r, 1);
        grid.Children.Add(l);
        grid.Children.Add(r);
        DetailTotalsPanel.Children.Add(grid);
    }

    // -----------------------------------------------------------------
    // Action bar
    // -----------------------------------------------------------------
    private void Reprint_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var order = _selected.Source;
        if (order.IsVoided) return;
        if (order.Items.Count == 0) order.Items = OrderRepository.GetItems(order.Id);
        try
        {
            if (ReceiptPrinter.PrintQuiet(order))
            {
                OrderRepository.LogReprint(order.Id);
                UpdateKpis();  // refresh reprints KPI
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Print failed: " + ex.Message, "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void VoidOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var order = _selected.Source;
        bool currentlyVoided = order.IsVoided;
        var verb = currentlyVoided ? "Restore" : "Void";
        var msg = currentlyVoided
            ? $"Restore order {OrderRowVm.IdString(order.Id)} into the daily totals?"
            : $"Void order {OrderRowVm.IdString(order.Id)}? It stays in the history but is removed from KPI sums.";
        var ok = MessageBox.Show(msg, $"{verb} order",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        try
        {
            OrderRepository.SetVoided(order.Id, !currentlyVoided);
            order.IsVoided = !currentlyVoided;
            Reload(); // refresh KPIs + list highlighting
            SelectOrder(order.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{verb} failed: " + ex.Message, "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_orders.Count == 0)
        {
            MessageBox.Show("Nothing to export in the selected range.", "Export CSV",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var sfd = new SaveFileDialog
        {
            Filter = "CSV files|*.csv",
            FileName = $"orders-{DateTime.Today:yyyy-MM-dd}.csv",
            Title = "Export orders to CSV"
        };
        if (sfd.ShowDialog() != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Order #,Date,Customer,Payment,Items,Pieces,Total");
            foreach (var o in _orders)
            {
                string cust = string.IsNullOrWhiteSpace(o.CustomerName) ? "Walk-in" : o.CustomerName;
                sb.AppendLine(string.Join(",", new[]
                {
                    OrderRowVm.IdString(o.Id),
                    o.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    Csv(cust),
                    Csv(o.PaymentMethod),
                    o.LineCount.ToString(CultureInfo.InvariantCulture),
                    o.PieceCount.ToString(CultureInfo.InvariantCulture),
                    o.Total.ToString("0.00", CultureInfo.InvariantCulture)
                }));
            }
            File.WriteAllText(sfd.FileName, sb.ToString(), new UTF8Encoding(true));
            MessageBox.Show($"Exported {_orders.Count} orders.", "Export complete",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Export failed: " + ex.Message, "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string Csv(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}

// =====================================================================
// View-models for the heterogeneous list (day dividers + order rows)
// =====================================================================
public class DayDividerVm
{
    public DayDividerVm(DateTime day) { Day = day; LabelText = FormatLabel(day); TotalText = ""; }
    public DateTime Day { get; }
    public string LabelText { get; private set; }
    public string TotalText { get; private set; }
    public void Set(int count, decimal revenue)
    {
        TotalText = $"{count} order{(count == 1 ? "" : "s")} · {Money.Format(revenue)}";
    }
    private static string FormatLabel(DateTime d)
    {
        var today = DateTime.Today;
        var label = d.ToString("dddd · dd MMM");
        if (d == today) label += " · today";
        else if (d == today.AddDays(-1)) label += " · yesterday";
        return label;
    }
}

public class OrderRowVm : System.ComponentModel.INotifyPropertyChanged
{
    public Order Source { get; }
    public Action ClickAction { get; }
    public ICommand SelectCommand { get; }

    public OrderRowVm(Order o, Action onClick)
    {
        Source = o;
        ClickAction = onClick;
        SelectCommand = new RelayCommand(_ => onClick());
    }

    public bool IsSelected { get; set; }

    public string IdText => IdString(Source.Id);
    public static string IdString(int id) => $"#{id:D5}";
    public string CustomerName => string.IsNullOrWhiteSpace(Source.CustomerName) ? "Walk-in" : Source.CustomerName!;
    public string PaymentMethodUpper => (Source.PaymentMethod ?? "Cash").ToUpperInvariant();
    public string TimeText => Source.CreatedAt.ToString("hh:mm tt");
    public string ItemsText => $"{Source.LineCount} item{(Source.LineCount == 1 ? "" : "s")}";
    public string TotalText => Money.Format(Source.Total);
    public string PiecesText => $"{Source.PieceCount} pcs";

    public bool IsVoided => Source.IsVoided;
    public Visibility VoidedBadgeVisibility => Source.IsVoided ? Visibility.Visible : Visibility.Collapsed;
    public double RowItemOpacity => Source.IsVoided ? 0.55 : 1.0;

    public Brush PayBg => PaymentColors(Source.PaymentMethod).bg;
    public Brush PayFg => PaymentColors(Source.PaymentMethod).fg;

    public Brush RowBackground => IsSelected
        ? (Brush)Application.Current.Resources["InfoBgBrush"]
        : Brushes.Transparent;

    public Thickness RowPadding => IsSelected
        ? new Thickness(11, 12, 14, 12) // 3px less on the left to make room for the border
        : new Thickness(14, 12, 14, 12);

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(RowBackground)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(RowPadding)));
    }

    private static (Brush bg, Brush fg) PaymentColors(string? method) => method?.Trim().ToLowerInvariant() switch
    {
        "upi" => ((Brush)Application.Current.Resources["SwatchBeveragesBg"],
                  (Brush)Application.Current.Resources["SwatchBeveragesFg"]),
        "card" => ((Brush)Application.Current.Resources["SwatchSweetsBg"],
                   (Brush)Application.Current.Resources["SwatchSweetsFg"]),
        _ => ((Brush)Application.Current.Resources["SuccessBgBrush"],
              (Brush)Application.Current.Resources["SuccessFgBrush"]),
    };
}

internal class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    public RelayCommand(Action<object?> execute) { _execute = execute; }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute(parameter);
    public event EventHandler? CanExecuteChanged
    {
        add { } remove { }
    }
}
