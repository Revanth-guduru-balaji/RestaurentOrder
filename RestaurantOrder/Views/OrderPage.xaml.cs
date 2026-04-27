using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RestaurantOrder.Data;
using RestaurantOrder.Services;
using MenuItem = RestaurantOrder.Data.MenuItem;

namespace RestaurantOrder.Views;

public partial class OrderPage : UserControl
{
    private readonly List<MenuItem> _allItems = new();
    private string _activeCategory = "All";
    private readonly Dictionary<int, CartLine> _cart = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(15) };

    private class CartLine
    {
        public MenuItem Item { get; init; } = new();
        public int Qty { get; set; }
    }

    public OrderPage()
    {
        InitializeComponent();
        Loaded += OrderPage_Loaded;
        Unloaded += (_, _) => _clock.Stop();
        _clock.Tick += (_, _) => UpdateClock();
    }

    private void OrderPage_Loaded(object sender, RoutedEventArgs e)
    {
        _allItems.Clear();
        _allItems.AddRange(MenuRepository.GetAll(onlyAvailable: true));
        BuildCategoryTabs();
        RenderMenu();
        UpdateClock();
        _clock.Start();
    }

    private void UpdateClock()
    {
        OrderTimeText.Text = DateTime.Now.ToString("ddd, dd MMM yyyy · hh:mm tt");
    }

    private void BuildCategoryTabs()
    {
        CategoryTabs.Items.Clear();
        var cats = new List<string> { "All" };
        cats.AddRange(_allItems.Select(i => string.IsNullOrWhiteSpace(i.Category) ? "Other" : i.Category)
                               .Distinct().OrderBy(c => c));
        foreach (var c in cats)
        {
            var btn = new Button
            {
                Content = c,
                Tag = c,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(14, 6, 14, 6),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                FontSize = 13
            };
            ApplyCategoryStyle(btn, c == _activeCategory);
            btn.Click += (_, _) =>
            {
                _activeCategory = c;
                BuildCategoryTabs();
                RenderMenu();
            };
            CategoryTabs.Items.Add(btn);
        }
    }

    private static void ApplyCategoryStyle(Button btn, bool active)
    {
        if (active)
        {
            btn.Background = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            btn.Foreground = Brushes.White;
            btn.BorderBrush = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
        }
        else
        {
            btn.Background = Brushes.White;
            btn.Foreground = (Brush)Application.Current.Resources["BrandTextBrush"];
            btn.BorderBrush = (Brush)Application.Current.Resources["BrandBorderBrush"];
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderMenu();

    private void RenderMenu()
    {
        MenuList.Items.Clear();
        var search = (SearchBox.Text ?? "").Trim();
        IEnumerable<MenuItem> q = _allItems;
        if (_activeCategory != "All")
            q = q.Where(i => (string.IsNullOrWhiteSpace(i.Category) ? "Other" : i.Category) == _activeCategory);
        if (!string.IsNullOrEmpty(search))
            q = q.Where(i => i.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

        foreach (var it in q)
            MenuList.Items.Add(BuildTile(it));
    }

    private Button BuildTile(MenuItem item)
    {
        var btn = new Button
        {
            Style = (Style)Application.Current.Resources["MenuTile"],
            Width = 180,
            Height = 110,
            Tag = item
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["BrandTextBrush"]
        });
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.Category) ? " " : item.Category,
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = (Brush)Application.Current.Resources["BrandSubtleTextBrush"]
        });
        var spacer = new Grid { Height = 8 };
        stack.Children.Add(spacer);
        stack.Children.Add(new TextBlock
        {
            Text = Money.Format(item.Price),
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["BrandPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Bottom
        });
        btn.Content = stack;
        btn.Click += (_, _) => AddToCart(item);
        return btn;
    }

    private void AddToCart(MenuItem item)
    {
        if (_cart.TryGetValue(item.Id, out var line)) line.Qty++;
        else _cart[item.Id] = new CartLine { Item = item, Qty = 1 };
        RenderCart();
    }

    private void RenderCart()
    {
        CartList.Children.Clear();
        decimal total = 0;
        int count = 0;
        if (_cart.Count == 0)
        {
            var empty = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(8, 60, 8, 8)
            };
            empty.Children.Add(new TextBlock
            {
                Text = "🛒",
                FontSize = 42,
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = 0.4
            });
            empty.Children.Add(new TextBlock
            {
                Text = "Cart is empty",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["BrandTextBrush"]
            });
            empty.Children.Add(new TextBlock
            {
                Text = "Tap an item on the left to add it",
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["BrandSubtleTextBrush"]
            });
            CartList.Children.Add(empty);
        }
        foreach (var kv in _cart)
        {
            var ln = kv.Value;
            total += ln.Item.Price * ln.Qty;
            count += ln.Qty;
            CartList.Children.Add(BuildCartRow(ln));
        }
        ItemCountText.Text = count.ToString();
        TotalText.Text = Money.Format(total);
    }

    private Border BuildCartRow(CartLine line)
    {
        var b = new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["BrandBorderBrush"],
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(2, 10, 2, 10)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        info.Children.Add(new TextBlock
        {
            Text = line.Item.Name,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = line.Item.Name,
            Foreground = (Brush)Application.Current.Resources["BrandTextBrush"]
        });
        info.Children.Add(new TextBlock
        {
            Text = $"{Money.Format(line.Item.Price)}  ·  total {Money.Format(line.Item.Price * line.Qty)}",
            Foreground = (Brush)Application.Current.Resources["BrandSubtleTextBrush"],
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        var qtyPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var minus = MakeQtyButton("−");
        minus.Click += (_, _) =>
        {
            line.Qty--;
            if (line.Qty <= 0) _cart.Remove(line.Item.Id);
            RenderCart();
        };
        var plus = MakeQtyButton("+");
        plus.Click += (_, _) => { line.Qty++; RenderCart(); };
        var qtyText = new TextBlock
        {
            Text = line.Qty.ToString(),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 32,
            TextAlignment = TextAlignment.Center,
            FontWeight = FontWeights.Bold,
            FontSize = 15,
            Foreground = (Brush)Application.Current.Resources["BrandTextBrush"]
        };
        qtyPanel.Children.Add(minus);
        qtyPanel.Children.Add(qtyText);
        qtyPanel.Children.Add(plus);
        Grid.SetColumn(qtyPanel, 1);
        grid.Children.Add(qtyPanel);

        b.Child = grid;
        return b;
    }

    private static Button MakeQtyButton(string text)
    {
        return new Button
        {
            Content = text,
            Margin = new Thickness(2, 0, 2, 0),
            Style = (Style)Application.Current.Resources["QtyButton"]
        };
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _cart.Clear();
        CustomerNameBox.Text = "";
        RenderCart();
    }

    private void PlaceAndPrint_Click(object sender, RoutedEventArgs e)
    {
        if (_cart.Count == 0)
        {
            MessageBox.Show("Cart is empty.", "No items", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var order = new Order
        {
            CreatedAt = DateTime.Now,
            CustomerName = string.IsNullOrWhiteSpace(CustomerNameBox.Text) ? null : CustomerNameBox.Text.Trim(),
            PaymentMethod = PayCash.IsChecked == true ? "Cash" : (PayUpi.IsChecked == true ? "UPI" : "Card")
        };
        decimal total = 0;
        foreach (var kv in _cart)
        {
            var line = kv.Value;
            var item = new OrderItem
            {
                MenuItemId = line.Item.Id,
                MenuItemName = line.Item.Name,
                UnitPrice = line.Item.Price,
                Quantity = line.Qty
            };
            order.Items.Add(item);
            total += item.LineTotal;
        }
        order.Total = total;

        OrderRepository.Create(order);

        bool printed = false;
        string? printError = null;
        if (AppSettings.Current.AutoPrint)
        {
            try { printed = ReceiptPrinter.PrintQuiet(order); }
            catch (Exception ex) { printError = ex.Message; }
        }
        else
        {
            try
            {
                var preview = new ConfirmReceiptWindow(order) { Owner = Window.GetWindow(this) };
                preview.ShowDialog();
                printed = preview.Printed;
            }
            catch (Exception ex) { printError = ex.Message; }
        }

        _cart.Clear();
        CustomerNameBox.Text = "";
        RenderCart();

        if (printError != null)
        {
            ShowToast($"Order #{order.Id:D5} saved · print failed: {printError}", isError: true);
        }
        else if (printed)
        {
            ShowToast($"✓ Order #{order.Id:D5} placed & printed", isError: false);
        }
        else
        {
            ShowToast($"✓ Order #{order.Id:D5} placed (not printed)", isError: false);
        }
    }

    private void ShowToast(string text, bool isError)
    {
        ToastText.Text = text;
        ToastBorder.Background = (Brush)Application.Current.Resources[isError ? "BrandDangerBrush" : "BrandSuccessBrush"];
        ToastBorder.Visibility = Visibility.Visible;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(isError ? 6 : 3) };
        t.Tick += (_, _) => { ToastBorder.Visibility = Visibility.Collapsed; t.Stop(); };
        t.Start();
    }
}
