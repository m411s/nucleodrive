using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using NucleoDrive.App.Services;

namespace NucleoDrive.App.Views;

public sealed partial class SetupWindow : Window
{
    public SetupWindow()
    {
        AvaloniaXamlLoader.Load(this);
        if (StatusIcons.InstalledAppIconSafe() is { } icon)
            Icon = icon;
    }
}
