using Pdrive.Sync;

namespace Pdrive.App.Services;

public enum AppTheme { Auto, Light, Dark }

/// <summary>Light, dark, or following the system; saved in ~/.config/pdrive/theme.</summary>
public interface IAppearance
{
    AppTheme Theme { get; set; }
}

/// <summary>Reads and writes the theme choice. A missing or unreadable file means <see cref="AppTheme.Auto"/>.</summary>
public sealed class ThemeStore(string configDir)
{
    private string File => Path.Combine(configDir, "theme");

    public AppTheme Load()
    {
        try
        {
            return System.IO.File.ReadAllText(File).Trim() switch
            {
                "light" => AppTheme.Light,
                "dark" => AppTheme.Dark,
                _ => AppTheme.Auto,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return AppTheme.Auto;
        }
    }

    public void Save(AppTheme theme)
    {
        PdrivePaths.EnsurePrivateDir(configDir);
        System.IO.File.WriteAllText(File, theme.ToString().ToLowerInvariant() + "\n");
    }
}

/// <summary>Applies the theme to the running app (every window follows) and remembers it.</summary>
public sealed class Appearance(ThemeStore store, Action<AppTheme> apply) : IAppearance
{
    private AppTheme _theme = store.Load();

    public AppTheme Theme
    {
        get => _theme;
        set
        {
            if (value == _theme)
                return;
            _theme = value;
            apply(value);
            try
            {
                store.Save(value);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // applied for this session; it just won't be remembered
            }
        }
    }

    public static Avalonia.Styling.ThemeVariant VariantFor(AppTheme theme) => theme switch
    {
        AppTheme.Light => Avalonia.Styling.ThemeVariant.Light,
        AppTheme.Dark => Avalonia.Styling.ThemeVariant.Dark,
        _ => Avalonia.Styling.ThemeVariant.Default, // follow the system
    };
}
