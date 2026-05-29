using System.Windows;
using RestaurantOrder.Data;
using RestaurantOrder.Services;

namespace RestaurantOrder.Views;

public partial class ConfirmReceiptWindow : Window
{
    private readonly Order _order;
    private readonly bool _printKitchen;

    public bool Printed { get; private set; }

    public ConfirmReceiptWindow(Order order, bool printKitchen = false)
    {
        InitializeComponent();
        _order = order;
        _printKitchen = printKitchen;
        OrderInfoText.Text = $"Order #{order.Id:D5}  ·  {order.Items.Count} items  ·  {Money.Format(order.Total)}";
        Preview.Document = ReceiptPrinter.BuildPreview(order, 360);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = ReceiptPrinter.PrintWithDialog(_order, _printKitchen);
            if (result.BillPrinted)
            {
                Printed = true;
                if (result.KitchenFailed)
                    MessageBox.Show("The customer bill printed, but the KITCHEN copy did not. Please reprint the kitchen ticket.",
                        "Kitchen ticket failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
            }
        }
        catch (System.Exception ex)
        {
            MessageBox.Show("Printing failed: " + ex.Message, "Print error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
