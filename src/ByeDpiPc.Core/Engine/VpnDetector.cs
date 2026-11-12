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
                .Where(HasRoutableAddress)
                .Select(n => n.Name)
                .Distinct()
                .ToList();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>
    /// У настоящего VPN есть обычный адрес. Системные туннели macOS (utun0…utun9: iCloud Private Relay, AirDrop и т.п.)
    /// имеют только link-local IPv6 (fe80::) и не перехватывают трафик. Свой TUN ByeDPI (172.19.0.1) тоже не считается.
    /// </summary>
    internal static bool HasRoutableAddress(NetworkInterface n)
    {
        foreach (var a in n.GetIPProperties().UnicastAddresses)
        {
            var ip = a.Address;
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast) continue;
                return true;
            }
            var b = ip.GetAddressBytes();
            if (b.Length == 4 && b[0] == 169 && b[1] == 254) continue;          // APIPA
            if (b.Length == 4 && b[0] == 172 && b[1] == 19 && b[2] == 0) continue; // собственный TUN
            return true;
        }
        return false;
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
