
namespace Wabbajack.App.UI;

public interface ILauncherSettings
{
    /// <summary>
    /// Overrides the current locale of the application at startup.
    /// </summary>
    /// <remarks>If this value is empty, the locale will not be overwritten.</remarks>
    public string LocaleOverride { get; set; }
}

public class LauncherSettings : ILauncherSettings
{
    public string LocaleOverride { get; set; } = string.Empty;

    // ReSharper disable once EmptyConstructor
    public LauncherSettings() { }
}
