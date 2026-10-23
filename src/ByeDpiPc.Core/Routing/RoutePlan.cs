using System.Net;
using System.Net.Sockets;

namespace ByeDpiPc.Core.Routing;

/// <summary>Что матчить для одного маршрута.</summary>
public sealed class RouteTargets
{
    public SortedSet<string> Processes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SortedSet<string> Domains { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SortedSet<string> IpCidrs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsEmpty => Processes.Count == 0 && Domains.Count == 0 && IpCidrs.Count == 0;
}

/// <summary>
/// Итоговый план маршрутизации, общий для sing-box и PAC: какие программы/домены/подсети куда идут
/// и что делать со всем остальным.
/// </summary>
public sealed class RoutePlan
{
    public RouteTargets Direct { get; } = new();
    public RouteTargets ByeDpi { get; } = new();
    public RouteTargets Upstream { get; } = new();
    public RouteMode Final { get; init; }
    public UpstreamProxy? UpstreamProxy { get; init; }
    public List<string> Warnings { get; } = new();

    public RouteTargets Get(RouteMode mode) => mode switch
    {
        RouteMode.Direct => Direct,
        RouteMode.Upstream => Upstream,
        _ => ByeDpi,
    };

    public static RoutePlan Build(AppSettings s)
    {
        UpstreamProxy? upstream = null;
        if (s.UpstreamEnabled && !string.IsNullOrWhiteSpace(s.UpstreamUri))
        {
            if (UpstreamProxy.TryParse(s.UpstreamUri, out var parsed, out var error)) upstream = parsed;
            else Log.Warn("Маршруты", "Внешний прокси не используется: " + error);
        }

        var plan = new RoutePlan
        {
            Final = s.Filter == FilterMode.Whitelist ? RouteMode.Direct : RouteMode.ByeDpi,
            UpstreamProxy = upstream,
        };

        var rules = s.Rules.Where(r => r.Enabled).ToList();
        if (s.CustomDomainsEnabled && s.CustomDomains.Count > 0)
            rules.Add(new AppRule { Name = "Мои домены", Domains = s.CustomDomains, Route = s.CustomDomainsRoute });

        foreach (var rule in rules)
        {
            var route = EffectiveRoute(rule.Route, s.Filter);
            if (route == RouteMode.Upstream && upstream == null)
            {
                plan.Warnings.Add($"«{rule.Name}»: внешний прокси не настроен, используется обход DPI.");
                route = RouteMode.ByeDpi;
            }
            var t = plan.Get(route);
            foreach (var p in rule.Processes) AddNonEmpty(t.Processes, p.Trim());
            foreach (var d in rule.Domains)
                if (NormalizeDomain(d) is { } nd) t.Domains.Add(nd);
            foreach (var c in rule.IpCidrs)
                if (NormalizeCidr(c) is { } nc) t.IpCidrs.Add(nc);
        }

        // Явный «Напрямую» важнее всего, затем внешний прокси, затем обход.
        foreach (var d in plan.Direct.Domains) { plan.Upstream.Domains.Remove(d); plan.ByeDpi.Domains.Remove(d); }
        foreach (var d in plan.Upstream.Domains) plan.ByeDpi.Domains.Remove(d);
        return plan;
    }

    /// <summary>
    /// Чёрный список: отмеченные идут напрямую (внешний прокси сохраняется), остальное — через обход.
    /// Белый список и «Нет»: маршрут правила как есть.
    /// </summary>
    public static RouteMode EffectiveRoute(RouteMode ruleRoute, FilterMode filter) =>
        filter == FilterMode.Blacklist && ruleRoute == RouteMode.ByeDpi ? RouteMode.Direct : ruleRoute;

    public static string? NormalizeDomain(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var d = raw.Trim().ToLowerInvariant();
        var hash = d.IndexOf('#');
        if (hash >= 0) d = d[..hash].Trim();
        if (d.StartsWith("http://")) d = d[7..];
        else if (d.StartsWith("https://")) d = d[8..];
        var slash = d.IndexOfAny(new[] { '/', ':' });
        if (slash >= 0) d = d[..slash];
        d = d.TrimStart('*').TrimStart('.').TrimEnd('.');
        if (d.Length == 0 || d.Contains(' ') || !d.Contains('.')) return null;
        foreach (var ch in d)
            if (!(char.IsLetterOrDigit(ch) || ch is '-' or '.' or '_')) return null;
        return d;
    }

    public static string? NormalizeCidr(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var c = raw.Trim();
        var parts = c.Split('/');
        if (!IPAddress.TryParse(parts[0], out var ip)) return null;
        var max = ip.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        if (parts.Length == 1) return $"{ip}/{max}";
        if (parts.Length != 2 || !int.TryParse(parts[1], out var bits) || bits < 0 || bits > max) return null;
        return $"{ip}/{bits}";
    }

    private static void AddNonEmpty(SortedSet<string> set, string value)
    {
        if (value.Length > 0) set.Add(value);
    }
}
