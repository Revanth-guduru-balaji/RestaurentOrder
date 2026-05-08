using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using RestaurantOrder.Views;

namespace RestaurantOrder;

public partial class MainWindow : Window
{
    // Pages are cached for the lifetime of the window. The Take Order page in
    // particular owns in-progress drafts that must survive navigating away to
    // Inventory or the dashboard and back.
    private readonly Dictionary<string, UserControl> _pages = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Navigate("Dashboard");
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
            Navigate(tag);
    }

    private void Navigate(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "Dashboard" => new DashboardPage(),
                "Order" => new OrderPage(),
                "Inventory" => new InventoryPage(),
                "History" => new OrderHistoryPage(),
                "Settings" => new SettingsPage(),
                _ => new DashboardPage()
            };
            _pages[key] = page;
        }
        ContentHost.Content = page;
    }
}
