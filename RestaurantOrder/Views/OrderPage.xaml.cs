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

    // ----- Multi-draft state ---------------------------------------------
    // Drafts are persisted: a held cart survives tab switches, app restart,
    // even an unclean shutdown. _drafts is the in-memory mirror of the
    // Drafts table (loaded once on first Loaded). The active draft's items
    // live in _cart for fast UI rendering; we sync _cart -> active Draft on
    // every mutation, then debounce-save to disk.
    private readonly List<Draft> _drafts = new();
    private Draft? _activeDraft;
    private bool _draftsInitialized;
    private bool _suppressDraftSave;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private class CartLine
    {
        public MenuItem Item { get; init; } = new();
        public int Qty { get; set; }
    }

    public OrderPage()
    {
        InitializeComponent();
        Loaded += OrderPage_Loaded;
        Unloaded += OrderPage_Unloaded;
        _clock.Tick += (_, _) => UpdateClock();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); FlushActiveDraft(); };
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

        // Load drafts the first time the page is shown. On subsequent shows
        // (cashier flips back from Inventory etc) we keep the in-memory state
        // so they land on the same draft.
        if (!_draftsInitialized)
        {
            LoadDraftsFromDisk();
            _draftsInitialized = true;
        }
        RebuildDraftTabs();
        RenderActiveDraft();
    }

    private void OrderPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _clock.Stop();
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); FlushActiveDraft(); }
    }

    // --------------------------------------------------------------------
    // Draft load / switch / new / close
    // --------------------------------------------------------------------
    private void LoadDraftsFromDisk()
    {
        _drafts.Clear();
        try { _drafts.AddRange(DraftRepository.GetAll()); }
        catch { /* DB unavailable: start with no drafts */ }

        if (_drafts.Count == 0)
        {
            // Always have at least one draft visible.
            _drafts.Add(new Draft());
        }
        _activeDraft = _drafts[0];
    }

    private void SwitchToDraft(Draft draft)
    {
        if (ReferenceEquals(_activeDraft, draft)) return;
        // Flush current cart to active draft, then to disk.
        SyncCartToActiveDraft();
        if (_saveTimer.IsEnabled) _saveTimer.Stop();
        FlushActiveDraft();

        _activeDraft = draft;
        RebuildDraftTabs();
        RenderActiveDraft();
    }

    private void NewDraft_Click(object? sender, RoutedEventArgs e)
    {
        SyncCartToActiveDraft();
        if (_saveTimer.IsEnabled) _saveTimer.Stop();
        FlushActiveDraft();

        var draft = new Draft();
        _drafts.Add(draft);
        _activeDraft = draft;
        RebuildDraftTabs();
        RenderActiveDraft();
    }

    private void CloseDraft_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Draft draft) return;

        bool hasItems = draft.Items.Count > 0
            || (ReferenceEquals(draft, _activeDraft) && _cart.Count > 0);
        if (hasItems)
        {
            var ok = MessageBox.Show("Close this draft order? Items in it will be lost.",
                "Close draft", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ok != MessageBoxResult.Yes) return;
        }

        DeleteDraft(draft);
    }

    private void DeleteDraft(Draft draft)
    {
        if (draft.Id > 0)
        {
            try { DraftRepository.Delete(draft.Id); } catch { }
        }
        bool wasActive = ReferenceEquals(draft, _activeDraft);
        _drafts.Remove(draft);

        if (_drafts.Count == 0)
        {
            _drafts.Add(new Draft());
            _activeDraft = _drafts[0];
        }
        else if (wasActive)
        {
            _activeDraft = _drafts[0];
        }

        RebuildDraftTabs();
        if (wasActive) RenderActiveDraft();
    }

    /// Pulls _cart + form state INTO the active Draft object.
    /// Called before switching drafts and before persisting.
    private void SyncCartToActiveDraft()
    {
        if (_activeDraft == null) return;
        _activeDraft.Items.Clear();
        foreach (var line in _cart.Values)
        {
            _activeDraft.Items.Add(new DraftItem
            {
                MenuItemId = line.Item.Id,
                MenuItemName = line.Item.Name,
                UnitPrice = line.Item.Price,
                Quantity = line.Qty
            });
        }
        _activeDraft.CustomerName = string.IsNullOrWhiteSpace(CustomerNameBox.Text) ? null : CustomerNameBox.Text.Trim();
        _activeDraft.PaymentMethod = PayCash.IsChecked == true ? "Cash" : (PayUpi.IsChecked == true ? "UPI" : "Card");
        _activeDraft.IsParcel = ParcelBox.IsChecked == true;
        _activeDraft.DiscountAmount = GetDiscount();
        _activeDraft.UpdatedAt = DateTime.Now;
    }

    /// Mirrors the active Draft INTO _cart + form fields, then re-renders.
    private void RenderActiveDraft()
    {
        _suppressDraftSave = true;
        try
        {
            _cart.Clear();
            if (_activeDraft != null)
            {
                foreach (var it in _activeDraft.Items)
                {
                    var live = _allItems.FirstOrDefault(x => x.Id == it.MenuItemId);
                    // If the item is no longer in the live menu (deleted /
                    // marked unavailable), keep the line with its captured
                    // price + qty. We can't know stock-tracking state here, so
                    // we treat it as untracked — Place & Print will still
                    // validate against the real DB and surface a 'Stock
                    // changed' message if the item won't deduct.
                    var menuItem = live ?? new MenuItem
                    {
                        Id = it.MenuItemId,
                        Name = it.MenuItemName,
                        Price = it.UnitPrice
                    };
                    _cart[it.MenuItemId] = new CartLine { Item = menuItem, Qty = it.Quantity };
                }
                CustomerNameBox.Text = _activeDraft.CustomerName ?? "";
                ParcelBox.IsChecked = _activeDraft.IsParcel;
                DiscountBox.Text = _activeDraft.DiscountAmount.ToString("0.##", CultureInfo.InvariantCulture);
                if (string.Equals(_activeDraft.PaymentMethod, "UPI", StringComparison.OrdinalIgnoreCase))
                    PayUpi.IsChecked = true;
                else if (string.Equals(_activeDraft.PaymentMethod, "Card", StringComparison.OrdinalIgnoreCase))
                    PayCard.IsChecked = true;
                else
                    PayCash.IsChecked = true;
            }
            else
            {
                CustomerNameBox.Text = "";
                ParcelBox.IsChecked = false;
                DiscountBox.Text = "0";
                PayCash.IsChecked = true;
            }
        }
        finally { _suppressDraftSave = false; }
        RenderMenu();
        RenderCart();
        RebuildDraftTabs(); // tab counts may have changed after _cart was rewritten
    }

    /// Schedule a debounced persist of the active draft. Eagerly mirrors
    /// the current UI into the in-memory Draft so tab labels and counts
    /// reflect the live state immediately (the 500ms timer just handles
    /// the disk write). If the OS kills the process before the timer
    /// fires, in-memory state stays consistent until Unloaded flushes it.
    private void MarkDraftDirty()
    {
        if (_suppressDraftSave) return;
        SyncCartToActiveDraft();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushActiveDraft()
    {
        if (_activeDraft == null) return;
        SyncCartToActiveDraft();
        // Don't persist a brand-new untouched draft (no items, no name, no flags).
        if (_activeDraft.Id == 0
            && _activeDraft.Items.Count == 0
            && string.IsNullOrWhiteSpace(_activeDraft.CustomerName)
            && !_activeDraft.IsParcel
            && _activeDraft.DiscountAmount == 0m)
        {
            return;
        }
        try
        {
            if (_activeDraft.Id == 0) DraftRepository.Insert(_activeDraft);
            else DraftRepository.Update(_activeDraft);
            RebuildDraftTabs(); // refresh the tab label (Draft # depends on order)
        }
        catch { /* swallow — will retry on next mutation */ }
    }

    private void RebuildDraftTabs()
    {
        DraftTabsPanel.Children.Clear();
        for (int i = 0; i < _drafts.Count; i++)
        {
            var d = _drafts[i];
            DraftTabsPanel.Children.Add(BuildDraftTab(d, i + 1));
        }

        // + New button at the end
        var newBtn = new Button
        {
            Style = (Style)Application.Current.Resources["GhostButton"],
            Content = "+ New",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        newBtn.Click += NewDraft_Click;
        DraftTabsPanel.Children.Add(newBtn);
    }

    private Border BuildDraftTab(Draft draft, int displayNumber)
    {
        bool isActive = ReferenceEquals(draft, _activeDraft);
        int itemCount = ReferenceEquals(draft, _activeDraft)
            ? _cart.Values.Sum(l => l.Qty)
            : draft.Items.Sum(i => i.Quantity);

        var tab = new Border
        {
            Background = isActive
                ? (Brush)Application.Current.Resources["BrandPrimaryBrush"]
                : Brushes.White,
            BorderBrush = (Brush)Application.Current.Resources["BrandBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            MinHeight = 36,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(14, 0, 8, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        tab.MouseLeftButtonUp += (_, _) => SwitchToDraft(draft);

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var title = string.IsNullOrWhiteSpace(draft.CustomerName)
            ? $"Draft {displayNumber}"
            : $"Draft {displayNumber} · {draft.CustomerName}";
        if (draft.IsParcel) title += " · 📦";

        row.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = isActive ? Brushes.White : (Brush)Application.Current.Resources["BrandTextBrush"],
            Margin = new Thickness(0, 0, 8, 0)
        });

        // Item count chip (skip when zero so empty drafts look quiet)
        if (itemCount > 0)
        {
            var countBadge = new Border
            {
                Background = isActive
                    ? new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))
                    : (Brush)Application.Current.Resources["BrandSoftSurfaceBrush"],
                CornerRadius = new CornerRadius(10),
                MinHeight = 20,
                Padding = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            countBadge.Child = new TextBlock
            {
                Text = itemCount.ToString(),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = isActive ? Brushes.White : (Brush)Application.Current.Resources["BrandSubtleTextDarkBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            row.Children.Add(countBadge);
        }

        var closeBtn = new Button
        {
            Content = "✕",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = isActive ? Brushes.White : (Brush)Application.Current.Resources["BrandSubtleTextBrush"],
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = draft,
            ToolTip = "Close this draft"
        };
        closeBtn.Click += CloseDraft_Click;
        row.Children.Add(closeBtn);

        tab.Child = row;
        return tab;
    }

    // --------------------------------------------------------------------
    // Menu rendering (unchanged)
    // --------------------------------------------------------------------
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
            // Counts beside category names are visual noise during a busy
            // service — drop them, just show the category label.
            var btn = new Button
            {
                Style = (Style)Application.Current.Resources["CategoryChip"],
                Content = c,
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

        // Selected count badge — top-RIGHT corner so it never covers the
        // item name. Round badge: width = height so the radius is a perfect
        // circle. When a stock badge would also show, the count badge wins
        // the top-right slot and the stock badge is suppressed (cashier
        // already added the item; remaining stock is in the cart's qty box).
        if (inCart)
        {
            var bdg = new Border
            {
                Background = (Brush)Application.Current.Resources["BrandPrimaryBrush"],
                CornerRadius = new CornerRadius(11),
                Width = 22,
                Height = 22,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -4, -4, 0),
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
        else if (showBadge)
        {
            // Stock badge: pill shape (radius = height/2 = 11 for 22px height).
            var badge = new Border
            {
                Background = soldOut
                    ? (Brush)Application.Current.Resources["BrandDangerBrush"]
                    : new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(10, 0, 10, 0),
                MinHeight = 22,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -4, -4, 0)
            };
            badge.Child = new TextBlock
            {
                Text = soldOut ? "Sold out" : $"{item.AvailableQty} left",
                Foreground = Brushes.White,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
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
        RenderMenu();
        MarkDraftDirty();
        RebuildDraftTabs();
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
                Text = "",
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
        MarkDraftDirty();
    }

    private void CustomerName_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        MarkDraftDirty();
        RebuildDraftTabs(); // tab label includes customer name
    }

    private void Parcel_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        MarkDraftDirty();
        RebuildDraftTabs();
    }

    private void Payment_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        MarkDraftDirty();
    }

    private Border BuildCartRow(CartLine line)
    {
        // Compact 2-column row: name+price on left, qty stepper + line total
        // on right, all on one visual line. Bulk orders (20-50 items) need
        // density so the cashier can scan the cart without endless scrolling.
        var b = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(2, 6, 2, 6)
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
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = line.Item.Name,
            Foreground = (Brush)Application.Current.Resources["BrandTextBrush"]
        });
        info.Children.Add(new TextBlock
        {
            Text = $"{Money.Format(line.Item.Price)} each",
            Foreground = (Brush)Application.Current.Resources["BrandSubtleTextBrush"],
            FontSize = 11,
            Margin = new Thickness(0, 1, 0, 4)
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
            MarkDraftDirty();
            RebuildDraftTabs();
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
            MarkDraftDirty();
            RebuildDraftTabs();
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
            // Plain Unicode ✕ renders cleanly everywhere; the Segoe MDL2
            // glyph fell back to a tofu box on some POS hardware.
            Content = new TextBlock
            {
                Text = "✕",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
        del.Click += (_, _) =>
        {
            _cart.Remove(line.Item.Id);
            RenderCart();
            RenderMenu();
            MarkDraftDirty();
            RebuildDraftTabs();
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
            MarkDraftDirty();
            RebuildDraftTabs();
            return;
        }
        if (line.Item.TracksStock && n > line.Item.AvailableQty)
        {
            ShowToast($"Only {line.Item.AvailableQty} {line.Item.Name} available", isError: true);
            n = line.Item.AvailableQty;
        }
        line.Qty = n;
        RenderCart();
        MarkDraftDirty();
        RebuildDraftTabs();
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
        MarkDraftDirty();
        RebuildDraftTabs();
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

        // Order is committed — drop the held draft so it doesn't reappear on
        // the next launch as an in-progress cart.
        if (_activeDraft != null)
        {
            var placed = _activeDraft;
            DeleteDraft(placed); // also creates an empty fallback draft
        }
        ReloadMenu();
        RenderActiveDraft();

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
