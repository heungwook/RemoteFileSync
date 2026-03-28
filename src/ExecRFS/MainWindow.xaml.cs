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
            sp.GetService<ProfileService>()?.AutoSave();
            var procs = sp.GetService<SyncProcesses>();
            procs?.Server.Dispose();
            procs?.Client.Dispose();
        };
    }
}
