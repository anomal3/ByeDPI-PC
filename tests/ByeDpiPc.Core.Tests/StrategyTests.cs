using ByeDpiPc.Core;
using ByeDpiPc.Core.Strategy;
using Xunit;

namespace ByeDpiPc.Core.Tests;

public class StrategyTests
{
    [Fact]
    public void Split_HandlesQuotes()
    {
        Assert.Equal(new[] { "-n", "www.iana.org", "-l", ":GET / HTTP" }, CiadpiCommand.Split("-n www.iana.org -l ':GET / HTTP'"));
        Assert.Equal(new[] { "-a", "" }, CiadpiCommand.Split("-a \"\""));
    }

    [Fact]
    public void BuildArguments_ListensOnLoopbackAndStripsUserPort()
    {
        var args = CiadpiCommand.BuildArguments("-p 9999 --ip=0.0.0.0 -o1 -n {sni} -p1234", 1080, "example.org");
        Assert.Equal(new[] { "-i", "127.0.0.1", "-p", "1080", "-o1", "-n", "example.org" }, args);
    }

    [Theory]
    [InlineData("-o1 -a1 -r-5+se")]
    [InlineData("--split 1 --tlsrec 1+s")]
    [InlineData("-d3+s -Mh,d -a2")]
    [InlineData("-f-1 -t6 -n {sni}")]
    public void UiStrategy_RoundTrips(string args)
    {
        var parsed = UiStrategy.TryParse(args);
        Assert.NotNull(parsed);
        var again = UiStrategy.TryParse(parsed!.ToArguments());
        Assert.NotNull(again);
        Assert.Equal(parsed.ToArguments(), again!.ToArguments());
    }

    [Fact]
    public void UiStrategy_ReadsTvStrategy()
    {
        var s = UiStrategy.TryParse("-o1 -a1 -r-5+se")!;
        Assert.Equal(DesyncMethod.Oob, s.Method);
        Assert.Equal(1, s.SplitPosition);
        Assert.True(s.TlsRec);
        Assert.Equal(-5, s.TlsRecPosition);
        Assert.True(s.TlsRecFromEnd);
        Assert.Equal(1, s.UdpFakeCount);
    }

    [Theory]
    [InlineData("-o1 -a1 -At,r,s -f-1 -a1")]
    [InlineData("-s1 -s5")]
    [InlineData("--unknown 1")]
    public void UiStrategy_RejectsComplex(string args) => Assert.Null(UiStrategy.TryParse(args));

    [Fact]
    public void Presets_AreLoaded()
    {
        var settings = new AppSettings();
        Presets.EnsureDefaults(settings);
        Assert.Contains(settings.Rules, r => r.Id == "preset.telegram" && r.Enabled);
        Assert.Contains(settings.Rules, r => r.Id == "preset.metaquest" && r.Enabled);
        Assert.Equal("Универсальная", settings.ActiveStrategy);
        Assert.True(Presets.LoadTestStrategies().Count >= 50);
        Assert.NotEmpty(settings.TestSites);
    }

    [Theory]
    [InlineData("rr1---sn-5hne6nzk.googlevideo.com", true)]
    [InlineData("https://rr3---sn-pivhx-n8vs.googlevideo.com/", true)]
    [InlineData("manifest.googlevideo.com", false)]
    [InlineData("youtube.com", false)]
    [InlineData("", false)]
    public void CurlProbe_RecognizesVideoNodes(string site, bool expected) =>
        Assert.Equal(expected, ByeDpiPc.Core.Testing.CurlProbe.IsVideoNode(site));

    [Fact]
    public void Presets_AddVideoNodeToExistingTestSites()
    {
        var settings = new AppSettings { SchemaVersion = 2, TestSites = { "youtube.com" } };
        Presets.EnsureDefaults(settings);
        Assert.Contains(settings.TestSites, ByeDpiPc.Core.Testing.CurlProbe.IsVideoNode);
        Assert.Equal(4, settings.SchemaVersion);
    }

    [Fact]
    public void Candidates_StartWithProvenStrategies()
    {
        var settings = new AppSettings();
        Presets.EnsureDefaults(settings);
        var list = Presets.Candidates(settings, EngineKind.ByeDpi);
        Assert.Equal(Presets.ProvenStrategies[0], list[0]);
        Assert.Equal(list.Count, list.Distinct().Count());
    }

    [Fact]
    public void ProvenStrategies_PassThroughArgumentBuilder()
    {
        foreach (var s in Presets.ProvenStrategies)
        {
            var args = CiadpiCommand.BuildArguments(s, 1080, "www.iana.org");
            Assert.Equal(new[] { "-i", "127.0.0.1", "-p", "1080" }, args.Take(4));
            Assert.Contains("-o1", args);
            if (s.StartsWith("-H")) Assert.Equal(new[] { "-H", ":googleapis.com" }, args.Skip(4).Take(2));
        }
    }

    [Theory]
    [InlineData("youtube.com", true)]
    [InlineData("youtubei.googleapis.com", true)]
    [InlineData("rr1---sn-5hne6nzk.googlevideo.com", true)]
    [InlineData("discord.com", false)]
    [InlineData("i.ytimg.com", false)]
    public void CurlProbe_CriticalSites(string site, bool expected) =>
        Assert.Equal(expected, ByeDpiPc.Core.Testing.CurlProbe.IsCritical(site));

    [Fact]
    public void Presets_RespectRemoved()
    {
        var settings = new AppSettings { RemovedPresets = { "preset.x" } };
        Presets.EnsureDefaults(settings);
        Assert.DoesNotContain(settings.Rules, r => r.Id == "preset.x");
    }
}
