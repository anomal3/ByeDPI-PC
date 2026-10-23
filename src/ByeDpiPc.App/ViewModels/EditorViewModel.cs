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
    public bool IsActive => SelectedProfile == _host.Settings.ActiveStrategy;

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
        foreach (var p in _host.Settings.Strategies) Profiles.Add(p.Name);
        SelectedProfile = keep != null && Profiles.Contains(keep) ? keep : _host.Settings.GetActiveStrategy()?.Name;
        LoadProfile();
    }

    partial void OnSelectedProfileChanged(string? value) => LoadProfile();

    private void LoadProfile()
    {
        var p = _host.Settings.Strategies.FirstOrDefault(s => s.Name == SelectedProfile);
        _loading = true;
        ProfileName = p?.Name ?? "";
        Arguments = p?.Arguments ?? "";
        LoadSimple(Arguments);
        IsSimpleMode = SimpleAvailable;
        _loading = false;
        IsDirty = false;
        Validation = null;
        OnPropertyChanged(nameof(IsActive));
    }

    private void LoadSimple(string args)
    {
        var s = UiStrategy.TryParse(args);
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
            case nameof(Arguments) or nameof(ProfileName):
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
        if (UiStrategy.TryParse(Arguments) == null)
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
        var p = _host.Settings.Strategies.FirstOrDefault(s => s.Name == SelectedProfile);
        if (p == null) return;
        var name = string.IsNullOrWhiteSpace(ProfileName) ? p.Name : ProfileName.Trim();
        if (name != p.Name && _host.Settings.Strategies.Any(s => s.Name == name)) name += " (2)";
        var wasActive = _host.Settings.ActiveStrategy == p.Name;
        p.Name = name;
        p.Arguments = Arguments.Trim();
        if (wasActive) _host.Settings.ActiveStrategy = name;
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
        _host.Settings.Strategies.Add(new StrategyProfile(name, new UiStrategy().ToArguments()));
        _host.Save(apply: false);
        _host.NotifyStrategiesChanged();
        SelectedProfile = name;
        IsSimpleMode = true;
    }

    [RelayCommand]
    private void Duplicate()
    {
        var name = Unique((ProfileName.Length > 0 ? ProfileName : "Стратегия") + " — копия");
        _host.Settings.Strategies.Add(new StrategyProfile(name, Arguments));
        _host.Save(apply: false);
        _host.NotifyStrategiesChanged();
        SelectedProfile = name;
    }

    [RelayCommand]
    private void Delete()
    {
        if (_host.Settings.Strategies.Count <= 1) return;
        var p = _host.Settings.Strategies.FirstOrDefault(s => s.Name == SelectedProfile);
        if (p == null) return;
        _host.Settings.Strategies.Remove(p);
        if (_host.Settings.ActiveStrategy == p.Name) _host.Settings.ActiveStrategy = _host.Settings.Strategies[0].Name;
        _host.Save();
        SelectedProfile = null;
        _host.NotifyStrategiesChanged();
    }

    [RelayCommand]
    private void Revert() => LoadProfile();

    [RelayCommand]
    private void OpenHelp() => Ui.Open("https://github.com/hufrea/byedpi#readme");

    [RelayCommand]
    private async Task Validate()
    {
        IsValidating = true;
        Validation = null;
        try
        {
            var error = await Task.Run(() => CiadpiEngine.ValidateAsync(Arguments, _host.Settings.FakeSni));
            ValidationOk = error == null;
            Validation = error ?? "ciadpi запустился с этими аргументами — стратегия корректна.";
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
        for (int i = 2; _host.Settings.Strategies.Any(s => s.Name == name); i++) name = $"{baseName} {i}";
        return name;
    }
}
