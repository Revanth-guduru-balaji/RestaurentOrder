using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace RestaurantOrder;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Last-resort crash logging so we always have a breadcrumb when the
        // window dies silently. Writes to %LocalAppData%\RestaurantOrder\crash.log.
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainException;

        Data.Database.Initialize();
        Data.Database.ResetDailyStockIfNeeded();
    }

    private static string CrashLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RestaurantOrder", "crash.log");

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("UI thread", e.Exception);
        try
        {
            MessageBox.Show($"Unexpected error: {e.Exception.Message}\n\nDetails written to:\n{CrashLogPath}",
                "RestaurantOrder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
        e.Handled = true; // keep the app alive; the user can keep working with other tabs
    }

    private static void OnAppDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) Log("AppDomain", ex);
    }

    private static void Log(string source, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}\n\n");
        }
        catch { }
    }
}
