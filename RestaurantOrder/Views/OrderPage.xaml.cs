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
        // F9 = Place & Print, F4 = focus search
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F9)
            {
                PlaceAndPrint_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.F4)
            {
                SearchBox.Focus();
                e.Handled = true;
            }
        };
    }

    private void OrderPage_Loaded(object sender, RoutedEventArgs e)
    {
        ReloadMenu();
        ApplyPlaceButtonLabel();
        UpdateClock();
        _clock.Start();
        RenderCart();
    }

    private void ReloadMenu()
    {
        _allItems.Clear();
        _allItems.AddRange(MenuRepository.GetAll(onlyAvailable: true));
        BuildCategoryTabs();
        RenderMenu();
    }

    private void ApplyPlaceButtonLabel()
    {
        PlaceButton.Content = AppSettings.Current.PrintAfterPlace ? "Place & Print" : "Place Order";
    }

    private void UpdateClock()
    {
        OrderTimeText.Text = DateTime.Now.ToString("ddd, dd MMM yyyy · hh:mm tt");
    }

    private void BuildCategoryTabs()
    {
        CategoryTabs.Items.Clear();

        var counts = _allItems
            .GroupBy(i => string.IsNullOrWhiteSpace(i.Category) ? "Other" : i.Category)
            .ToDictionary(g => g.Key, g => g.Count());
        var cats = new List<string> { "All" };
        cats.AddRange(counts.Keys.OrderBy(c => c));

        foreach (var c in cats)
        {
            int n = c == "All" ? _allItems.Count : counts.TryGetValue(c, out var v) ? v : 0;
            var btn = new Button
            {
                Style = (Style)Application.Current.Resources["CategoryChip"],
                Content = $"{c} · {n}",
                Tag = c,
                Margin = new Thickness(0, 0, 8, 8),
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
        // WrapPanel layout: tiles need an explicit width to wrap predictably.
        // 180dp keeps roughly 4 tiles per row at typical POS resolutions and
        // gives big finger targets on touchscreens.
        var btn = new Button
        {
            Style = (Style)Application.Current.Resources["MenuTile"],
            Height = 120,
            Width = 180,
            Margin = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Top,
            Tag = item
        };

        var settings = AppSettings.Current;
        bool soldOut = item.TracksStock && item.AvailableQty <= 0;
        bool showBadge = settings.ShowLowStockBadge
                          && item.TracksStock
                          && item.AvailableQty < settings.LowStockThreshold;
        bool inCart = _cart.TryGetValue(item.Id, out var existingLine);

        var root = new Grid();

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontWeight = FontWeights.Bold,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
            Foreground = (Brush)Application.Current.Resources["BrandTextBrush"]
        });
        stack.Children.Add(new TextBlock
        {
            Text = (string.IsNullOrWhiteSpace(item.Category) ? "" : item.Category).ToUpperInvariant(),
            FontSize = 10,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = (Brush)Application.Current.Resources["BrandSubtleTextBrush"]
        });
        stack.Children.Add(new Grid { Height = 8 });
        stack.Children.Add(new TextBlock
        {
            Text = Money.Format(item.Price),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["BrandPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Bottom
        });
        root.Children.Add(stack);

        // Selected count badge (top-left) — visible when item is already in cart
        if (inCart)
        {
            var bdg = new Border
            {
                Background = (Brush)Application.Current.Resources["BrandPrimaryBrush"],
                CornerRadius = new CornerRadius(4),
                Width = 22,
                Height = 22,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(-4, -4, 0, 0),
            };
            bdg.Child = new TextBlock
            {
                Text = existingLine!.Qty.ToString(),
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            root.Children.Add(bdg);
        }

        if (showBadge)
        {
            var badge = new Border
            {
                Background = soldOut
                    ? (Brush)Application.Current.Resources["BrandDangerBrush"]
                    : new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -4, -4, 0)
            };
            badge.Child = new TextBlock
            {
                Text = soldOut ? "Sold out" : $"{item.AvailableQty} left",
                Foreground = Brushes.White,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            };
            root.Children.Add(badge);
        }

        btn.Content = root;
        btn.IsEnabled = !soldOut;
        if (soldOut)
        {
            btn.Opacity = 0.55;
            btn.ToolTip = "Out of stock";
        }
        btn.Click += (_, _) => AddToCart(item);
        return btn;
    }

    private void AddToCart(MenuItem item)
    {
        if (_cart.TryGetValue(item.Id, out var line))
        {
            if (item.TracksStock && line.Qty + 1 > item.AvailableQty)
            {
                ShowToast($"Only {item.AvailableQty} {item.Name} available", isError: true);
                return;
            }
            line.Qty++;
        }
        else
        {
            if (item.TracksStock && item.AvailableQty <= 0)
            {
                ShowToast($"{item.Name} is out of stock", isError: true);
                return;
            }
            _cart[item.Id] = new CartLine { Item = item, Qty = 1 };
        }
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
                Text = "",
                FontFamily = (FontFamily)Application.Current.Resources["BrandIconFontFamily"],
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

        var breakdown = PriceBreakdown.Compute(total, GetDiscount());
        SubtotalText.Text = Money.Format(breakdown.Subtotal);

        var settings = AppSettings.Current;
        if (settings.TaxPercent > 0m)
        {
            TaxRow.Visibility = Visibility.Visible;
            TaxLabelText.Text = $"Tax ({settings.TaxPercent:0.##}%)";
            TaxAmountText.Text = Money.Format(breakdown.TaxAmount);
        }
        else
        {
            TaxRow.Visibility = Visibility.Collapsed;
        }

        DiscountRow.Visibility = settings.EnableDiscountField ? Visibility.Visible : Visibility.Collapsed;

        if (settings.RoundToNearestRupee && breakdown.RoundingAmount != 0m)
        {
            RoundingRow.Visibility = Visibility.Visible;
            RoundingAmountText.Text = (breakdown.RoundingAmount >= 0 ? "+ " : "− ") + Money.Format(Math.Abs(breakdown.RoundingAmount));
        }
        else
        {
            RoundingRow.Visibility = Visibility.Collapsed;
        }

        TotalText.Text = Money.Format(breakdown.Total);
    }

    private decimal GetDiscount()
    {
        if (DiscountBox == null) return 0m;
        if (decimal.TryParse(DiscountBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            || decimal.TryParse(DiscountBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out d))
            return d < 0 ? 0 : d;
        return 0m;
    }

    private void Discount_Changed(object sender, TextChangedEventArgs e)
    {
        // TextChanged fires once during XAML init (Text="0") before all named
        // elements are wired up — guard until the page is fully loaded.
        if (!IsLoaded) return;
        RenderCart();
    }

    private Border BuildCartRow(CartLine line)
    {
        var b = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(2, 10, 2, 12)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ----- LEFT: info + qty stepper -----
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        info.Children.Add(new TextBlock
        {
            Text = line.Item.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = line.Item.Name,
            Foreground = (Brush)Application.Current.Resources["BrandTextBrush"]
        });
        info.Children.Add(new TextBlock
        {
            Text = $"{Money.Format(line.Item.Price)} each",
            Foreground = (Brush)Application.Current.Resources["BrandSubtleTextBrush"],
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 6)
        });

        var qtyPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var minus = MakeQtyButton("−");
        var plus = MakeQtyButton("+");
        var qtyBox = new TextBox
        {
            Text = line.Qty.ToString(),
            Width = 44,
            Height = 28,
            Margin = new Thickness(4, 0, 4, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            BorderBrush = (Brush)Application.Current.Resources["BrandBorderBrush"]
        };
        qtyBox.PreviewTextInput += (_, ev) =>
        {
            ev.Handled = !int.TryParse(ev.Text, out int _);
        };
        qtyBox.LostFocus += (_, _) => CommitQtyEdit(line, qtyBox);
        qtyBox.KeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Enter) CommitQtyEdit(line, qtyBox);
        };
        minus.Click += (_, _) =>
        {
            line.Qty--;
            if (line.Qty <= 0) _cart.Remove(line.Item.Id);
            RenderCart();
            RenderMenu();
        };
        plus.Click += (_, _) =>
        {
            if (line.Item.TracksStock && line.Qty + 1 > line.Item.AvailableQty)
            {
                ShowToast($"Only {line.Item.AvailableQty} {line.Item.Name} available", isError: true);
                return;
            }
            line.Qty++;
            RenderCart();
            RenderMenu();
        };
        qtyPanel.Children.Add(minus);
        qtyPanel.Children.Add(qtyBox);
        qtyPanel.Children.Add(plus);
        info.Children.Add(qtyPanel);

        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        // ----- RIGHT: line total + delete icon -----
        var rightStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        rightStack.Children.Add(new TextBlock
        {
            Text = Money.Format(line.Item.Price * line.Qty),
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = (Brush)Application.Current.Resources["BrandTextBrush"]
        });

        var del = new Button
        {
            Style = (Style)Application.Current.Resources["IconDangerButton"],
            Margin = new Thickness(0, 6, 0, 0),
            Width = 28,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "Remove from order",
            Content = new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)Application.Current.Resources["BrandIconFontFamily"],
                FontSize = 12
            }
        };
        del.Click += (_, _) =>
        {
            _cart.Remove(line.Item.Id);
            RenderCart();
            RenderMenu();
        };
        rightStack.Children.Add(del);
        Grid.SetColumn(rightStack, 1);
        grid.Children.Add(rightStack);

        b.Child = grid;
        return b;
    }

    private void CommitQtyEdit(CartLine line, TextBox box)
    {
        if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0)
        {
            box.Text = line.Qty.ToString();
            return;
        }
        if (n == 0)
        {
            _cart.Remove(line.Item.Id);
            RenderCart();
            return;
        }
        if (line.Item.TracksStock && n > line.Item.AvailableQty)
        {
            ShowToast($"Only {line.Item.AvailableQty} {line.Item.Name} available", isError: true);
            n = line.Item.AvailableQty;
        }
        line.Qty = n;
        RenderCart();
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
        DiscountBox.Text = "0";
        ParcelBox.IsChecked = false;
        RenderCart();
    }

    private void PlaceAndPrint_Click(object sender, RoutedEventArgs e)
    {
        if (_cart.Count == 0)
        {
            ShowToast("Cart is empty — add items before placing the order", isError: true);
            return;
        }

        var deductions = _cart.Values
            .Select(l => (l.Item.Id, l.Qty))
            .ToList();
        if (!MenuRepository.TryDeductStock(deductions))
        {
            ShowToast("Stock changed since you started — refreshing menu", isError: true);
            ReloadMenu();
            return;
        }

        var order = new Order
        {
            CreatedAt = DateTime.Now,
            CustomerName = string.IsNullOrWhiteSpace(CustomerNameBox.Text) ? null : CustomerNameBox.Text.Trim(),
            PaymentMethod = PayCash.IsChecked == true ? "Cash" : (PayUpi.IsChecked == true ? "UPI" : "Card"),
            IsParcel = ParcelBox.IsChecked == true
        };
        decimal subtotal = 0;
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
            subtotal += item.LineTotal;
        }
        var bd = PriceBreakdown.Compute(subtotal, GetDiscount());
        order.Subtotal = bd.Subtotal;
        order.TaxAmount = bd.TaxAmount;
        order.DiscountAmount = bd.DiscountAmount;
        order.RoundingAmount = bd.RoundingAmount;
        order.Total = bd.Total;

        OrderRepository.Create(order);

        bool printed = false;
        string? printError = null;
        var settings = AppSettings.Current;
        if (settings.PrintAfterPlace)
        {
            if (settings.AutoPrint)
            {
                // First print (the only one that should ever produce a kitchen
                // ticket): customer bill + kitchen copy.
                try { printed = ReceiptPrinter.PrintQuiet(order, printKitchen: true); }
                catch (Exception ex) { printError = ex.Message; }
            }
            else
            {
                try
                {
                    var preview = new ConfirmReceiptWindow(order, printKitchen: true)
                    {
                        Owner = Window.GetWindow(this)
                    };
                    preview.ShowDialog();
                    printed = preview.Printed;
                }
                catch (Exception ex) { printError = ex.Message; }
            }
        }

        _cart.Clear();
        CustomerNameBox.Text = "";
        DiscountBox.Text = "0";
        ParcelBox.IsChecked = false; // reset for next order
        ReloadMenu();
        RenderCart();

        if (printError != null)
            ShowToast($"Order #{order.Id:D5} saved · print failed: {printError}", isError: true);
        else if (printed)
            ShowToast($"Order #{order.Id:D5} placed & printed", isError: false);
        else if (settings.PrintAfterPlace)
            ShowToast($"Order #{order.Id:D5} placed (not printed)", isError: false);
        else
            ShowToast($"Order #{order.Id:D5} placed", isError: false);
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
