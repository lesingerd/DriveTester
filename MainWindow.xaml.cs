using System.Collections.Specialized;
using System.Windows;
using DriveTester.ViewModels;

namespace DriveTester;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        App.Log("MainWindow.ctor enter");
        try
        {
            InitializeComponent();
            App.Log("MainWindow.InitializeComponent success");

            _vm = new MainViewModel();
            DataContext = _vm;
            App.Log("MainWindow.DataContext set");

            // Auto-scroll log listbox
            ((INotifyCollectionChanged)_vm.Logs).CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add && AutoScrollCheckbox.IsChecked == true)
                {
                    if (LogListBox.Items.Count > 0)
                    {
                        LogListBox.ScrollIntoView(LogListBox.Items[^1]);
                    }
                }
            };

            // Redraw speed graph on telemetry update
            _vm.SpeedHistoryChanged += () =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    LiveSpeedGraph.SpeedPoints = _vm.SpeedHistory;
                    LiveSpeedGraph.Redraw();
                });
            };

            Closing += (s, e) => App.Log($"MainWindow Closing event: Cancel={e.Cancel}");
            Closed += (s, e) => App.Log("MainWindow Closed event");
            Loaded += (s, e) => App.Log("MainWindow Loaded event");

            App.Log("MainWindow.ctor complete");
        }
        catch (Exception ex)
        {
            App.Log($"Exception in MainWindow.ctor: {ex}");
            throw;
        }
    }
}