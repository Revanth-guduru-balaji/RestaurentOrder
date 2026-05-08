using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using RestaurantOrder.Data;
using RestaurantOrder.Services;
using MenuItem = RestaurantOrder.Data.MenuItem;

namespace RestaurantOrder.Views;

public partial class InventoryPage : UserControl
{
    private List<MenuItem> _all = new();
    private readonly ObservableCollection<RowVm> _view = new();
    private string _activeCategory = "All";
    private bool _filterLow;
    private bool _filterOut;
    private bool _filterAvailable;

    public InventoryPage()
    {
        InitializeComponent();
        RowsList.ItemsSource = _view;
        Loaded += (_, _) => Refresh();
        // F2 = Add Item, F4 = focus search
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.F2)
            {
                AddItem_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.F4)
            {
                SearchBox.Focus();
                e.Handled = true;
            }
        };
    }

    // --------------------------------------------------------------------
    // Data load + filter
    // --------------------------------------------------------------------
    private void Refresh()
    {
        _all = MenuRepository.GetAll();
        UpdateKpis();
        BuildCategoryChips();
        ApplyFilter();
    }

    private void UpdateKpis()
    {
        int avail = _all.Count(i => i.IsAvailable && (!i.TracksStock || i.AvailableQty > 0));
        int low = _all.Count(i => i.TracksStock
                                  && i.AvailableQty > 0
                                  && i.AvailableQty < AppSettings.Current.LowStockThreshold);
        int outCount = _all.Count(i => i.TracksStock && i.AvailableQty <= 0);
        decimal value = _all.Where(i => i.TracksStock).Sum(i => i.AvailableQty * i.Price);

        KpiAvailableValue.Text = avail.ToString();
        KpiAvailableSub.Text = $"of {_all.Count} items";
        KpiLowValue.Text = low.ToString();
        KpiLowSub.Text = $"below threshold of {AppSettings.Current.LowStockThreshold}";
        KpiOutValue.Text = outCount.ToString();
        KpiValueValue.Text = "₹ " + value.ToString("N0", CultureInfo.InvariantCulture);

        var cats = _all.Select(i => string.IsNullOrWhiteSpace(i.Category) ? "Other" : i.Category)
                       .Distinct().Count();
        PageSubtitleText.Text = $"{_all.Count} items across {cats} categor{(cats == 1 ? "y" : "ies")}";
    }

    private void BuildCategoryChips()
    {
        var counts = _all
            .GroupBy(i => string.IsNullOrWhiteSpace(i.Category) ? "Other" : i.Category)
            .ToDictionary(g => g.Key, g => g.Count());
        var cats = new List<string> { "All" };
        cats.AddRange(counts.Keys.OrderBy(c => c));

        CategoryChips.Items.Clear();
        foreach (var c in cats)
        {
            int n = c == "All" ? _all.Count : counts.TryGetValue(c, out var v) ? v : 0;
            var chip = new ToggleButton
            {
                Style = (Style)Application.Current.Resources["FilterChip"],
                Tag = c,
                IsChecked = c == _activeCategory,
                Content = BuildChipContent(c, n),
            };
            chip.Click += CategoryChip_Click;
            CategoryChips.Items.Add(chip);
        }
    }

    private static StackPanel BuildChipContent(string label, int count)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock
        {
            Text = count.ToString(CultureInfo.InvariantCulture),
            FontSize = 10,
            Margin = new Thickness(6, 0, 0, 0),
            Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Center
        });
        return sp;
    }

    private void CategoryChip_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;
        // Enforce single-select among category chips
        foreach (var item in CategoryChips.Items.OfType<ToggleButton>())
            item.IsChecked = ReferenceEquals(item, btn);
        _activeCategory = btn.Tag as string ?? "All";
        ApplyFilter();
    }

    private void StatusChip_Click(object sender, RoutedEventArgs e)
    {
        _filterLow = LowChip.IsChecked == true;
        _filterOut = OutChip.IsChecked == true;
        SyncKpiToggles();
        ApplyFilter();
    }

    // KPI cards double as filter toggles. Available / Low / Out are mutually
    // independent — checking multiple narrows the table further (intersection).
    private void KpiToggle_Click(object sender, RoutedEventArgs e)
    {
        _filterAvailable = KpiAvailableToggle.IsChecked == true;
        _filterLow = KpiLowToggle.IsChecked == true;
        _filterOut = KpiOutToggle.IsChecked == true;
        // Keep the bottom-bar status chips in sync with the KPI toggles.
        LowChip.IsChecked = _filterLow;
        OutChip.IsChecked = _filterOut;
        ApplyFilter();
    }

    private void SyncKpiToggles()
    {
        KpiLowToggle.IsChecked = _filterLow;
        KpiOutToggle.IsChecked = _filterOut;
        KpiAvailableToggle.IsChecked = _filterAvailable;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var search = (SearchBox.Text ?? "").Trim();
        var threshold = AppSettings.Current.LowStockThreshold;

        IEnumerable<MenuItem> q = _all;
        if (_activeCategory != "All")
            q = q.Where(i => (string.IsNullOrWhiteSpace(i.Category) ? "Other" : i.Category) == _activeCategory);
        if (!string.IsNullOrEmpty(search))
            q = q.Where(i => i.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                          || (i.Category ?? "").Contains(search, StringComparison.OrdinalIgnoreCase));
        if (_filterAvailable)
            q = q.Where(i => i.IsAvailable && (!i.TracksStock || i.AvailableQty > 0));
        if (_filterLow)
            q = q.Where(i => i.TracksStock && i.AvailableQty > 0 && i.AvailableQty < threshold);
        if (_filterOut)
            q = q.Where(i => i.TracksStock && i.AvailableQty <= 0);

        _view.Clear();
        foreach (var i in q)
            _view.Add(RowVm.From(i, threshold));

        EmptyState.Visibility = _view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // --------------------------------------------------------------------
    // Action handlers
    // --------------------------------------------------------------------
    private void AddItem_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new EditItemWindow(null) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            MenuRepository.Insert(dlg.Result);
            Refresh();
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is int id)
        {
            var item = _all.FirstOrDefault(x => x.Id == id);
            if (item == null) return;
            var dlg = new EditItemWindow(item) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.Result != null)
            {
                MenuRepository.Update(dlg.Result);
                Refresh();
            }
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is int id)
        {
            var item = _all.FirstOrDefault(x => x.Id == id);
            if (item == null) return;
            var ok = MessageBox.Show($"Delete '{item.Name}'? This cannot be undone.",
                "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ok == MessageBoxResult.Yes)
            {
                MenuRepository.Delete(id);
                Refresh();
            }
        }
    }

    private void Available_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender is not CheckBox cb) return;
        if (cb.Tag is not MenuItem item) return;
        if (item.IsAvailable == (cb.IsChecked == true)) return;
        item.IsAvailable = cb.IsChecked == true;
        try
        {
            MenuRepository.Update(item);
            UpdateKpis();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Update failed: " + ex.Message, "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Refresh();
        }
    }

    private void ResetDailyStock_Click(object sender, RoutedEventArgs e)
    {
        var ok = MessageBox.Show(
            "Reset Available qty back to Estimated qty for all tracked items?",
            "Reset stock", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        foreach (var i in _all)
        {
            if (i.EstimatedAvailableQty <= 0) continue;
            i.AvailableQty = i.EstimatedAvailableQty;
            MenuRepository.Update(i);
        }
        Refresh();
    }

    private void DownloadSample_Click(object sender, RoutedEventArgs e)
    {
        var sfd = new SaveFileDialog
        {
            Filter = "Excel files|*.xlsx",
            FileName = "menu-sample.xlsx",
            Title = "Save sample template"
        };
        if (sfd.ShowDialog() == true)
        {
            try
            {
                ExcelService.WriteSampleTemplate(sfd.FileName);
                MessageBox.Show("Template saved.\nEdit the rows and use 'Import Excel' to load.",
                    "Sample saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save template: " + ex.Message,
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void ImportExcel_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog { Filter = "Excel files|*.xlsx;*.xls", Title = "Import menu" };
        if (ofd.ShowDialog() != true) return;

        try
        {
            var result = ExcelService.ImportMenu(ofd.FileName);
            var msg = $"Imported / updated {result.Imported} item(s).";
            if (result.Skipped > 0)
                msg += $"\nSkipped {result.Skipped} row(s).";
            if (result.Errors.Count > 0)
                msg += "\n\nFirst issues:\n• " + string.Join("\n• ", result.Errors.Take(5));
            MessageBox.Show(msg, "Import complete", MessageBoxButton.OK, MessageBoxImage.Information);
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Import failed: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

/// <summary>Row projection bound to the InventoryPage ItemsControl.</summary>
public class RowVm
{
    public int Id { get; set; }
    public MenuItem Source { get; set; } = new();
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string PriceText { get; set; } = "";
    public string MetaText { get; set; } = "";

    public string SwatchLetter { get; set; } = "";
    public Brush SwatchBg { get; set; } = Brushes.Transparent;
    public Brush SwatchFg { get; set; } = Brushes.Transparent;

    public bool IsAvailable { get; set; }

    public Brush NameForeground { get; set; } = Brushes.Black;
    public Brush RowBackground { get; set; } = Brushes.Transparent;
    public double RowOpacity { get; set; } = 1.0;

    public Visibility LowBadgeVisibility { get; set; } = Visibility.Collapsed;
    public Visibility OutBadgeVisibility { get; set; } = Visibility.Collapsed;

    public string AvailQtyText { get; set; } = "";
    public string EstQtyText { get; set; } = "";
    public Brush QtyForeground { get; set; } = Brushes.Black;
    public Brush FillBrush { get; set; } = Brushes.Transparent;
    public double FillOpacity { get; set; } = 1.0;
    public GridLength FillStar { get; set; } = new GridLength(0, GridUnitType.Star);
    public GridLength RemainStar { get; set; } = new GridLength(1, GridUnitType.Star);
    public Visibility StockLineVisibility { get; set; } = Visibility.Visible;
    public Visibility NotTrackedVisibility { get; set; } = Visibility.Collapsed;

    public static RowVm From(MenuItem i, int lowThreshold)
    {
        var (bg, fg, letter) = SwatchFor(i.Category);
        bool tracksStock = i.TracksStock;
        bool isOut = tracksStock && i.AvailableQty <= 0;
        bool isLow = tracksStock && i.AvailableQty > 0 && i.AvailableQty < lowThreshold;

        var vm = new RowVm
        {
            Id = i.Id,
            Source = i,
            Name = i.Name,
            Category = string.IsNullOrWhiteSpace(i.Category) ? "Other" : i.Category,
            PriceText = "₹ " + i.Price.ToString("N2", CultureInfo.InvariantCulture),
            MetaText = BuildMeta(i),
            SwatchLetter = letter,
            SwatchBg = bg,
            SwatchFg = fg,
            IsAvailable = i.IsAvailable,
            NameForeground = isOut
                ? new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69))
                : new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A)),
            RowOpacity = isOut ? 0.78 : 1.0,
            LowBadgeVisibility = isLow ? Visibility.Visible : Visibility.Collapsed,
            OutBadgeVisibility = isOut ? Visibility.Visible : Visibility.Collapsed,
        };

        if (!tracksStock)
        {
            vm.StockLineVisibility = Visibility.Collapsed;
            vm.NotTrackedVisibility = Visibility.Visible;
            vm.FillStar = new GridLength(0, GridUnitType.Star);
            vm.RemainStar = new GridLength(1, GridUnitType.Star);
            return vm;
        }

        vm.AvailQtyText = i.AvailableQty.ToString();
        vm.EstQtyText = "  / " + i.EstimatedAvailableQty.ToString();

        double ratio = i.EstimatedAvailableQty > 0
            ? Math.Min(1.0, (double)i.AvailableQty / i.EstimatedAvailableQty)
            : 0;
        if (isOut) ratio = 1.0; // fill bar full but with low opacity (red wash)
        vm.FillStar = new GridLength(Math.Max(0.0001, ratio), GridUnitType.Star);
        vm.RemainStar = new GridLength(Math.Max(0.0001, 1.0 - ratio), GridUnitType.Star);

        if (isOut)
        {
            vm.QtyForeground = new SolidColorBrush(Color.FromRgb(0x99, 0x1B, 0x1B));
            vm.FillBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            vm.FillOpacity = 0.18;
        }
        else if (isLow)
        {
            vm.QtyForeground = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
            vm.FillBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
            vm.FillOpacity = 1.0;
        }
        else
        {
            vm.QtyForeground = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));
            vm.FillBrush = new SolidColorBrush(Color.FromRgb(0x08, 0x91, 0xB2));
            vm.FillOpacity = 1.0;
        }
        return vm;
    }

    private static string BuildMeta(MenuItem i)
    {
        // Synthetic SKU and per-unit hint, mirrors the design-system mock copy.
        string sku = SkuFor(i);
        string price = i.Price.ToString("0", CultureInfo.InvariantCulture);
        return $"SKU {sku} · ₹{price} / serving";
    }

    private static string SkuFor(MenuItem i)
    {
        var letters = new string((i.Name ?? "")
            .Where(char.IsLetter)
            .Take(3)
            .ToArray()).ToUpperInvariant();
        if (letters.Length < 3) letters = letters.PadRight(3, 'X');
        var price = (int)Math.Round(i.Price);
        return $"{letters}-{price:000}";
    }

    private static (Brush bg, Brush fg, string letter) SwatchFor(string category)
    {
        string c = (category ?? "").Trim().ToLowerInvariant();
        return c switch
        {
            "breakfast" => (R("SwatchBreakfastBg"), R("SwatchBreakfastFg"), "B"),
            "beverages" => (R("SwatchBeveragesBg"), R("SwatchBeveragesFg"), "D"),
            "meals" or "lunch" or "dinner" => (R("SwatchMealsBg"), R("SwatchMealsFg"), "M"),
            "snacks" or "tiffin" => (R("SwatchSnacksBg"), R("SwatchSnacksFg"), "S"),
            "sweets" or "desserts" => (R("SwatchSweetsBg"), R("SwatchSweetsFg"), "W"),
            _ => (R("InfoBgBrush"), R("InfoFgBrush"),
                  string.IsNullOrEmpty(category) ? "?" : category[..1].ToUpperInvariant()),
        };
    }

    private static Brush R(string key) => (Brush)Application.Current.Resources[key];
}
