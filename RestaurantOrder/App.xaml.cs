using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace RestaurantOrder;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private DispatcherTimer? _dailyResetTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance guard: a second copy writing the same SQLite file would
        // race the first (double-decrement / double-print). If one is already
        // running, tell the user and bow out.
        _singleInstance = new Mutex(true, @"Local\RestaurantOrder.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("RestaurantOrder is already running on this computer.",
                "RestaurantOrder", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Last-resort crash logging so we always have a breadcrumb when the
        // window dies silently. Writes to %LocalAppData%\RestaurantOrder\crash.log.
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainException;

        Data.Database.Initialize();
        Data.Database.ResetDailyStockIfNeeded();

        if (!string.IsNullOrEmpty(Data.Database.StartupWarning))
        {
            try
            {
                MessageBox.Show(Data.Database.StartupWarning, "RestaurantOrder",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
        }

        // Re-run the daily stock reset periodically so a terminal left running
        // across midnight still refreshes stock for the new day. The query is
        // cheap and gated — a no-op unless the calendar date actually changed.
        _dailyResetTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _dailyResetTimer.Tick += (_, _) =>
        {
            try { Data.Database.ResetDailyStockIfNeeded(); } catch { }
        };
        _dailyResetTimer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _dailyResetTimer?.Stop();
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static string CrashLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RestaurantOrder", "crash.log");

    private static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";

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

    /// Append a breadcrumb to crash.log. Public so non-fatal but important
    /// failures (e.g. a kitchen ticket that wouldn't print) can be traced.
    public static void Log(string source, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{AppVersion} {source}: {ex}\n\n");
        }
        catch { }
    }
}
