using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Pdrive.App.Services;

namespace Pdrive.App.Views;

public sealed partial class SetupWindow : Window
{
    public SetupWindow()
    {
        AvaloniaXamlLoader.Load(this);
        if (StatusIcons.InstalledAppIconSafe() is { } icon)
            Icon = icon;
    }
}
