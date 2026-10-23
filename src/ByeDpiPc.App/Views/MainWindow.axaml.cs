using Avalonia.Controls;
using Avalonia.Input;
using ByeDpiPc.App.ViewModels;

namespace ByeDpiPc.App.Views;

public partial class MainWindow : Window
{
    /// <summary>true — окно действительно закрывается (выход из программы), а не прячется в трей.</summary>
    public bool AllowClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();
    }

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
