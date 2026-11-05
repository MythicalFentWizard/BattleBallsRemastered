using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace Launcher;

/// Builds a stable fingerprint for this machine so a ban can cover the hardware and not
/// just the account.
///
/// Two deliberate decisions:
///  - The raw components never leave this machine and are never stored anywhere. Only
///    the hash is sent, and only at login.
///  - The hash is NOT salted per master server. A salt would make the value meaningless
///    to anyone importing an exported ban list, which is the whole point of sharing one.
///    The trade-off is that it is a stable cross-server identifier, so treat it as
///    personal data: it identifies a machine across every cluster that sees it.
///
/// Hardware ids are spoofable. This slows down ban evasion; it does not prevent it.
public static class HardwareId
{
    /// Returns the hex SHA-256 of the machine fingerprint, or null if nothing stable
    /// could be read - in which case bans fall back to the account name alone.
    public static string? Compute()
    {
        var parts = new List<string>();

        // Windows installation GUID: stable across reboots, changes on reinstall.
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is string guid && guid.Length > 0)
                parts.Add("guid:" + guid);
        }
        catch { }

        // MAC of the first physical, non-virtual adapter. Loopback and tunnels are
        // skipped, and so are the usual virtual adapters, which otherwise make the
        // fingerprint change when Hyper-V or a VPN is installed.
        try
        {
            var mac = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                                                    or NetworkInterfaceType.Wireless80211)
                .Where(n => !n.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase)
                            && !n.Description.Contains("vmware", StringComparison.OrdinalIgnoreCase)
                            && !n.Description.Contains("hyper-v", StringComparison.OrdinalIgnoreCase))
                .Select(n => n.GetPhysicalAddress().ToString())
                .FirstOrDefault(a => a is { Length: > 0 });
            if (mac is { Length: > 0 }) parts.Add("mac:" + mac);
        }
        catch { }

        if (parts.Count == 0) return null;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
