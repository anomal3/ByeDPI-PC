using System.Collections.ObjectModel;
using ByeDpiPc.App.Services;
using ByeDpiPc.Core;
using ByeDpiPc.Core.Engine;
using ByeDpiPc.Core.Sharing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ByeDpiPc.App.ViewModels;

/// <summary>Окно «Поделиться настройками»: файл .bdpi или код bdpi:… для друга.</summary>
public sealed partial class ShareExportViewModel : ViewModelBase
{
    private readonly AppHost _host;
    private readonly Action _close;

    [ObservableProperty] private string _name = "Мои настройки ByeDPI";
    [ObservableProperty] private bool _includeStrategy = true;
    [ObservableProperty] private bool _includeRules = true;
    [ObservableProperty] private bool _includeUpstream;
    [ObservableProperty] private string? _status;

    public bool HasUpstream { get; }
    public string UpstreamText { get; }

    public ShareExportViewModel(AppHost host, Action close)
    {
        _host = host;
        _close = close;
        HasUpstream = host.Settings.UpstreamEnabled && !string.IsNullOrWhiteSpace(host.Settings.UpstreamUri);
        UpstreamText = HasUpstream && Core.Routing.UpstreamProxy.TryParse(host.Settings.UpstreamUri, out var p, out _) ? p!.Describe() : "не настроен";
    }

    private ShareBundle Build() => ShareBundle.Create(_host.Settings, Name.Trim(), new ShareBundle.Options
    {
        Strategy = IncludeStrategy, Rules = IncludeRules, Upstream = IncludeUpstream && HasUpstream,
    });

    [RelayCommand]
    private async Task SaveFile()
    {
        var file = (string.IsNullOrWhiteSpace(Name) ? "byedpi" : string.Concat(Name.Split(Path.GetInvalidFileNameChars()))) + ShareBundle.Extension;
        if (await Ui.SaveFileAsync(file, "Настройки ByeDPI PC", "*.bdpi", Build().ToJson()))
            Status = "Файл сохранён — отправьте его другу. Он откроет его через «Импорт» или просто перетащит в окно программы.";
    }

    [RelayCommand]
    private async Task CopyCode()
    {
        await Ui.CopyAsync(Build().ToCode());
        Status = "Код скопирован. Вставьте его в сообщение — друг нажмёт «Импорт → Вставить код».";
    }

    [RelayCommand]
    private void Close() => _close();
}

/// <summary>Окно импорта: показывает, что изменится, и применяет по кнопке.</summary>
public sealed partial class ShareImportViewModel : ViewModelBase
{
    private readonly AppHost _host;
    private readonly Action _close;
    private ShareBundle? _bundle;

    public ObservableCollection<string> Changes { get; } = new();

    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _title;
    [ObservableProperty] private string? _comment;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _done;

    public ShareImportViewModel(AppHost host, Action close, string? initialText = null)
    {
        _host = host;
        _close = close;
        if (initialText != null)
        {
            Code = initialText;
            Preview();
        }
    }

    partial void OnCodeChanged(string value)
    {
        if (value.Length > 10) Preview();
    }

    private void Preview()
    {
        Error = null;
        Changes.Clear();
        IsReady = false;
        _bundle = null;
        if (string.IsNullOrWhiteSpace(Code)) return;
        try
        {
            _bundle = ShareBundle.Parse(Code);
            Title = string.IsNullOrWhiteSpace(_bundle.Name) ? "Настройки" : _bundle.Name;
            Comment = _bundle.Comment;
            foreach (var c in _bundle.Describe()) Changes.Add(c);
            IsReady = Changes.Count > 0;
            if (!IsReady) Error = "В наборе нет настроек.";
        }
        catch (UserFacingException ex)
        {
            Error = ex.Message + " " + ex.Advice;
        }
    }

    [RelayCommand]
    private async Task OpenFile()
    {
        var text = await Ui.ReadTextFileAsync("Файл настроек ByeDPI PC", "*.bdpi");
        if (text != null) Code = text;
        Preview();
    }

    [RelayCommand]
    private async Task Paste()
    {
        var text = await Ui.PasteAsync();
        if (!string.IsNullOrWhiteSpace(text)) Code = text.Trim();
        Preview();
    }

    [RelayCommand]
    private async Task Apply()
    {
        if (_bundle == null) return;
        _bundle.ApplyTo(_host.Settings);
        Presets.EnsureDefaults(_host.Settings);
        _host.StrategyBroken = false;
        _host.Save();
        _host.NotifyStrategiesChanged();
        _host.NotifyRulesChanged();
        Done = true;
        Log.Info("Импорт", $"Применены настройки «{Title}»");
        if (_host.Connection.IsConnected) await _host.Connection.ApplyAsync(_host.Settings);
    }

    [RelayCommand]
    private void Close() => _close();
}
