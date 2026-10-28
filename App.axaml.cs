using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using tgdl.ViewModels;
using tgdl.Views;

namespace tgdl;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.Exit += (_, _) =>
            {
                try { vm.DisposeAsync().AsTask().Wait(3000); } catch { /* 退出时不再抛 */ }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}