using System.Windows;

namespace Launcher;

public partial class App : Application
{
    /// Client box: started with --client, the launcher hides everything to do with
    /// hosting so a player only sees the fields they need - name, password, the address
    /// of the cluster they are joining, and Launch Game.
    public static bool ClientOnly { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        ClientOnly = e.Args.Any(a => a.Equals("--client", StringComparison.OrdinalIgnoreCase));
        base.OnStartup(e);
        new MainWindow().Show();
    }
}
