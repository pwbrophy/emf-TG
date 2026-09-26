using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using UnityEngine;
using WebSocketSharp.Net.WebSockets;

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

    // WebSocketSharp keeps each accepted connection's TcpClient in a private field
    // of its internal TcpListenerWebSocketContext and never exposes a NoDelay option.
    private static FieldInfo _tcpClientField;
    private static bool _warnedNoTcpField;

    /// <summary>
    /// Turns off Nagle's algorithm on a WebSocketSharp server connection. Call from
    /// WebSocketBehavior.OnOpen.
    ///
    /// With Nagle on, Windows holds each small message (drive/turret ~40 bytes)
    /// until the previous one is ACKed. The ESP32's lwIP stack delays ACKs for up
    /// to ~250 ms, so a 20 Hz drive stream arrived at the robot in bursts, adding
    /// up to a quarter of a second of control lag.
    /// </summary>
    public static void DisableNagle(WebSocketContext context)
    {
        try
        {
            if (_tcpClientField == null && context != null)
                _tcpClientField = context.GetType().GetField("_tcpClient", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_tcpClientField?.GetValue(context) is TcpClient client)
            {
                client.NoDelay = true;
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[Net] Could not set TCP NoDelay: " + ex.Message);
            return;
        }

        if (!_warnedNoTcpField)
        {
            _warnedNoTcpField = true;
            Debug.LogWarning("[Net] websocket-sharp internals changed — TCP NoDelay not set; controls may lag.");
        }
    }
}
