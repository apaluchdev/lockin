using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LockIn.Remote;

/// <summary>Finds this PC's addresses on the local network, best first.</summary>
public static class NetworkAddresses
{
    public static IReadOnlyList<IPAddress> LocalIPv4()
    {
        var candidates = new List<(IPAddress Address, int Score)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            var looksVirtual = nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                || nic.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase);

            foreach (var unicast in props.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IsLinkLocal(address))
                {
                    continue;
                }

                var score = (hasGateway ? 4 : 0) + (IsPrivate(address) ? 2 : 0) + (looksVirtual ? 0 : 1);
                candidates.Add((address, score));
            }
        }

        return [.. candidates.OrderByDescending(c => c.Score).Select(c => c.Address).Distinct()];
    }

    private static bool IsLinkLocal(IPAddress address) => address.GetAddressBytes() is [169, 254, ..];

    private static bool IsPrivate(IPAddress address) => address.GetAddressBytes() switch
    {
        [10, ..] => true,
        [172, >= 16 and <= 31, ..] => true,
        [192, 168, ..] => true,
        _ => false,
    };
}
