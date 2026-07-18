using System.Windows;
using ExecRFS.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ExecRFS;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddSingleton<ProfileService>();
        services.AddSingleton<LogAggregator>();
        services.AddSingleton(new SyncProcesses(
            new ProcessManager("server"),
            new ProcessManager("client")));

        var sp = services.BuildServiceProvider();
        blazorWebView.Services = sp;

        Closing += (_, _) =>
        {
            var procs = sp.GetService<SyncProcesses>();
            try
            {
                sp.GetService<ProfileService>()?.AutoSave();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AutoSave failed on close: {ex}");
            }
            finally
            {
                // Must run even if AutoSave threw, or both CLI children are orphaned.
                procs?.Server.Dispose();
                procs?.Client.Dispose();
            }
        };
    }
}
