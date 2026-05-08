using System.Windows;
using System.Windows.Controls;
using RestaurantOrder.Views;

namespace RestaurantOrder;

public partial class MainWindow : Window
{
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
        UserControl page = key switch
        {
            "Dashboard" => new DashboardPage(),
            "Order" => new OrderPage(),
            "Inventory" => new InventoryPage(),
            "History" => new OrderHistoryPage(),
            "Settings" => new SettingsPage(),
            _ => new DashboardPage()
        };
        ContentHost.Content = page;
    }
}
