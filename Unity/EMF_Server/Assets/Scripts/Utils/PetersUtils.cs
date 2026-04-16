using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

public static class PetersUtils
{
    /// <summary>
    /// Returns the first active LAN IPv4 address (Ethernet or Wi-Fi).
    /// Avoids loopback and virtual/VPN adapters.
    /// Falls back to loopback if nothing suitable is found.
    /// </summary>
    public static IPAddress GetLocalIPAddress()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            // Skip adapters that are not up
            if (ni.OperationalStatus != OperationalStatus.Up) continue;

            // Skip loopback
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            // Only consider physical Ethernet or Wi-Fi
            if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    return addr.Address;
            }
        }

        return IPAddress.Loopback;
    }
}
