using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private bool _filterWalkIn;
    private bool _filterParcel;
    private bool _datesInitialized;

    // Debounce search so a DB JOIN doesn't run on every keystroke.
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    // Bumped on every reload so a slow background fetch can't overwrite the UI
    // with stale results after a newer reload has started.
    private int _reloadGen;

    /// 30-minute time options for the From/To time combos. The trailing
    /// 23:59 value lets the cashier include the entire last day without
    /// resorting to free-text entry.
    private static readonly string[] TimeOptions =
        Enumerable.Range(0, 48)
            .Select(half => $"{half / 2:D2}:{(half % 2 == 0 ? "00" : "30")}")
            .Append("23:59")
            .ToArray();

    public OrderHistoryPage()
    {
        InitializeComponent();
        OrdersList.ItemsSource = _items;
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Reload(); };
        Unloaded += (_, _) => _searchTimer.Stop();
        Loaded += (_, _) =>
        {
            // Initialize once; page is cached across nav.
            if (!_datesInitialized)
            {
                _suppressReload = true;
                FromTimeCombo.ItemsSource = TimeOptions;
                ToTimeCombo.ItemsSource = TimeOptions;
                FromTimeCombo.SelectedItem = "00:00";
                ToTimeCombo.SelectedItem = "23:59";
                ToDate.SelectedDate = DateTime.Today;
                FromDate.SelectedDate = DateTime.Today.AddDays(-6);
                _suppressReload = false;
                _datesInitialized = true;
            }
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
        ClearPresetSelection();
        Reload();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressReload) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void TimeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressReload) return;
        ClearPresetSelection(); // a manual time edit means 'Custom'
        Reload();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn || btn.Tag is not string tag) return;

        // Single-select among presets; clicking the active one keeps it on.
        foreach (var p in PresetButtons()) p.IsChecked = ReferenceEquals(p, btn);

        var today = DateTime.Today;
        DateTime from, to;
        switch (tag)
        {
            case "Today":     from = today; to = today; break;
            case "Yesterday": from = today.AddDays(-1); to = today.AddDays(-1); break;
            case "Week":      from = today.AddDays(-6); to = today; break;
            case "Thirty":    from = today.AddDays(-29); to = today; break;
            case "Month":     from = new DateTime(today.Year, today.Month, 1); to = today; break;
            case "All":       from = today.AddYears(-10); to = today; break;
            default: return;
        }

        _suppressReload = true;
        FromDate.SelectedDate = from;
        ToDate.SelectedDate = to;
        FromTimeCombo.SelectedItem = "00:00";
        ToTimeCombo.SelectedItem = "23:59";
        _suppressReload = false;
        Reload();
    }

    private IEnumerable<ToggleButton> PresetButtons()
    {
        yield return PresetToday;
        yield return PresetYesterday;
        yield return PresetWeek;
        yield return PresetThirty;
        yield return PresetMonth;
        yield return PresetAll;
    }

    private void ClearPresetSelection()
    {
        // Manual date / time edit decouples the view from any preset.
        foreach (var p in PresetButtons()) p.IsChecked = false;
    }

    private void ChannelChip_Click(object sender, RoutedEventArgs e)
    {
        _filterWalkIn = WalkInChip.IsChecked == true;
        _filterParcel = ParcelChip.IsChecked == true;
        Reload();
    }

    /// Parses "HH:mm" or "H:m" into a TimeSpan. Empty / unparseable input
    /// returns null so callers can fall back to a default.
    private static TimeSpan? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (TimeSpan.TryParseExact(text.Trim(), new[] { @"h\:m", @"hh\:mm" },
                CultureInfo.InvariantCulture, out var ts))
            return ts;
        return null;
    }

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
    /// The single source of truth for the [from, to) window selected by the
    /// date pickers + time combos, including the 23:59 → end-of-day rule. Used
    /// by both the orders list and the KPI panel so they can never disagree.
    private (DateTime from, DateTime to) GetSelectedRange()
    {
        var fromDay = (FromDate.SelectedDate ?? DateTime.Today.AddDays(-6)).Date;
        var toDay = (ToDate.SelectedDate ?? DateTime.Today).Date;
        var fromTime = ParseTime(FromTimeCombo?.SelectedItem as string) ?? TimeSpan.Zero;
        var toTimeOpt = ParseTime(ToTimeCombo?.SelectedItem as string);
        var from = fromDay + fromTime;
        // To = end-of-day exclusive when no explicit time picked. 23:59 maps
        // to 23:59:59.999 so the last day's late orders are included.
        DateTime to = toTimeOpt.HasValue
            ? (toTimeOpt.Value == new TimeSpan(23, 59, 0) ? toDay.AddDays(1).AddTicks(-1) : toDay + toTimeOpt.Value)
            : toDay.AddDays(1);
        return (from, to);
    }

    private void Reload() => _ = ReloadAsync();

    // Fetches off the UI thread so switching to this tab (or typing in search)
    // never freezes the window, even on a large history. A generation counter
    // discards results from a reload that a newer one has already superseded.
    private async Task ReloadAsync()
    {
        var (from, to) = GetSelectedRange();
        var search = (SearchBox.Text ?? "").Trim();
        int gen = ++_reloadGen;

        List<Order> orders;
        try
        {
            orders = await Task.Run(() => OrderRepository.GetBetween(from, to, search));
        }
        catch (Exception ex)
        {
            if (gen == _reloadGen)
                MessageBox.Show("Couldn't load orders: " + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (gen != _reloadGen) return; // a newer reload superseded this one

        _orders = orders;

        // Walk-in / Parcel chips: when both are off, show everything; when one
        // is on, narrow to that channel; when both are on, show both (i.e. all)
        // since an order is exactly one of the two.
        if (_filterWalkIn && !_filterParcel)
            _orders = _orders.Where(o => !o.IsParcel).ToList();
        else if (_filterParcel && !_filterWalkIn)
            _orders = _orders.Where(o => o.IsParcel).ToList();

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
        // Route currency through the single Money formatter (en-IN grouping)
        // instead of hand-built strings, so grouping is consistent across KPIs.
        KpiRevenueValue.Text = Money.Format(revenue, withDecimals: false);
        KpiRevenueSub.Text = $"{Money.Format(avg, withDecimals: false)} avg";
        KpiAvgValue.Text = Money.Format(avg);
        CountText.Text = $"{n} orders · {Money.Format(revenue)}";

        // Reprints KPI for the active range — same window as the orders list.
        var (from, to) = GetSelectedRange();
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
        // 'to' is exclusive; subtract a tick before truncating so a 22:00
        // upper bound on day X still buckets day X.
        var end = to.AddTicks(-1).Date;
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

        // Editable Payment dropdown — committed to DB on selection change.
        var payRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 4) };
        payRow.Children.Add(new TextBlock
        {
            Text = "Pay",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["BrandSubtleTextBrush"],
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        var payCombo = new ComboBox { MinWidth = 80, Tag = order };
        foreach (var m in PaymentMethods.All) payCombo.Items.Add(m);
        payCombo.SelectedItem = string.IsNullOrWhiteSpace(order.PaymentMethod)
            ? PaymentMethods.Cash : order.PaymentMethod;
        payCombo.SelectionChanged += PaymentMethod_Changed;
        payRow.Children.Add(payCombo);
        DetailMetaPanel.Children.Add(payRow);

        // Editable Walk-in / Parcel dropdown.
        var channelRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 4) };
        channelRow.Children.Add(new TextBlock
        {
            Text = "Channel",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["BrandSubtleTextBrush"],
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        var channelCombo = new ComboBox { MinWidth = 90, Tag = order };
        channelCombo.Items.Add(OrderChannels.WalkIn);
        channelCombo.Items.Add(OrderChannels.Parcel);
        channelCombo.SelectedItem = order.IsParcel ? OrderChannels.Parcel : OrderChannels.WalkIn;
        channelCombo.SelectionChanged += Channel_Changed;
        channelRow.Children.Add(channelCombo);
        DetailMetaPanel.Children.Add(channelRow);
    }

    private void PaymentMethod_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.Tag is not Order order) return;
        var newMethod = cb.SelectedItem as string ?? "Cash";
        if (order.PaymentMethod == newMethod) return;
        try
        {
            OrderRepository.UpdatePaymentMethod(order.Id, newMethod);
            order.PaymentMethod = newMethod;
            // Refresh the row's payment chip
            foreach (var row in _items.OfType<OrderRowVm>().Where(r => r.Source.Id == order.Id))
                row.Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Update failed: " + ex.Message, "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Channel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.Tag is not Order order) return;
        bool newIsParcel = (cb.SelectedItem as string) == OrderChannels.Parcel;
        if (order.IsParcel == newIsParcel) return;
        try
        {
            OrderRepository.UpdateIsParcel(order.Id, newIsParcel);
            order.IsParcel = newIsParcel;
            foreach (var row in _items.OfType<OrderRowVm>().Where(r => r.Source.Id == order.Id))
                row.Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Update failed: " + ex.Message, "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            if (ReceiptPrinter.PrintQuiet(order).BillPrinted)
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

    private async void VoidOrder_Click(object sender, RoutedEventArgs e)
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
            await ReloadAsync();      // refresh KPIs + list highlighting
            SelectOrder(order.Id);    // re-select once the reload has finished
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
    public Visibility ParcelBadgeVisibility => Source.IsParcel ? Visibility.Visible : Visibility.Collapsed;
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
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(PaymentMethodUpper)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(PayBg)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(PayFg)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ParcelBadgeVisibility)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(VoidedBadgeVisibility)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsVoided)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(RowItemOpacity)));
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
