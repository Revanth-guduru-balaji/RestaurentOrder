using System.Globalization;
using System.Windows;
using RestaurantOrder.Data;
using MenuItem = RestaurantOrder.Data.MenuItem;

namespace RestaurantOrder.Views;

public partial class EditItemWindow : Window
{
    public MenuItem? Result { get; private set; }

    public EditItemWindow(MenuItem? existing)
    {
        InitializeComponent();
        if (existing != null)
        {
            Title = "Edit Menu Item";
            NameBox.Text = existing.Name;
            CategoryBox.Text = existing.Category;
            PriceBox.Text = existing.Price.ToString("0.00", CultureInfo.InvariantCulture);
            AvailableBox.IsChecked = existing.IsAvailable;
            Result = existing;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("Name is required."); return;
        }
        if (!decimal.TryParse(PriceBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
            && !decimal.TryParse(PriceBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out price))
        {
            MessageBox.Show("Enter a valid price."); return;
        }
        if (price < 0) { MessageBox.Show("Price cannot be negative."); return; }

        Result ??= new MenuItem();
        Result.Name = name;
        Result.Category = (CategoryBox.Text ?? "").Trim();
        Result.Price = price;
        Result.IsAvailable = AvailableBox.IsChecked == true;
        DialogResult = true;
        Close();
    }
}
