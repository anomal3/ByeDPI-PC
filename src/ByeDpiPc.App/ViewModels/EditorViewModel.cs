using System.Collections.ObjectModel;
using ByeDpiPc.App.Services;
using ByeDpiPc.Core;
using ByeDpiPc.Core.Engine;
using ByeDpiPc.Core.Strategy;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ByeDpiPc.App.ViewModels;

public sealed partial class EditorViewModel : ViewModelBase
{
    private readonly AppHost _host;
    private bool _loading;

    public ObservableCollection<string> Profiles { get; } = new();

    public string[] Methods { get; } =
    {
        "Нет", "Split — разбить запрос", "Disorder — разбить и переставить", "Fake — поддельный пакет",
        "OOB — внеполосный байт", "DisOOB — disorder + OOB",
    };

    [ObservableProperty] private string? _selectedProfile;
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _arguments = "";
    [ObservableProperty] private string _googleArguments = "";
    [ObservableProperty] private bool _isSimpleMode = true;
    [ObservableProperty] private bool _simpleAvailable = true;
    [ObservableProperty] private string? _validation;
    [ObservableProperty] private bool _validationOk;
    [ObservableProperty] private bool _isValidating;
    [ObservableProperty] private bool _isDirty;

    // Простой режим
    [ObservableProperty] private int _methodIndex;
    [ObservableProperty] private decimal _splitPosition = 1;
    [ObservableProperty] private bool _splitAtSni;
    [ObservableProperty] private decimal _fakeTtl = 8;
    [ObservableProperty] private bool _useFakeSni;
    [ObservableProperty] private string _oobChar = "a";
    [ObservableProperty] private bool _hostMixedCase;
    [ObservableProperty] private bool _domainMixedCase;
    [ObservableProperty] private bool _hostRemoveSpaces;
    [ObservableProperty] private bool _tlsRec;
    [ObservableProperty] private decimal _tlsRecPosition = 1;
    [ObservableProperty] private bool _tlsRecAtSni = true;
    [ObservableProperty] private bool _tlsRecFromEnd;
    [ObservableProperty] private decimal _udpFakeCount = 1;
    [ObservableProperty] private bool _desyncHttp = true;
    [ObservableProperty] private bool _desyncHttps = true;
    [ObservableProperty] private bool _desyncUdp = true;
    [ObservableProperty] private decimal _defaultTtl;
    [ObservableProperty] private bool _autoOnBlock;

    public bool IsFake => MethodIndex == (int)DesyncMethod.Fake;
    public bool IsOob => MethodIndex is (int)DesyncMethod.Oob or (int)DesyncMethod.DisOob;
    public bool HasMethod => MethodIndex != 0;
    public bool IsActive => SelectedProfile == _host.Settings.GetActiveName(Engine);
    private EngineKind Engine => _host.Settings.Engine;
    public bool IsZapret => Engine == EngineKind.Zapret;
    public bool ComplexWarning => !SimpleAvailable && !IsZapret;
    partial void OnSimpleAvailableChanged(bool value) => OnPropertyChanged(nameof(ComplexWarning));
    public string EngineHint => IsZapret
        ? "Стратегия — аргументы winws (zapret) для TCP 80/443. Профили для QUIC и голоса Discord программа добавляет сама."
        : "Стратегия — это аргументы byedpi: как «запутать» первые пакеты соединения, чтобы фильтр провайдера не увидел адрес сайта. Можно хранить несколько стратегий и переключаться между ними на главной.";

    public string FakeSni
    {
        get => _host.Settings.FakeSni;
        set
        {
            _host.Settings.FakeSni = value;
            OnPropertyChanged();
            _host.Save();
        }
    }

    public EditorViewModel(AppHost host)
    {
        _host = host;
        host.StrategiesChanged += ReloadProfiles;
        ReloadProfiles();
    }

    public override void OnShown()
    {
        if (!IsDirty) ReloadProfiles();
    }

    private void ReloadProfiles()
    {
        var keep = SelectedProfile;
        Profiles.Clear();
        foreach (var p in _host.Settings.EngineStrategies()) Profiles.Add(p.Name);
        OnPropertyChanged(nameof(IsZapret));
        OnPropertyChanged(nameof(EngineHint));
        SelectedProfile = keep != null && Profiles.Contains(keep) ? keep : _host.Settings.GetActiveStrategy()?.Name;
        LoadProfile();
    }

