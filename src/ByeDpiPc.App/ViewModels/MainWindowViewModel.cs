using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using ByeDpiPc.App.Services;
using ByeDpiPc.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ByeDpiPc.App.ViewModels;

public enum PageKind { Home, Apps, Lists, Editor, Tester, Logs, Settings }

public sealed class NavItem
{
    public required PageKind Kind { get; init; }
    public required string Title { get; init; }
    public required StreamGeometry? Icon { get; init; }
    public required ViewModelBase Page { get; init; }
}

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>Вызывается при каждом показе страницы.</summary>
    public virtual void OnShown() { }
}

public sealed partial class MainWindowViewModel : ViewModelBase
{
    public AppHost Host { get; }
    public ObservableCollection<NavItem> Nav { get; } = new();

    public HomeViewModel Home { get; }
    public AppsViewModel Apps { get; }
    public ListsViewModel Lists { get; }
    public EditorViewModel Editor { get; }
    public TesterViewModel Tester { get; }
    public LogsViewModel Logs { get; }
    public SettingsViewModel SettingsPage { get; }

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private ViewModelBase? _currentPage;
    /// <summary>Модальная панель поверх окна (выбор программ, диагностика).</summary>
    [ObservableProperty] private ViewModelBase? _dialog;
    [ObservableProperty] private bool _isDark;

    public string Version { get; } = "v" + (typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    public MainWindowViewModel(AppHost host)
    {
        Host = host;
        Home = new HomeViewModel(host, Navigate);
        Apps = new AppsViewModel(host, ShowDialog);
        Lists = new ListsViewModel(host);
        Editor = new EditorViewModel(host);
        Tester = new TesterViewModel(host);
        Logs = new LogsViewModel(host, ShowDialog);
        SettingsPage = new SettingsViewModel(host, ApplyTheme, () => ShareExport(), () => ShareImport());

        Add(PageKind.Home, "Главная", "Icon.HomeOutline", Home);
        Add(PageKind.Apps, "Приложения", "Icon.Apps", Apps);
        Add(PageKind.Lists, "Списки", "Icon.FormatListBulletedSquare", Lists);
        Add(PageKind.Editor, "Стратегия", "Icon.TuneVariant", Editor);
        Add(PageKind.Tester, "Подбор", "Icon.FlaskOutline", Tester);
        Add(PageKind.Logs, "Логи", "Icon.TextBoxOutline", Logs);
        Add(PageKind.Settings, "Настройки", "Icon.CogOutline", SettingsPage);
        SelectedNav = Nav[0];
        ApplyTheme(host.Settings.Theme);
    }

    private void Add(PageKind kind, string title, string icon, ViewModelBase page) =>
        Nav.Add(new NavItem { Kind = kind, Title = title, Icon = Ui.Geometry(icon), Page = page });

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value == null) return;
        CurrentPage = value.Page;
        value.Page.OnShown();
    }

    public void Navigate(PageKind kind) => SelectedNav = Nav.First(n => n.Kind == kind);

    public void ShowDialog(ViewModelBase? dialog) => Dialog = dialog;

    [RelayCommand]
    private void CloseDialog() => Dialog = null;

    [RelayCommand]
    public void ShareExport() => Dialog = new ShareExportViewModel(Host, () => Dialog = null);

    [RelayCommand]
    public void ShareImport() => Dialog = new ShareImportViewModel(Host, () => Dialog = null);

    /// <summary>Импорт из перетащенного файла или вставленного текста.</summary>
    public void ImportText(string text) => Dialog = new ShareImportViewModel(Host, () => Dialog = null, text);

    [RelayCommand]
    private void ToggleTheme()
    {
        var next = IsDark ? ThemeMode.Light : ThemeMode.Dark;
        Host.Settings.Theme = next;
        Host.Save(apply: false);
        ApplyTheme(next);
        SettingsPage.RefreshTheme();
    }

    public void ApplyTheme(ThemeMode mode)
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        IsDark = app.ActualThemeVariant == ThemeVariant.Dark;
    }

    public void OnSystemThemeChanged()
    {
        if (Application.Current is { } app) IsDark = app.ActualThemeVariant == ThemeVariant.Dark;
    }
}
