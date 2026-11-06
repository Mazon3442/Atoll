using System;
using System.Windows;
using Velopack;

namespace Atoll;

// Velopack requires VelopackApp.Build().Run() as the first line of Main so install, uninstall and
// apply-on-restart hooks fire before WPF spins up. App.xaml is a Page (see Atoll.App.csproj) so the
// SDK does not generate a competing Main.
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().SetArgs(args).Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
