
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Wabbajack.App.UI.Routing;
using ReactiveUI;
using Wabbajack.App.UI;
using Wabbajack.App.UI.Views;
using Wabbajack.App.UI.ViewModels;

namespace Wabbajack.App.UI;

public static class Services
{
    // ReSharper disable once InconsistentNaming
    public static IServiceCollection AddUI(this IServiceCollection c, ILauncherSettings? settings)
    {
        if (settings == null)
            c.AddSingleton<ILauncherSettings, LauncherSettings>();
        else
            c.AddSingleton(settings);

        return c
            .AddTransient<MainWindow>()

            // Services
            .AddSingleton<IRouter, ReactiveMessageRouter>()

            // View Models
            .AddTransient<MainWindowViewModel>()
            .AddSingleton<IViewLocator, InjectedViewLocator>()

            //.AddViewModel<CompletedViewModel, ICompletedViewModel>()

            // Other
            .AddSingleton<InjectedViewLocator>()
            .AddSingleton<App>();
    }

}
