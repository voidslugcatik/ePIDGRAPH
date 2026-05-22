using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ƎPIDGRAPH.ViewModels;
using ƎPIDGRAPH.Views;
using ƎPIDGRAPH.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ƎPIDGRAPH
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            var services = new ServiceCollection();

            services.AddSingleton<IBBLFileService, BBLFileService>();
            services.AddTransient<MainWindowViewModel>();
            services.AddTransient<MainWindow>();

            var provider = services.BuildServiceProvider();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var mainWindow = provider.GetRequiredService<MainWindow>();
                desktop.MainWindow = mainWindow;
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}