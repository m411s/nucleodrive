using System.Reflection;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Pdrive.App.Views;

/// <summary>Version, what pdrive is, the Proton disclaimer, the MIT licence and a link to the project.</summary>
public sealed partial class AboutWindow : Window
{
    public const string ProjectUrl = "https://github.com/m411s/pdrive";

    public AboutWindow() : this(_ => { })
    {
    }

    /// <param name="openUrl">Opens a link in the browser (xdg-open in the app).</param>
    public AboutWindow(Action<string> openUrl)
    {
        AvaloniaXamlLoader.Load(this);
        if (Pdrive.App.Services.StatusIcons.InstalledAppIconSafe() is { } icon)
            Icon = icon;
        this.FindControl<TextBlock>("VersionText")!.Text = "Version " + Version;
        this.FindControl<SelectableTextBlock>("LinkText")!.Text = ProjectUrl;
        this.FindControl<SelectableTextBlock>("LicenseBox")!.Text = System.Text.RegularExpressions.Regex.Replace(LicenseText.ReplaceLineEndings("\n"), @"(?<!\n)\n(?!\n)", " "); // reflow paragraphs
        this.FindControl<Button>("GitHubButton")!.Click += (_, _) => openUrl(ProjectUrl);
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
        try
        {
            this.FindControl<Image>("Logo")!.Source = new Bitmap(AssetLoader.Open(new Uri("avares://pdrive/Assets/pdrive.png")));
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or ArgumentException)
        {
            // no image backend (headless tests)
        }
    }

    /// <summary>The informational version ("0.1.0+commit"), shortened to 7 hex digits of the commit.</summary>
    public static string Version
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? "unknown";
            var plus = v.IndexOf('+');
            return plus >= 0 && v.Length > plus + 8 ? v[..(plus + 8)] : v;
        }
    }

    public static string LicenseText
    {
        get
        {
            try
            {
                using var reader = new StreamReader(AssetLoader.Open(new Uri("avares://pdrive/Assets/LICENSE.txt")));
                return reader.ReadToEnd();
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or FileNotFoundException)
            {
                return "MIT License. See " + ProjectUrl + "/blob/main/LICENSE";
            }
        }
    }
}
