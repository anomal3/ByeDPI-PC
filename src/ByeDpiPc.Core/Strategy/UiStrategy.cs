using System.Globalization;
using System.Text.RegularExpressions;

namespace ByeDpiPc.Core.Strategy;

public enum DesyncMethod { None, Split, Disorder, Fake, Oob, DisOob }

/// <summary>
/// «Простой режим» редактора: основные параметры byedpi галочками и полями.
/// По смыслу повторяет UISettings из ByeByeDPI:
/// https://github.com/romanvht/ByeByeDPI/blob/master/app/src/main/java/io/github/romanvht/byedpi/data/UISettings.kt
/// </summary>
public sealed partial class UiStrategy
{
    public DesyncMethod Method { get; set; } = DesyncMethod.Oob;
    public int SplitPosition { get; set; } = 1;
    public bool SplitAtSni { get; set; }
    public int FakeTtl { get; set; } = 8;
    public bool UseFakeSni { get; set; }
    public string OobChar { get; set; } = "a";
    public bool HostMixedCase { get; set; }
    public bool DomainMixedCase { get; set; }
    public bool HostRemoveSpaces { get; set; }
    public bool TlsRec { get; set; } = true;
    public int TlsRecPosition { get; set; } = -5;
    public bool TlsRecAtSni { get; set; } = true;
    public bool TlsRecFromEnd { get; set; } = true;
    public int UdpFakeCount { get; set; } = 1;
    public bool DesyncHttp { get; set; } = true;
    public bool DesyncHttps { get; set; } = true;
    public bool DesyncUdp { get; set; } = true;
    public int DefaultTtl { get; set; }
    /// <summary>Применять запутывание только при признаках блокировки (--auto=torst).</summary>
    public bool AutoOnBlock { get; set; }

    public string ToArguments()
    {
        var a = new List<string>();
        if (DefaultTtl > 0) a.Add($"-g{DefaultTtl}");

        if (AutoOnBlock) a.Add("-At,r,s");

        var protos = new List<string>();
        if (DesyncHttp) protos.Add("h");
        if (DesyncHttps) protos.Add("t");
        if (DesyncUdp) protos.Add("u");
        if (protos.Count is > 0 and < 3) a.Add("-K" + string.Join(',', protos));

        var pos = SplitPosition.ToString(CultureInfo.InvariantCulture) + (SplitAtSni ? "+s" : "");
        switch (Method)
        {
            case DesyncMethod.Split: a.Add("-s" + pos); break;
            case DesyncMethod.Disorder: a.Add("-d" + pos); break;
            case DesyncMethod.Fake: a.Add("-f" + pos); break;
            case DesyncMethod.Oob: a.Add("-o" + pos); break;
            case DesyncMethod.DisOob: a.Add("-q" + pos); break;
        }
        if (Method == DesyncMethod.Fake)
        {
            if (FakeTtl > 0 && FakeTtl != 8) a.Add($"-t{FakeTtl}");
            if (UseFakeSni) { a.Add("-n"); a.Add("{sni}"); }
        }
        if (Method is DesyncMethod.Oob or DesyncMethod.DisOob && !string.IsNullOrEmpty(OobChar) && OobChar != "a")
        {
            a.Add("-e"); a.Add(OobChar);
        }

        var mods = new List<string>();
        if (HostMixedCase) mods.Add("h");
        if (DomainMixedCase) mods.Add("d");
        if (HostRemoveSpaces) mods.Add("r");
        if (mods.Count > 0) a.Add("-M" + string.Join(',', mods));

        if (TlsRec)
        {
            var flag = TlsRecAtSni ? (TlsRecFromEnd ? "+se" : "+s") : "";
            a.Add("-r" + TlsRecPosition.ToString(CultureInfo.InvariantCulture) + flag);
        }
        if (UdpFakeCount > 0 && DesyncUdp) a.Add($"-a{UdpFakeCount}");
        return CiadpiCommand.Join(a);
    }

