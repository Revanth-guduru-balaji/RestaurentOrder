using System.Windows;
using RestaurantOrder.Data;
using RestaurantOrder.Services;

namespace RestaurantOrder.Views;

public partial class ConfirmReceiptWindow : Window
{
    private readonly Order _order;

    public bool Printed { get; private set; }

    public ConfirmReceiptWindow(Order order)
    {
        InitializeComponent();
        _order = order;
        OrderInfoText.Text = $"Order #{order.Id:D5}  ·  {order.Items.Count} items  ·  {Money.Format(order.Total)}";
        Preview.Document = ReceiptPrinter.BuildPreview(order, 360);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ReceiptPrinter.PrintWithDialog(_order))
            {
                Printed = true;
                Close();
            }
        }
        catch (System.Exception ex)
        {
            MessageBox.Show("Printing failed: " + ex.Message, "Print error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
