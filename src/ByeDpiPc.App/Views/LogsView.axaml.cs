using Avalonia.Controls;
using Avalonia.Threading;
using ByeDpiPc.App.ViewModels;

namespace ByeDpiPc.App.Views;

public partial class LogsView : UserControl
{
    private LogsViewModel? _vm;

    public LogsView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.ScrollToEnd -= Scroll;
        _vm = DataContext as LogsViewModel;
        if (_vm != null) _vm.ScrollToEnd += Scroll;
        Scroll();
    }

    private void Scroll() => Dispatcher.UIThread.Post(() =>
    {
        if (List.ItemCount > 0) List.ScrollIntoView(List.ItemCount - 1);
    }, DispatcherPriority.Background);
}