    /// <summary>
    /// Пытается прочитать простую стратегию обратно в форму. Сложные стратегии (группы --auto,
    /// несколько разбиений и т.п.) не разбираются — их редактируют в режиме командной строки.
    /// </summary>
    public static UiStrategy? TryParse(string args)
    {
        var tokens = CiadpiCommand.Split(args);
        var s = new UiStrategy { Method = DesyncMethod.None, TlsRec = false, UdpFakeCount = 0, DesyncUdp = true };
        bool methodSet = false;
        for (int i = 0; i < tokens.Count; i++)
        {
            var (name, value) = SplitOption(tokens, ref i);
            if (name == null) return null;
            switch (name)
            {
                case "-s" or "--split" or "-d" or "--disorder" or "-f" or "--fake" or "-o" or "--oob" or "-q" or "--disoob":
                    if (methodSet || value == null) return null;
                    var m = PosRegex().Match(value);
                    if (!m.Success || m.Groups["flag"].Value is not ("" or "+s")) return null;
                    s.SplitPosition = int.Parse(m.Groups["pos"].Value, CultureInfo.InvariantCulture);
                    s.SplitAtSni = m.Groups["flag"].Value == "+s";
                    s.Method = name switch
                    {
                        "-s" or "--split" => DesyncMethod.Split,
                        "-d" or "--disorder" => DesyncMethod.Disorder,
                        "-f" or "--fake" => DesyncMethod.Fake,
                        "-o" or "--oob" => DesyncMethod.Oob,
                        _ => DesyncMethod.DisOob,
                    };
                    methodSet = true;
                    break;
                case "-t" or "--ttl":
                    if (!int.TryParse(value, out var ttl)) return null;
                    s.FakeTtl = ttl;
                    break;
                case "-n" or "--fake-sni":
                    s.UseFakeSni = true;
                    break;
                case "-e" or "--oob-data":
                    s.OobChar = value ?? "a";
                    break;
                case "-M" or "--mod-http":
                    foreach (var part in (value ?? "").Split(','))
                    {
                        switch (part.Trim())
                        {
                            case "h" or "hcsmix": s.HostMixedCase = true; break;
                            case "d" or "dcsmix": s.DomainMixedCase = true; break;
                            case "r" or "rmspace": s.HostRemoveSpaces = true; break;
                            default: return null;
                        }
                    }
                    break;
                case "-r" or "--tlsrec":
                    if (s.TlsRec || value == null) return null;
                    var r = PosRegex().Match(value);
                    if (!r.Success || r.Groups["flag"].Value is not ("" or "+s" or "+se")) return null;
                    s.TlsRec = true;
                    s.TlsRecPosition = int.Parse(r.Groups["pos"].Value, CultureInfo.InvariantCulture);
                    s.TlsRecAtSni = r.Groups["flag"].Value.StartsWith("+s");
                    s.TlsRecFromEnd = r.Groups["flag"].Value == "+se";
                    break;
                case "-a" or "--udp-fake":
                    if (!int.TryParse(value, out var udp)) return null;
                    s.UdpFakeCount = udp;
                    break;
                case "-g" or "--def-ttl":
                    if (!int.TryParse(value, out var dttl)) return null;
                    s.DefaultTtl = dttl;
                    break;
                case "-K" or "--proto":
                    var p = (value ?? "").Split(',').Select(x => x.Trim()).ToHashSet();
                    s.DesyncHttp = p.Contains("h") || p.Contains("http");
                    s.DesyncHttps = p.Contains("t") || p.Contains("tls");
                    s.DesyncUdp = p.Contains("u") || p.Contains("udp");
                    break;
                case "-A" or "--auto":
                    if (methodSet || s.TlsRec || s.AutoOnBlock) return null; // группы после --auto форма не умеет
                    s.AutoOnBlock = true;
                    break;
                default:
                    return null;
            }
        }
        return s;
    }

    private static readonly HashSet<string> ValueOptions = new()
    {
        "-s", "--split", "-d", "--disorder", "-f", "--fake", "-o", "--oob", "-q", "--disoob", "-t", "--ttl",
        "-n", "--fake-sni", "-e", "--oob-data", "-M", "--mod-http", "-r", "--tlsrec", "-a", "--udp-fake",
        "-g", "--def-ttl", "-K", "--proto", "-A", "--auto",
    };

    /// <summary>Разбирает «-s1», «-s 1», «--split=1», «--split 1».</summary>
    private static (string? Name, string? Value) SplitOption(List<string> tokens, ref int i)
    {
        var t = tokens[i];
        if (t.StartsWith("--"))
        {
            var eq = t.IndexOf('=');
            if (eq > 0) return (t[..eq], t[(eq + 1)..]);
            if (!ValueOptions.Contains(t)) return (null, null);
            return (t, ++i < tokens.Count ? tokens[i] : null);
        }
        if (t.Length < 2 || t[0] != '-') return (null, null);
        var name = t[..2];
        if (!ValueOptions.Contains(name)) return (null, null);
        if (t.Length > 2) return (name, t[2..]);
        return (name, ++i < tokens.Count ? tokens[i] : null);
    }

    [GeneratedRegex(@"^(?<pos>-?\d+)(?<flag>\+[a-z]+)?$")]
    private static partial Regex PosRegex();
}
