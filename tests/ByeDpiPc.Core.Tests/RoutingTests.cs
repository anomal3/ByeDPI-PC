using System.Diagnostics;
using System.Text.Json.Nodes;
using ByeDpiPc.Core;
using ByeDpiPc.Core.Routing;
using Xunit;

namespace ByeDpiPc.Core.Tests;

public class RoutingTests
{
    private static AppSettings Settings(FilterMode filter = FilterMode.Whitelist)
    {
        var s = new AppSettings { Filter = filter };
        Presets.EnsureDefaults(s);
        return s;
    }

    [Theory]
    [InlineData("https://www.YouTube.com/watch?v=1", "www.youtube.com")]
    [InlineData("*.example.org", "example.org")]
    [InlineData("example.org:443", "example.org")]
    [InlineData("not a domain", null)]
    [InlineData("localhost", null)]
    public void NormalizeDomain(string input, string? expected) => Assert.Equal(expected, RoutePlan.NormalizeDomain(input));

    [Theory]
    [InlineData("91.108.4.0/22", "91.108.4.0/22")]
    [InlineData("1.2.3.4", "1.2.3.4/32")]
    [InlineData("2001:67c:4e8::/48", "2001:67c:4e8::/48")]
    [InlineData("1.2.3.4/40", null)]
    public void NormalizeCidr(string input, string? expected) => Assert.Equal(expected, RoutePlan.NormalizeCidr(input));

    [Fact]
    public void Whitelist_EnabledRulesGoThroughByeDpi()
    {
        var plan = RoutePlan.Build(Settings());
        Assert.Equal(RouteMode.Direct, plan.Final);
        Assert.Contains("youtube.com", plan.ByeDpi.Domains);
        Assert.Contains("Telegram.exe", plan.ByeDpi.Processes);
        Assert.Contains("149.154.160.0/20", plan.ByeDpi.IpCidrs);
        Assert.DoesNotContain("x.com", plan.ByeDpi.Domains); // пресет X выключен по умолчанию
    }

    [Fact]
    public void Blacklist_EnabledRulesGoDirect()
    {
        var plan = RoutePlan.Build(Settings(FilterMode.Blacklist));
        Assert.Equal(RouteMode.ByeDpi, plan.Final);
        Assert.Contains("youtube.com", plan.Direct.Domains);
        Assert.Empty(plan.ByeDpi.Domains);
    }

    [Fact]
    public void Upstream_WithoutProxy_FallsBackWithWarning()
    {
        var s = Settings();
        s.Rules.First(r => r.Id == "preset.telegram").Route = RouteMode.Upstream;
        var plan = RoutePlan.Build(s);
        Assert.Null(plan.UpstreamProxy);
        Assert.Contains("telegram.org", plan.ByeDpi.Domains);
        Assert.NotEmpty(plan.Warnings);
    }

    [Fact]
    public void Upstream_WithProxy_GetsOwnRule()
    {
        var s = Settings();
        s.UpstreamEnabled = true;
        s.UpstreamUri = "socks5://user:p%40ss@10.0.0.5:1081";
        s.Rules.First(r => r.Id == "preset.telegram").Route = RouteMode.Upstream;
        var plan = RoutePlan.Build(s);
        Assert.Equal(new UpstreamProxy(UpstreamKind.Socks5, "10.0.0.5", 1081, "user", "p@ss"), plan.UpstreamProxy);
        Assert.Contains("telegram.org", plan.Upstream.Domains);
        Assert.DoesNotContain("telegram.org", plan.ByeDpi.Domains);

        var json = JsonNode.Parse(SingBoxConfigBuilder.Build(s, plan, windows: true))!;
        var upstream = json["outbounds"]!.AsArray().First(o => (string?)o!["tag"] == "upstream")!;
        Assert.Equal("socks", (string?)upstream["type"]);
        Assert.Equal("p@ss", (string?)upstream["password"]);
    }

    [Fact]
    public void SingBoxConfig_HasLoopProtectionAndSeparateRules()
    {
        var s = Settings();
        var json = JsonNode.Parse(SingBoxConfigBuilder.Build(s, RoutePlan.Build(s), windows: true))!;
        var rules = json["route"]!["rules"]!.AsArray();
        Assert.Equal("sniff", (string?)rules[0]!["action"]);
        Assert.Equal("hijack-dns", (string?)rules[1]!["action"]);
        var self = rules[2]!["process_name"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Contains("ciadpi.exe", self);
        Assert.Contains("sing-box.exe", self);
        Assert.Equal("direct", (string?)rules[2]!["outbound"]);
        // Ни одно правило не смешивает процессы и домены (в sing-box это было бы «И»).
        Assert.DoesNotContain(rules, r => r!["process_name"] != null && r["domain_suffix"] != null);
        Assert.Equal("direct", (string?)json["route"]!["final"]);
        Assert.Equal(1080, (int)json["outbounds"]![1]!["server_port"]!);
    }

    [Fact]
    public void SingBoxConfig_UnixProcessNamesWithoutExe()
    {
        var s = Settings();
        var json = SingBoxConfigBuilder.Build(s, RoutePlan.Build(s), windows: false);
        Assert.DoesNotContain("Telegram.exe", json);
        Assert.Contains("\"telegram-desktop\"", json);
    }

    [Fact]
    public void SingBoxConfig_PassesSingBoxCheck()
    {
        var singBox = Environment.GetEnvironmentVariable("BYEDPI_PC_SINGBOX") ?? FindSingBox();
        if (singBox == null) return; // движок не скачан — проверку пропускаем
        var s = Settings();
        s.UpstreamEnabled = true;
        s.UpstreamUri = "http://1.2.3.4:3128";
        s.Rules.First(r => r.Id == "preset.instagram").Route = RouteMode.Upstream;
        s.Rules.First(r => r.Id == "preset.rutracker").Route = RouteMode.Direct;
        s.CustomDomains.Add("example.org");
        var file = Path.GetTempFileName();
        File.WriteAllText(file, SingBoxConfigBuilder.Build(s, RoutePlan.Build(s)));
        var psi = new ProcessStartInfo(singBox, $"check -c \"{file}\"") { RedirectStandardError = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        File.Delete(file);
        Assert.True(p.ExitCode == 0, err);
    }

    [Fact]
    public void Pac_RoutesByList()
    {
        var plan = RoutePlan.Build(Settings());
        var pac = PacBuilder.Build(plan, 1080);
        Assert.Contains("\"youtube.com\"", pac);
        Assert.Contains("SOCKS5 127.0.0.1:1080", pac);
        Assert.Contains("return \"DIRECT\";\n}", pac.Replace("\r\n", "\n"));
    }

    private static string? FindSingBox()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ByeDpiPc.sln"))) dir = dir.Parent;
        if (dir == null) return null;
        var engine = Path.Combine(dir.FullName, "engine");
        if (!Directory.Exists(engine)) return null;
        return Directory.EnumerateFiles(engine, OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box", SearchOption.AllDirectories).FirstOrDefault();
    }
}