    partial void OnSelectedProfileChanged(string? value) => LoadProfile();

    private void LoadProfile()
    {
        var p = _host.Settings.EngineStrategies().FirstOrDefault(s => s.Name == SelectedProfile);
        _loading = true;
        ProfileName = p?.Name ?? "";
        Arguments = p?.Arguments ?? "";
        GoogleArguments = p?.GoogleArguments ?? "";
        LoadSimple(Arguments);
        IsSimpleMode = SimpleAvailable;
        _loading = false;
        IsDirty = false;
        Validation = null;
        OnPropertyChanged(nameof(IsActive));
    }

    private void LoadSimple(string args)
    {
        var s = IsZapret ? null : UiStrategy.TryParse(args);
        SimpleAvailable = s != null;
        if (s == null)
        {
            IsSimpleMode = false;
            return;
        }
        var wasLoading = _loading;
        _loading = true;
        MethodIndex = (int)s.Method;
        SplitPosition = s.SplitPosition;
        SplitAtSni = s.SplitAtSni;
        FakeTtl = s.FakeTtl;
        UseFakeSni = s.UseFakeSni;
        OobChar = s.OobChar;
        HostMixedCase = s.HostMixedCase;
        DomainMixedCase = s.DomainMixedCase;
        HostRemoveSpaces = s.HostRemoveSpaces;
        TlsRec = s.TlsRec;
        TlsRecPosition = s.TlsRecPosition;
        TlsRecAtSni = s.TlsRecAtSni;
        TlsRecFromEnd = s.TlsRecFromEnd;
        UdpFakeCount = s.UdpFakeCount;
        DesyncHttp = s.DesyncHttp;
        DesyncHttps = s.DesyncHttps;
        DesyncUdp = s.DesyncUdp;
        DefaultTtl = s.DefaultTtl;
        AutoOnBlock = s.AutoOnBlock;
        _loading = wasLoading;
    }

