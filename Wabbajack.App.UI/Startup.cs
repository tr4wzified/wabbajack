using Avalonia;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using ReactiveUI;
using Splat;
using System.IO;
using System.Reactive;

namespace Wabbajack.App.UI;
public class Startup
{
    public static void Main(string[] args)
    {
        var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                //var appFolder = FileSystem.Shared.GetKnownPath(KnownPath.EntryDirectory);
                //var configJson = File.ReadAllText(appFolder.Combine("AppConfig.json").GetFullPath());

                //config = JsonSerializer.Deserialize<AppConfig>(configJson);
                //config.Sanitize();
                //services.AddApp(config).Validate();
                services.AddFileSystem();
                services.AddUI(null);
            })
            //.ConfigureLogging((_, builder) => AddLogging(builder, ConfigurationPath.LoggingSet))
            .Build();
        /*
        RxApp.DefaultExceptionHandler = Observer.Create<Exception>(ex =>
        {
            _logger.LogError(ex, "Unhandled exception");
        })
        */
        BuildAvaloniaApp(host.Services)
        .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp(IServiceProvider serviceProvider)
    {
        //ReactiveUiExtensions.DefaultLogger = serviceProvider.GetRequiredService<ILogger<Startup>>();


        var app = AppBuilder.Configure(serviceProvider.GetRequiredService<App>)
                            .UsePlatformDetect()
                            .LogToTrace(Avalonia.Logging.LogEventLevel.Verbose)
                            .UseReactiveUI();

        Locator.CurrentMutable.UnregisterCurrent(typeof(IViewLocator));
        Locator.CurrentMutable.Register(serviceProvider.GetRequiredService<InjectedViewLocator>, typeof(IViewLocator));

        return app;

    }
}
