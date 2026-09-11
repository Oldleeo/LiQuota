using System.Windows;
using System.IO;

namespace CodexQuotaWidget;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = "Local\\CodexQuotaWidget.1A8FA33B";
        _singleInstanceMutex = new Mutex(true, mutexName, out var createdNew);

        if (!createdNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var qaOutputPath = GetQaOutputPath(e.Args);
        var settings = AppSettings.Load();
        ThemeService.Apply(settings.Theme);
        var window = new MainWindow(settings, qaOutputPath);
        MainWindow = window;
        window.Show();
    }

    private static string? GetQaOutputPath(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index].Equals("--qa-output", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }

        return null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
