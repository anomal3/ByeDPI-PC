using System.Net.NetworkInformation;
using ByeDpiPc.Core.Routing;

namespace ByeDpiPc.Core.Engine;

/// <summary>
/// Ищет другие активные VPN/TUN-адаптеры. Они перехватывают маршрут по умолчанию и часто блокируют
/// трафик в обход себя (kill-switch), поэтому режим TUN рядом с ними не работает.
/// </summary>
public static class VpnDetector
{
    private static readonly string[] Markers =
        { "wireguard", "wintun", "tap-windows", "openvpn", "amnezia", "outline", "tun", "vpn", "hamachi", "zerotier" };

    public static List<string> ActiveVpnAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => !n.Name.Equals(SingBoxConfigBuilder.TunInterfaceName, StringComparison.OrdinalIgnoreCase))
                .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel
                            || Markers.Any(m => n.Description.Contains(m, StringComparison.OrdinalIgnoreCase)
                                                || n.Name.Contains(m, StringComparison.OrdinalIgnoreCase)))
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(n => !new[] { "teredo", "isatap", "6to4", "hyper-v", "virtualbox", "vmware" }
                    .Any(x => n.Description.Contains(x, StringComparison.OrdinalIgnoreCase)))
                .Where(n => n.GetIPProperties().UnicastAddresses.Count > 0)
                .Select(n => n.Name)
                .Distinct()
                .ToList();
        }
        catch
        {
            return new();
        }
    }

    public static string? Warning()
    {
        var list = ActiveVpnAdapters();
        return list.Count == 0
            ? null
            : $"Включён другой VPN ({string.Join(", ", list)}). Он перехватывает трафик и может блокировать обход — " +
              "на время работы ByeDPI выключите его или переключитесь в режим «Только прокси».";
    }
}
