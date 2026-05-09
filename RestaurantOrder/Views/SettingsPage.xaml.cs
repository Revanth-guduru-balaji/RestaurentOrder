using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RestaurantOrder.Data;
using RestaurantOrder.Services;

namespace RestaurantOrder.Views;

public partial class SettingsPage : UserControl
{
    private static readonly Regex IntRegex = new(@"^[0-9]+$", RegexOptions.Compiled);
    private static readonly Regex DecimalRegex = new(@"^[0-9]*\.?[0-9]*$", RegexOptions.Compiled);
    private bool _suppressDirty;
    private readonly DispatcherTimer _saveTimer;

    public SettingsPage()
    {
        InitializeComponent();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); CommitToDisk(); };
        Loaded += (_, _) => LoadFromSettings();
        Unloaded += (_, _) => { if (_saveTimer.IsEnabled) { _saveTimer.Stop(); CommitToDisk(); } };
    }

    private void LoadFromSettings()
    {
        _suppressDirty = true;

        var s = AppSettings.Current;
        ShopNameBox.Text = s.ShopName;
        ShopLine2Box.Text = s.ShopLine2;
        ShopAddressBox.Text = s.ShopAddress;
        AutoPrintBox.IsChecked = s.AutoPrint;
        CompactBox.IsChecked = s.CompactReceipt;
        PrintAfterPlaceBox.IsChecked = s.PrintAfterPlace;
        RoundToRupeeBox.IsChecked = s.RoundToNearestRupee;
        EnableDiscountBox.IsChecked = s.EnableDiscountField;
        TaxPercentBox.Text = s.TaxPercent.ToString("0.##", CultureInfo.InvariantCulture);
        ShowLowStockBox.IsChecked = s.ShowLowStockBadge;
        LowStockThresholdBox.Text = s.LowStockThreshold.ToString(CultureInfo.InvariantCulture);

        var printers = ReceiptPrinter.InstalledPrinters();
        PrinterCombo.ItemsSource = printers;
        PrinterHint.Text = "";
        if (printers.Length == 0)
        {
            PrinterHint.Text = "No printers detected on this PC.";
        }
        else if (!string.IsNullOrEmpty(s.DefaultPrinterName)
                 && printers.Contains(s.DefaultPrinterName, StringComparer.OrdinalIgnoreCase))
        {
            PrinterCombo.SelectedItem = printers.First(
                p => string.Equals(p, s.DefaultPrinterName, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            PrinterHint.Text = "Detected from Windows · 80mm thermal recommended";
        }

        _suppressDirty = false;
        RefreshPreview();
    }

    private void CommitToDisk()
    {
        var s = AppSettings.Current;
        s.ShopName = (ShopNameBox.Text ?? "").Trim();
        s.ShopLine2 = (ShopLine2Box.Text ?? "").Trim();
        s.ShopAddress = (ShopAddressBox.Text ?? "").Trim();
        s.DefaultPrinterName = PrinterCombo.SelectedItem as string ?? "";
        s.AutoPrint = AutoPrintBox.IsChecked == true;
        s.CompactReceipt = CompactBox.IsChecked == true;
        s.PrintAfterPlace = PrintAfterPlaceBox.IsChecked == true;
        s.RoundToNearestRupee = RoundToRupeeBox.IsChecked == true;
        s.EnableDiscountField = EnableDiscountBox.IsChecked == true;
        if (decimal.TryParse(TaxPercentBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var taxPct) && taxPct >= 0)
            s.TaxPercent = taxPct;
        s.ShowLowStockBadge = ShowLowStockBox.IsChecked == true;
        if (int.TryParse(LowStockThresholdBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var th) && th >= 0)
            s.LowStockThreshold = th;

        try { s.Save(); } catch { /* ignore — will retry on next change */ }
    }

    private void FormField_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressDirty) return;
        _saveTimer.Stop();
        _saveTimer.Start();
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        var name = (ShopNameBox.Text ?? "").Trim();
        RcShopName.Text = string.IsNullOrEmpty(name) ? "ARYA VYSYA SSV" : name.ToUpperInvariant();

        var line2 = (ShopLine2Box.Text ?? "").Trim();
        RcLine2.Visibility = string.IsNullOrEmpty(line2) ? Visibility.Collapsed : Visibility.Visible;
        RcLine2.Text = line2;

        var addr = (ShopAddressBox.Text ?? "").Trim();
        RcAddress.Visibility = string.IsNullOrEmpty(addr) ? Visibility.Collapsed : Visibility.Visible;
        RcAddress.Text = addr;
    }

    private void SectionNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string targetName) return;
        if (FindName(targetName) is FrameworkElement el)
            el.BringIntoView();
    }

    private void IntOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !IntRegex.IsMatch(e.Text);

    private void DecimalOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !DecimalRegex.IsMatch((sender is TextBox tb ? tb.Text : "") + e.Text);

    private void TestPrint_Click(object sender, RoutedEventArgs e)
    {
        // Snapshot current form values to settings first so the test print uses
        // exactly what the user is looking at.
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); CommitToDisk(); }

        try
        {
            var sample = new Order
            {
                Id = 0,
                CreatedAt = DateTime.Now,
                CustomerName = "Test",
                PaymentMethod = "Cash",
                Total = 90m,
                Items =
                {
                    new OrderItem { MenuItemName = "Idly Plate (3 pcs)", Quantity = 1, UnitPrice = 25m },
                    new OrderItem { MenuItemName = "Plain Dosa", Quantity = 1, UnitPrice = 40m },
                    new OrderItem { MenuItemName = "Filter Coffee", Quantity = 1, UnitPrice = 25m },
                }
            };
            var sent = ReceiptPrinter.PrintQuiet(sample);
            var s = AppSettings.Current;
            PrinterHint.Text = sent
                ? "Test sent to " + (string.IsNullOrEmpty(s.DefaultPrinterName) ? "selected printer" : s.DefaultPrinterName)
                : "Test print cancelled.";
        }
        catch (Exception ex)
        {
            MessageBox.Show("Test print failed: " + ex.Message, "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
