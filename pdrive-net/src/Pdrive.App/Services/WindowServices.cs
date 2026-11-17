using Pdrive.App.Views;

namespace Pdrive.App.Services;

/// <summary>Dialogs and toasts for the view models, while the main window itself comes and goes
/// (it's only built when opened, and released when closed to the tray).</summary>
public sealed class WindowServices(Func<MainWindow> showWindow, Func<MainWindow?> currentWindow) : IDialogs, IToaster
{
    public Task<IReadOnlyList<string>> PickFilesAsync(string title) => showWindow().PickFilesAsync(title);

    public Task<string?> PickFolderAsync(string title) => showWindow().PickFolderAsync(title);

    public Task<bool> ConfirmAsync(string title, string message, string yes = "Yes", string no = "Cancel") =>
        showWindow().ConfirmAsync(title, message, yes, no);

    /// <summary>Shown in the window if it's open; the tray icon already reflects the state otherwise.</summary>
    public void Show(string message, bool error = false) => currentWindow()?.Show(message, error);
}
