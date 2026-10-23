using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using ByeDpiPc.Core;
using ByeDpiPc.Core.Routing;
using ByeDpiPc.Core.Sharing;
using Xunit;

namespace ByeDpiPc.Core.Tests;

public class ShareAndVlessTests
{
    private const string Link =
        "vless://49959f50-96be-49f3-b023-31dc2e4f2478@203.0.113.10:443?encryption=none&flow=xtls-rprx-vision&security=reality" +
        "&sni=www.microsoft.com&fp=chrome&pbk=SLRh56AUOWqx2LIYunJklMRpOzRaS5QsWYUJ0B9WAy0&sid=b4f1c68d8440ecff&type=tcp#Telegram-Ivan";

    private static AppSettings Settings()
    {
        var s = new AppSettings();
        Presets.EnsureDefaults(s);
        return s;
    }

    [Fact]
    public void Vless_ParsesRealityLink()
    {
        Assert.True(UpstreamProxy.TryParse(Link, out var p, out var err), err);
        Assert.Equal(UpstreamKind.Vless, p!.Kind);
        Assert.Equal("203.0.113.10", p.Host);
        Assert.Equal(443, p.Port);
        Assert.Equal("Telegram-Ivan", p.Label);
        Assert.Equal("reality", p.Vless!.Security);
        Assert.Equal("xtls-rprx-vision", p.Vless.Flow);
        var o = p.ToSingBoxOutbound();
        Assert.Equal("vless", (string?)o["type"]);
        Assert.Equal("SLRh56AUOWqx2LIYunJklMRpOzRaS5QsWYUJ0B9WAy0", (string?)o["tls"]!["reality"]!["public_key"]);
    }

    [Theory]
    [InlineData("vless://not-a-uuid@1.2.3.4:443?security=reality&pbk=x")]
    [InlineData("vless://49959f50-96be-49f3-b023-31dc2e4f2478@1.2.3.4:443?security=reality")]
    [InlineData("vless://49959f50-96be-49f3-b023-31dc2e4f2478@1.2.3.4:443?security=xtls")]
    public void Vless_RejectsBrokenLinks(string link) => Assert.False(UpstreamProxy.TryParse(link, out _, out _));

    [Fact]
    public void Share_RoundTripsThroughCode()
    {
        var s = Settings();
        s.UpstreamEnabled = true;
        s.UpstreamUri = Link;
        s.Rules.First(r => r.Id == "preset.telegram").Route = RouteMode.Upstream;
        var code = ShareBundle.Create(s, "Тест", new ShareBundle.Options { Upstream = true }).ToCode();
        Assert.StartsWith("bdpi:", code);

        var friend = Settings();
        var bundle = ShareBundle.Parse(code);
        Assert.Contains(bundle.Describe(), d => d.Contains("VLESS Reality"));
        bundle.ApplyTo(friend);
        Assert.True(friend.UpstreamEnabled);
        Assert.Equal(RouteMode.Upstream, friend.Rules.First(r => r.Id == "preset.telegram").Route);
        Assert.Equal(s.GetActiveStrategy()!.Arguments, friend.GetActiveStrategy()!.Arguments);
        // Встроенные наборы передаются без списков, у друга остаются его собственные.
        Assert.Contains("telegram.org", friend.Rules.First(r => r.Id == "preset.telegram").Domains);
    }

    [Fact]
    public void Share_AcceptsServerScriptCode()
    {
        // Так код собирает docs/server/add-user.sh: jq → gzip → base64url.
        var json = "{\"format\":\"byedpi-pc-share\",\"version\":1,\"name\":\"Telegram для Ivan\"," +
                   "\"rules\":[{\"Id\":\"preset.telegram\",\"Name\":\"Telegram\",\"Enabled\":true,\"Route\":\"Upstream\",\"IsPreset\":true,\"Processes\":[],\"Domains\":[],\"IpCidrs\":[]}]," +
                   $"\"upstream\":\"{Link}\"}}";
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(Encoding.UTF8.GetBytes(json));
        var code = "bdpi:" + Convert.ToBase64String(ms.ToArray()).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var friend = Settings();
        ShareBundle.Parse(code).ApplyTo(friend);
        Assert.Equal(Link, friend.UpstreamUri);
        Assert.Equal(RouteMode.Upstream, friend.Rules.First(r => r.Id == "preset.telegram").Route);
    }

    [Fact]
    public void Share_BareLinkRoutesTelegram()
    {
        var friend = Settings();
        ShareBundle.Parse(Link).ApplyTo(friend);
        Assert.True(friend.UpstreamEnabled);
        Assert.Equal(RouteMode.Upstream, friend.Rules.First(r => r.Id == "preset.telegram").Route);
    }

    [Fact]
    public void Ssh_ParsesLinkWithCyrillicLabel()
    {
        Assert.True(UpstreamProxy.TryParse("ssh://tun-ivan:Secret123@203.0.113.5:41830#Туннель-Иван", out var p, out var err), err);
        Assert.Equal(UpstreamKind.Ssh, p!.Kind);
        Assert.Equal(41830, p.Port);
        Assert.Equal("tun-ivan", p.User);
        Assert.Equal("Туннель-Иван", p.Label);
        Assert.True(p.NeedsLocalBridge);
        Assert.Equal("ssh", (string?)p.ToSingBoxOutbound()["type"]);
        Assert.False(UpstreamProxy.TryParse("ssh://203.0.113.5:22", out _, out _));
    }

    [Fact]
    public void Share_RejectsGarbage() => Assert.Throws<ByeDpiPc.Core.Engine.UserFacingException>(() => ShareBundle.Parse("bdpi:!!!"));

    [Fact]
    public void VlessConfigs_PassSingBoxCheck()
    {
        var singBox = FindSingBox();
        if (singBox == null) return;
        var s = Settings();
        s.UpstreamEnabled = true;
        s.UpstreamUri = Link;
        s.Rules.First(r => r.Id == "preset.telegram").Route = RouteMode.Upstream;
        var plan = RoutePlan.Build(s);
        Check(singBox, SingBoxConfigBuilder.Build(s, plan));
        Check(singBox, SingBoxConfigBuilder.Build(s, plan.UpstreamOnly()));
        Check(singBox, SingBoxConfigBuilder.BuildLocalBridge(plan.UpstreamProxy!, 20999));
        UpstreamProxy.TryParse("ssh://u:p@203.0.113.5:41830", out var ssh, out _);
        Check(singBox, SingBoxConfigBuilder.BuildLocalBridge(ssh!, 20998));
    }

    private static void Check(string singBox, string config)
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, config);
        var psi = new ProcessStartInfo(singBox, $"check -c \"{file}\"") { RedirectStandardError = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        File.Delete(file);
        Assert.True(p.ExitCode == 0, err + "\n" + config);
    }

    private static string? FindSingBox()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ByeDpiPc.sln"))) dir = dir.Parent;
        var engine = dir == null ? null : Path.Combine(dir.FullName, "engine");
        return engine == null || !Directory.Exists(engine) ? null
            : Directory.EnumerateFiles(engine, OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box", SearchOption.AllDirectories).FirstOrDefault();
    }
}
