using ByeDpiPc.Core;
using ByeDpiPc.Core.Engine;
using ByeDpiPc.Core.Routing;
using Xunit;

namespace ByeDpiPc.Core.Tests;

public class ZapretTests
{
    private static AppSettings Settings(FilterMode filter = FilterMode.Whitelist)
    {
        var s = new AppSettings { Filter = filter };
        Presets.EnsureDefaults(s);
        return s;
    }

    [Fact]
    public void Presets_HaveZapretStrategies()
    {
        var s = Settings();
        Assert.True(s.EngineStrategies(EngineKind.Zapret).Count() >= 10);
        Assert.Equal("Универсальная (seqovl)", s.ActiveZapretStrategy);
        Assert.Contains("--dpi-desync=", s.GetActiveStrategy(EngineKind.Zapret)!.Arguments);
    }

    [Fact]
    public void Whitelist_UsesHostlistAndIpset()
    {
        var s = Settings();
        var plan = RoutePlan.Build(s);
        var dir = Path.Combine(Path.GetTempPath(), "zapret-test-" + Guid.NewGuid().ToString("N"));
        var lists = ZapretCommand.WriteLists(plan, s.Filter, dir);
        Assert.NotNull(lists.Hosts);
        Assert.Contains("youtube.com", File.ReadAllLines(lists.Hosts!));
        Assert.NotNull(lists.Ipset);

        var args = ZapretCommand.Build(plan, s.Filter, "--dpi-desync=fake --dpi-desync-repeats=6", lists);
        Assert.StartsWith("--wf-tcp=80,443", args[0]);
        Assert.Contains($"--hostlist={lists.Hosts}", args);
        Assert.Contains($"--ipset={lists.Ipset}", args);
        Assert.Contains("--filter-l7=discord,stun", args); // Discord включён по умолчанию
        Assert.DoesNotContain(args, a => a.StartsWith("--hostlist-exclude"));
        Assert.NotEqual("--new", args[^1]);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Blacklist_ExcludesSelected()
    {
        var s = Settings(FilterMode.Blacklist);
        var plan = RoutePlan.Build(s);
        var dir = Path.Combine(Path.GetTempPath(), "zapret-test-" + Guid.NewGuid().ToString("N"));
        var lists = ZapretCommand.WriteLists(plan, s.Filter, dir);
        Assert.Null(lists.Hosts);
        Assert.NotNull(lists.HostsExclude);
        var args = ZapretCommand.Build(plan, s.Filter, "--dpi-desync=fake", lists);
        Assert.DoesNotContain(args, a => a.StartsWith("--hostlist="));
        Assert.Contains($"--hostlist-exclude={lists.HostsExclude}", args);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Whitelist_Empty_IsUserError()
    {
        var s = Settings();
        foreach (var r in s.Rules) r.Enabled = false;
        var plan = RoutePlan.Build(s);
        var lists = ZapretCommand.WriteLists(plan, s.Filter, Path.Combine(Path.GetTempPath(), "zapret-test-" + Guid.NewGuid().ToString("N")));
        Assert.Throws<UserFacingException>(() => ZapretCommand.Build(plan, s.Filter, "--dpi-desync=fake", lists));
    }

    [Fact]
    public void UpstreamOnlyPlan_RoutesOnlyUpstream()
    {
        var s = Settings();
        s.UpstreamEnabled = true;
        s.UpstreamUri = "socks5://1.2.3.4:1080";
        s.Rules.First(r => r.Id == "preset.telegram").Route = RouteMode.Upstream;
        var plan = RoutePlan.Build(s);
        Assert.True(plan.HasUpstreamTargets);
        var only = plan.UpstreamOnly();
        Assert.Empty(only.ByeDpi.Domains);
        Assert.Contains("telegram.org", only.Upstream.Domains);
        Assert.Equal(RouteMode.Direct, only.Final);
    }

    [Fact]
    public void OldSettings_MigrateToZapretOnWindows()
    {
        var s = new AppSettings { SchemaVersion = 1, Strategies = { new StrategyProfile("Моя", "-o1") }, ActiveStrategy = "Моя" };
        Presets.EnsureDefaults(s);
        Assert.Equal(2, s.SchemaVersion);
        Assert.Equal("Моя", s.ActiveStrategy);
        Assert.NotEmpty(s.EngineStrategies(EngineKind.Zapret));
        if (OperatingSystem.IsWindows()) Assert.Equal(EngineKind.Zapret, s.Engine);
    }
}
