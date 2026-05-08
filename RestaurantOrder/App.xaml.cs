using System.Windows;

namespace RestaurantOrder;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Data.Database.Initialize();
        Data.Database.ResetDailyStockIfNeeded();
    }
}
