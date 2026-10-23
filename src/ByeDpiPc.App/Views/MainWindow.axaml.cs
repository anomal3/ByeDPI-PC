using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using ByeDpiPc.App.ViewModels;

namespace ByeDpiPc.App.Views;

public partial class MainWindow : Window
{
    /// <summary>true — окно действительно закрывается (выход из программы), а не прячется в трей.</summary>
    public bool AllowClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>Перетащили файл .bdpi (или текст с кодом) в окно — открыть импорт.</summary>
#pragma warning disable CS0618 // DataTransfer в Avalonia 11.3 ещё не везде удобнее старого API
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        string? text = null;
        if (e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath() is { } path && File.Exists(path) && new FileInfo(path).Length < 1_000_000)
            text = await File.ReadAllTextAsync(path);
        else if (e.Data.GetText() is { } t)
            text = t;
        if (!string.IsNullOrWhiteSpace(text)) vm.ImportText(text);
    }
#pragma warning restore CS0618

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose && DataContext is MainWindowViewModel vm && !vm.Host.DemoMode)
        {
            e.Cancel = true;
            if (vm.Host.Settings.CloseToTray) Hide();
            else if (Avalonia.Application.Current is App app) _ = app.ExitAsync();
        }
        base.OnClosing(e);
    }

    private void OnDialogBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.Dialog = null;
    }

    private void OnDialogPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
}
