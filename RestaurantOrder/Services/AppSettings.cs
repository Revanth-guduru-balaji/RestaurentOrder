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
