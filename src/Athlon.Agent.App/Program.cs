using System.Windows;
using Velopack;

namespace Athlon.Agent.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ =>
            {
                // Preserve ~/.athlon-agent user data on uninstall.
            })
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