    private UiStrategy BuildSimple() => new()
    {
        Method = (DesyncMethod)MethodIndex,
        SplitPosition = (int)SplitPosition,
        SplitAtSni = SplitAtSni,
        FakeTtl = (int)FakeTtl,
        UseFakeSni = UseFakeSni,
        OobChar = string.IsNullOrEmpty(OobChar) ? "a" : OobChar[..1],
        HostMixedCase = HostMixedCase,
        DomainMixedCase = DomainMixedCase,
        HostRemoveSpaces = HostRemoveSpaces,
        TlsRec = TlsRec,
        TlsRecPosition = (int)TlsRecPosition,
        TlsRecAtSni = TlsRecAtSni,
        TlsRecFromEnd = TlsRecFromEnd,
        UdpFakeCount = (int)UdpFakeCount,
        DesyncHttp = DesyncHttp,
        DesyncHttps = DesyncHttps,
        DesyncUdp = DesyncUdp,
        DefaultTtl = (int)DefaultTtl,
        AutoOnBlock = AutoOnBlock,
    };

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(MethodIndex))
        {
            OnPropertyChanged(nameof(IsFake));
            OnPropertyChanged(nameof(IsOob));
            OnPropertyChanged(nameof(HasMethod));
        }
        if (_loading) return;
        switch (e.PropertyName)
        {
            case nameof(MethodIndex) or nameof(SplitPosition) or nameof(SplitAtSni) or nameof(FakeTtl) or nameof(UseFakeSni)
                or nameof(OobChar) or nameof(HostMixedCase) or nameof(DomainMixedCase) or nameof(HostRemoveSpaces)
                or nameof(TlsRec) or nameof(TlsRecPosition) or nameof(TlsRecAtSni) or nameof(TlsRecFromEnd)
                or nameof(UdpFakeCount) or nameof(DesyncHttp) or nameof(DesyncHttps) or nameof(DesyncUdp)
                or nameof(DefaultTtl) or nameof(AutoOnBlock):
                if (IsSimpleMode)
                {
                    _loading = true;
                    Arguments = BuildSimple().ToArguments();
                    _loading = false;
                    IsDirty = true;
                    Validation = null;
                }
                break;
            case nameof(Arguments) or nameof(ProfileName) or nameof(GoogleArguments):
                IsDirty = true;
                Validation = null;
                break;
        }
    }

    partial void OnIsSimpleModeChanged(bool value)
    {
        if (value) LoadSimple(Arguments);
    }

    [RelayCommand]
    private void SimpleMode()
    {
        if (IsZapret || UiStrategy.TryParse(Arguments) == null)
        {
            SimpleAvailable = false;
            return;
        }
        IsSimpleMode = true;
    }

    [RelayCommand]
    private void CommandMode() => IsSimpleMode = false;

    [RelayCommand]
    private void Save()
    {
        var p = _host.Settings.EngineStrategies().FirstOrDefault(s => s.Name == SelectedProfile);
        if (p == null) return;
        var name = string.IsNullOrWhiteSpace(ProfileName) ? p.Name : ProfileName.Trim();
        if (name != p.Name && _host.Settings.EngineStrategies().Any(s => s.Name == name)) name += " (2)";
        var wasActive = _host.Settings.GetActiveName(Engine) == p.Name;
        p.Name = name;
        p.Arguments = Arguments.Trim();
        p.GoogleArguments = IsZapret && !string.IsNullOrWhiteSpace(GoogleArguments) ? GoogleArguments.Trim() : null;
        if (wasActive) _host.Settings.SetActiveName(Engine, name);
        _host.Save();
        IsDirty = false;
        SelectedProfile = null;
        _host.NotifyStrategiesChanged();
        SelectedProfile = name;
    }

    [RelayCommand]
    private void MakeActive()
    {
        if (IsDirty) Save();
        if (SelectedProfile != null) _host.SetActiveStrategy(SelectedProfile);
        OnPropertyChanged(nameof(IsActive));
    }

    [RelayCommand]
    private void NewProfile()
    {
        var name = Unique("Новая стратегия");
        var args = IsZapret ? Presets.LoadZapretStrategies()[0].Arguments : new UiStrategy().ToArguments();
        _host.Settings.Strategies.Add(new StrategyProfile(name, args, Engine));
        _host.Save(apply: false);
        _host.NotifyStrategiesChanged();
        SelectedProfile = name;
        IsSimpleMode = true;
    }

    [RelayCommand]
    private void Duplicate()
    {
        var name = Unique((ProfileName.Length > 0 ? ProfileName : "Стратегия") + " — копия");
        _host.Settings.Strategies.Add(new StrategyProfile(name, Arguments, Engine));
        _host.Save(apply: false);
        _host.NotifyStrategiesChanged();
        SelectedProfile = name;
    }

    [RelayCommand]
    private void Delete()
    {
        if (_host.Settings.EngineStrategies().Count() <= 1) return;
        var p = _host.Settings.EngineStrategies().FirstOrDefault(s => s.Name == SelectedProfile);
        if (p == null) return;
        _host.Settings.Strategies.Remove(p);
        if (_host.Settings.GetActiveName(Engine) == p.Name)
            _host.Settings.SetActiveName(Engine, _host.Settings.EngineStrategies().First().Name);
        _host.Save();
        SelectedProfile = null;
        _host.NotifyStrategiesChanged();
    }

    [RelayCommand]
    private void Revert() => LoadProfile();

    [RelayCommand]
    private void OpenHelp() => Ui.Open(IsZapret ? "https://github.com/bol-van/zapret/blob/master/docs/readme.md#nfqws" : "https://github.com/hufrea/byedpi#readme");

    [RelayCommand]
    private async Task Validate()
    {
        IsValidating = true;
        Validation = null;
        try
        {
            string? error;
            if (IsZapret)
            {
                var r = await Task.Run(() => new ByeDpiPc.Core.Testing.StrategyTester { Engine = EngineKind.Zapret }
                    .TestOneAsync(Arguments, new[] { "youtube.com" }, CancellationToken.None));
                error = r.Error;
            }
            else
            {
                error = await Task.Run(() => CiadpiEngine.ValidateAsync(Arguments, _host.Settings.FakeSni));
            }
            ValidationOk = error == null;
            Validation = error ?? (IsZapret ? "winws запустился с этими аргументами — стратегия корректна." : "ciadpi запустился с этими аргументами — стратегия корректна.");
        }
        catch (Exception ex)
        {
            ValidationOk = false;
            Validation = ex.Message;
        }
        finally
        {
            IsValidating = false;
        }
    }

    private string Unique(string baseName)
    {
        var name = baseName;
        for (int i = 2; _host.Settings.EngineStrategies().Any(s => s.Name == name); i++) name = $"{baseName} {i}";
        return name;
    }
}
