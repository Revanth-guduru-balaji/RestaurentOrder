using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RestaurantOrder.Data;
using RestaurantOrder.Services;

namespace RestaurantOrder.Views;

public partial class OrderHistoryPage : UserControl
{
    private List<Order> _orders = new();
    private bool _suppressReload;

    private class OrderRow
    {
        public int Id { get; set; }
        public string IdText { get; set; } = "";
        public string DateText { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string PaymentMethod { get; set; } = "";
        public string TotalText { get; set; } = "";
        public Order Source { get; set; } = new();
    }

    public OrderHistoryPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _suppressReload = true;
            ToDate.SelectedDate = DateTime.Today;
            FromDate.SelectedDate = DateTime.Today.AddDays(-30);
            _suppressReload = false;
            Reload();
        };
    }

    private void DateRange_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressReload) return;
        Reload();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Reload();

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        SetDates(DateTime.Today, DateTime.Today);
    }

    private void Week_Click(object sender, RoutedEventArgs e)
    {
        SetDates(DateTime.Today.AddDays(-6), DateTime.Today);
    }

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

    private void Reload()
    {
        var from = (FromDate.SelectedDate ?? DateTime.Today.AddDays(-30)).Date;
        var to = (ToDate.SelectedDate ?? DateTime.Today).Date.AddDays(1);
        var search = (SearchBox.Text ?? "").Trim();

        _orders = OrderRepository.GetBetween(from, to, search);
        var rows = _orders.Select(o => new OrderRow
        {
            Id = o.Id,
            IdText = $"#{o.Id:D5}",
            DateText = o.CreatedAt.ToString("dd-MMM-yy HH:mm", CultureInfo.InvariantCulture),
            CustomerName = string.IsNullOrWhiteSpace(o.CustomerName) ? "—" : o.CustomerName,
            PaymentMethod = o.PaymentMethod,
            TotalText = Money.Format(o.Total),
            Source = o
        }).ToList();

        OrdersGrid.ItemsSource = rows;
        var total = _orders.Sum(o => o.Total);
        CountText.Text = $"{_orders.Count} order{(_orders.Count == 1 ? "" : "s")}";
        TotalText.Text = "Total: " + Money.Format(total);

        DetailTitle.Text = "Order details";
        DetailSub.Text = "Select an order to see items.";
        DetailItems.Children.Clear();
        DetailTotalText.Text = "";
        ReprintButton.IsEnabled = false;
        ReprintFeedback.Visibility = Visibility.Collapsed;
    }

    private void OrdersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OrdersGrid.SelectedItem is not OrderRow row)
        {
            ReprintButton.IsEnabled = false;
            return;
        }
        ReprintButton.IsEnabled = true;
        ReprintFeedback.Visibility = Visibility.Collapsed;

        var order = row.Source;
        if (order.Items.Count == 0) order.Items = OrderRepository.GetItems(order.Id);

        DetailTitle.Text = $"Order {row.IdText}";
        var custLine = string.IsNullOrWhiteSpace(order.CustomerName) ? "" : $" · {order.CustomerName}";
        DetailSub.Text = $"{order.CreatedAt:dd-MMM-yyyy HH:mm} · {order.PaymentMethod}{custLine}";

        DetailItems.Children.Clear();
        foreach (var it in order.Items)
        {
            var b = new Border
            {
                BorderBrush = (Brush)Application.Current.Resources["BrandBorderBrush"],
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 8, 0, 8)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = it.MenuItemName,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = it.MenuItemName,
                Foreground = (Brush)Application.Current.Resources["BrandTextBrush"]
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{it.Quantity} × {Money.Format(it.UnitPrice)}",
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["BrandSubtleTextBrush"]
            });
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var amount = new TextBlock
            {
                Text = Money.Format(it.LineTotal),
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["BrandTextBrush"],
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(amount, 1);
            grid.Children.Add(amount);

            b.Child = grid;
            DetailItems.Children.Add(b);
        }
        DetailTotalText.Text = Money.Format(order.Total);
    }

    private void Reprint_Click(object sender, RoutedEventArgs e)
    {
        if (OrdersGrid.SelectedItem is not OrderRow row) return;
        var order = row.Source;
        if (order.Items.Count == 0) order.Items = OrderRepository.GetItems(order.Id);
        try
        {
            if (ReceiptPrinter.PrintQuiet(order))
                ShowReprintFeedback($"✓ Reprinted at {DateTime.Now:HH:mm}");
            else
                ShowReprintFeedback("Reprint cancelled");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Print failed: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowReprintFeedback(string text)
    {
        ReprintFeedback.Text = text;
        ReprintFeedback.Visibility = Visibility.Visible;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        t.Tick += (_, _) =>
        {
            ReprintFeedback.Visibility = Visibility.Collapsed;
            t.Stop();
        };
        t.Start();
    }
}
