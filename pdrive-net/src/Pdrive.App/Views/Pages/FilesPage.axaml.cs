using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Pdrive.App.ViewModels;

namespace Pdrive.App.Views.Pages;

public sealed partial class FilesPage : UserControl
{
    public FilesPage()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDragOver(false));
        AddHandler(DragDrop.DropEvent, OnDrop);

        var list = this.FindControl<ListBox>("EntryList")!;
        list.DoubleTapped += (_, _) => OpenSelected(list);
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                OpenSelected(list);
            else if (e.Key == Key.Back && Vm is { } vm && vm.BackCommand.CanExecute(null))
                _ = vm.BackCommand.ExecuteAsync(null);
        };
    }

    private BrowserViewModel? Vm => DataContext as BrowserViewModel;

    private void OpenSelected(ListBox list)
    {
        if (list.SelectedItem is EntryViewModel { IsDir: true } entry && Vm is { } vm)
            _ = vm.OpenAsync(entry);
    }

    private void SetDragOver(bool value)
    {
        if (Vm is { } vm)
            vm.IsDragOver = value;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.TryGetFiles() is { Length: > 0 } && Vm is { HasClient: true };
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        SetDragOver(hasFiles);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        SetDragOver(false);
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
        if (paths.Count > 0 && Vm is { } vm)
            _ = vm.UploadPathsAsync(paths);
        e.Handled = true;
    }
}
