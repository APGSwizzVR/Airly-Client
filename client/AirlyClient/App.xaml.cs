using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AirlyClient;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (UpdateService.IsUpdaterLaunch(e.Args))
        {
            UpdateService.RunUpdater(e.Args);
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            HandleStartupFailure(ex);
            Shutdown(1);
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        HandleStartupFailure(e.Exception);
        e.Handled = true;
        Current?.Shutdown(1);
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            LogException(ex);
    }

    private static void HandleStartupFailure(Exception ex)
    {
        LogException(ex);
        try
        {
            MessageBox.Show(
                "Airly Client could not start." + Environment.NewLine + Environment.NewLine +
                "A crash log was saved to:" + Environment.NewLine + GetLogPath() + Environment.NewLine + Environment.NewLine +
                "Error: " + ex.Message,
                "Airly Client — Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
        }
    }

    private static void LogException(Exception ex)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Airly");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                GetLogPath(),
                "[" + DateTimeOffset.Now.ToString("O") + "] " + ex + Environment.NewLine + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static string GetLogPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Airly",
        "airly-client-crash.log");
}
