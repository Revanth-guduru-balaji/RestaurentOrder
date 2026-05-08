using System;
using System.IO;
using System.Text.Json;

namespace RestaurantOrder.Services;

public class AppSettings
{
    public string ShopName { get; set; } = "Araya Vysya SSV";
    public string ShopLine2 { get; set; } = "Restaurant";
    public string ShopAddress { get; set; } = "";
    public string DefaultPrinterName { get; set; } = "";
    public bool CompactReceipt { get; set; } = true;
    public bool AutoPrint { get; set; } = true;

    // Whether placing an order should also print a receipt.
    // false = "Place Order" only, true = "Place & Print".
    public bool PrintAfterPlace { get; set; } = true;

    // Show available qty in Take Order item card when below threshold.
    public bool ShowLowStockBadge { get; set; } = true;
    public int LowStockThreshold { get; set; } = 10;

    // ---- Pricing breakdown ----
    // Tax applied to subtotal (e.g. 5 = 5% GST). 0 disables the line.
    public decimal TaxPercent { get; set; } = 0m;
    // Auto round Total to nearest rupee; the rounding-off line shows the delta.
    public bool RoundToNearestRupee { get; set; } = true;
    // Show the discount input on the Take Order cart.
    public bool EnableDiscountField { get; set; } = true;

    private static readonly object _lock = new();
    private static AppSettings? _current;

    public static AppSettings Current
    {
        get
        {
            lock (_lock)
            {
                _current ??= Load();
                return _current;
            }
        }
    }

    public static string SettingsPath
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RestaurantOrder");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null) return s;
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
