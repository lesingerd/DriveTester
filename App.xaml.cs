using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace DriveTester;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launch_debug.log");

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log("App.OnStartup enter");
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.ProcessExit += (s, ev) => Log("ProcessExit event received");

        try
        {
            Log("Creating MainWindow instance...");
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            window.Activate();
            window.Focus();
            Log("MainWindow displayed successfully.");
        }
        catch (Exception ex)
        {
            Log($"Failed to show MainWindow in OnStartup: {ex}");
            MessageBox.Show($"Startup Error:\n{ex.Message}\n\n{ex.StackTrace}", "DriveTester", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        Log("App.OnStartup complete");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log($"DispatcherUnhandledException: {e.Exception}");
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nStack Trace:\n{e.Exception.StackTrace}",
            "DriveTester - Application Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    private void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log($"UnhandledException: {e.ExceptionObject}");
        if (e.ExceptionObject is Exception ex)
        {
            MessageBox.Show(
                $"Fatal application error:\n\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}",
                "DriveTester - Fatal Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log($"UnobservedTaskException: {e.Exception}");
        e.SetObserved();
    }
}
