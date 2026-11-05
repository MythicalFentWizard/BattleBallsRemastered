using System.Net;
using System.Net.Sockets;
using System.Text;
using MasterServer;

namespace Launcher;

/// The game binary has 127.0.0.1:29300 baked in, so a remote master is only reachable
/// by listening on loopback here and relaying onward, attaching the cluster password.
public class LocalProxy
{
    readonly MasterConfig _cfg;
    readonly Action<string> _log;
    CancellationTokenSource? _cts;

    /// Handed out by the master server at login. Attached to every relayed request so
    /// the master knows which account this player is, rather than taking a name on
    /// trust.
    public string? SessionToken { get; init; }

    public LocalProxy(MasterConfig cfg, Action<string> log)
    {
        _cfg = cfg;
        _log = log;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Listen(_cfg.HttpPort, RelayHttp, _cts.Token);
        _ = Listen(_cfg.RegistryPort, RelayRegistry, _cts.Token);
        _log($"Proxy listening on 127.0.0.1:{_cfg.HttpPort} and :{_cfg.RegistryPort}");
        _log($"Forwarding to {_cfg.MasterHost}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _log("Proxy stopped");
    }

    async Task Listen(int port, Func<TcpClient, Task> handler, CancellationToken ct)
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
        }
        catch (SocketException ex)
        {
            _log($"Cannot listen on {port}: {ex.Message}");
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = SafeHandle(client, handler);
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    async Task SafeHandle(TcpClient client, Func<TcpClient, Task> handler)
    {
        try { await handler(client); }
        catch (Exception ex) { _log($"Proxy relay error: {ex.GetType().Name}: {ex.Message}"); }
        finally { client.Close(); }
    }

    // ==================== Registry port ====================

    async Task RelayRegistry(TcpClient local)
    {
        local.NoDelay = true;
        using var remote = new TcpClient();
        await remote.ConnectAsync(_cfg.MasterHost, _cfg.RegistryPort);
        remote.NoDelay = true;

        var rs = remote.GetStream();
        await rs.WriteAsync(Encoding.ASCII.GetBytes(
            $"{MasterServerHost.AuthMagic}\n{_cfg.Password}\n"));
        await rs.FlushAsync();

        var reply = new byte[8];
        int n = await rs.ReadAsync(reply);
        var text = Encoding.ASCII.GetString(reply, 0, Math.Max(n, 0));
        if (!text.StartsWith("OK", StringComparison.Ordinal))
        {
            _log("Master server rejected the cluster password.");
            return;
        }

        var ls = local.GetStream();
        await Task.WhenAny(ls.CopyToAsync(rs), rs.CopyToAsync(ls));
    }

    // ==================== HTTP port ====================

    async Task RelayHttp(TcpClient local)
    {
        local.NoDelay = true;
        var ls = local.GetStream();

        var head = await ReadHeaders(ls);
        if (head == null) return;

        int contentLength = 0;
        var kept = new List<string>();
        var lines = head.Split("\r\n", StringSplitOptions.None);

        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line[15..].Trim(), out contentLength);
            // Replaced below: the response is piped until close, so keep-alive must be off.
            if (line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("X-Revival-Auth:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("X-Revival-Player:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("X-Revival-Session:", StringComparison.OrdinalIgnoreCase)) continue;
            kept.Add(line);
        }

        kept.Add("Connection: close");
        if (_cfg.Password.Length > 0)
            kept.Add($"X-Revival-Auth: {_cfg.Password}");
        if (_cfg.PlayerName.Length > 0)
            kept.Add($"X-Revival-Player: {_cfg.PlayerName}");
        if (SessionToken is { Length: > 0 })
            kept.Add($"X-Revival-Session: {SessionToken}");

        using var remote = new TcpClient();
        await remote.ConnectAsync(_cfg.MasterHost, _cfg.HttpPort);
        remote.NoDelay = true;
        var rs = remote.GetStream();

        await rs.WriteAsync(Encoding.ASCII.GetBytes(string.Join("\r\n", kept) + "\r\n\r\n"));

        if (contentLength > 0)
        {
            var body = new byte[contentLength];
            int got = 0;
            while (got < contentLength)
            {
                int r = await ls.ReadAsync(body.AsMemory(got, contentLength - got));
                if (r == 0) break;
                got += r;
            }
            await rs.WriteAsync(body.AsMemory(0, got));
        }
        await rs.FlushAsync();

        await rs.CopyToAsync(ls);
    }

    static async Task<string?> ReadHeaders(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (sb.Length < 16384)
        {
            if (await stream.ReadAsync(one, 0, 1) == 0) return null;
            sb.Append((char)one[0]);
            if (sb.Length >= 4 &&
                sb[^4] == '\r' && sb[^3] == '\n' && sb[^2] == '\r' && sb[^1] == '\n')
                return sb.ToString(0, sb.Length - 4);
        }
        return sb.ToString();
    }
}
