using System;
using System.Linq;
using System.Windows;
using RestaurantOrder.Data;
using RestaurantOrder.Services;

namespace RestaurantOrder.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        var s = AppSettings.Current;
        ShopNameBox.Text = s.ShopName;
        ShopLine2Box.Text = s.ShopLine2;
        ShopAddressBox.Text = s.ShopAddress;
        AutoPrintBox.IsChecked = s.AutoPrint;
        CompactBox.IsChecked = s.CompactReceipt;

        var printers = ReceiptPrinter.InstalledPrinters();
        PrinterCombo.ItemsSource = printers;
        if (printers.Length == 0)
        {
            PrinterHint.Text = "No printers detected on this PC.";
        }
        else
        {
            if (!string.IsNullOrEmpty(s.DefaultPrinterName) && printers.Contains(s.DefaultPrinterName, StringComparer.OrdinalIgnoreCase))
                PrinterCombo.SelectedItem = printers.First(p => string.Equals(p, s.DefaultPrinterName, StringComparison.OrdinalIgnoreCase));
            else
                PrinterHint.Text = "Pick a printer to print receipts without a prompt every time.";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = AppSettings.Current;
        s.ShopName = (ShopNameBox.Text ?? "").Trim();
        s.ShopLine2 = (ShopLine2Box.Text ?? "").Trim();
        s.ShopAddress = (ShopAddressBox.Text ?? "").Trim();
        s.DefaultPrinterName = PrinterCombo.SelectedItem as string ?? "";
        s.AutoPrint = AutoPrintBox.IsChecked == true;
        s.CompactReceipt = CompactBox.IsChecked == true;
        try { s.Save(); }
        catch (Exception ex)
        {
            MessageBox.Show("Could not save settings: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        DialogResult = true;
        Close();
    }

    private void TestPrint_Click(object sender, RoutedEventArgs e)
    {
        var s = AppSettings.Current;
        var savedPrinter = s.DefaultPrinterName;
        var savedName = s.ShopName;
        var savedLine = s.ShopLine2;
        var savedAddr = s.ShopAddress;
        var savedCompact = s.CompactReceipt;
        try
        {
            s.ShopName = (ShopNameBox.Text ?? "").Trim();
            s.ShopLine2 = (ShopLine2Box.Text ?? "").Trim();
            s.ShopAddress = (ShopAddressBox.Text ?? "").Trim();
            s.DefaultPrinterName = PrinterCombo.SelectedItem as string ?? "";
            s.CompactReceipt = CompactBox.IsChecked == true;

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
            PrinterHint.Text = sent
                ? "Test sent to " + (string.IsNullOrEmpty(s.DefaultPrinterName) ? "selected printer" : s.DefaultPrinterName)
                : "Test print cancelled.";
        }
        catch (Exception ex)
        {
            MessageBox.Show("Test print failed: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            s.ShopName = savedName;
            s.ShopLine2 = savedLine;
            s.ShopAddress = savedAddr;
            s.DefaultPrinterName = savedPrinter;
            s.CompactReceipt = savedCompact;
        }
    }
}
