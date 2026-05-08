using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using RestaurantOrder.Data;
using MenuItem = RestaurantOrder.Data.MenuItem;

namespace RestaurantOrder.Views;

public partial class EditItemWindow : Window
{
    private static readonly Regex IntRegex = new(@"^[0-9]+$", RegexOptions.Compiled);
    public MenuItem? Result { get; private set; }

    public EditItemWindow(MenuItem? existing)
    {
        InitializeComponent();

        // Populate category dropdown from existing distinct categories so the
        // user picks from what's already in use instead of typing free-form.
        var existingCategories = MenuRepository.GetAll()
            .Select(i => (i.Category ?? "").Trim())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
        CategoryBox.ItemsSource = existingCategories;

        if (existing != null)
        {
            Title = "Edit Menu Item";
            NameBox.Text = existing.Name;
            CategoryBox.Text = existing.Category;
            PriceBox.Text = existing.Price.ToString("0.00", CultureInfo.InvariantCulture);
            EstQtyBox.Text = existing.EstimatedAvailableQty.ToString(CultureInfo.InvariantCulture);
            AvailQtyBox.Text = existing.AvailableQty.ToString(CultureInfo.InvariantCulture);
            AvailableBox.IsChecked = existing.IsAvailable;
            Result = existing;
        }
        else
        {
            EstQtyBox.Text = "0";
            AvailQtyBox.Text = "0";
        }
    }

    private void IntOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !IntRegex.IsMatch(e.Text);

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

        // Empty / blank qty fields are treated as 0 (untracked). Only error
        // on garbage input or a negative number.
        int estQty = 0;
        if (!string.IsNullOrWhiteSpace(EstQtyBox.Text))
        {
            if (!int.TryParse(EstQtyBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out estQty) || estQty < 0)
            {
                MessageBox.Show("Estimated qty must be a non-negative whole number."); return;
            }
        }
        int availQty = 0;
        if (!string.IsNullOrWhiteSpace(AvailQtyBox.Text))
        {
            if (!int.TryParse(AvailQtyBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out availQty) || availQty < 0)
            {
                MessageBox.Show("Available qty must be a non-negative whole number."); return;
            }
        }

        Result ??= new MenuItem();
        Result.Name = name;
        Result.Category = (CategoryBox.Text ?? string.Empty).Trim();
        Result.Price = price;
        Result.EstimatedAvailableQty = estQty;
        Result.AvailableQty = estQty == 0 ? 0 : availQty;
        Result.IsAvailable = AvailableBox.IsChecked == true;
        DialogResult = true;
        Close();
    }
}
