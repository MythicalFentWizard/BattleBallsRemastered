using System.Net.Http.Json;

namespace MasterServer;

/// What the master server answers a login with. Lives next to the endpoint that
/// produces it so both ends cannot drift apart.
public sealed record LoginResult(
    bool Ok,
    string? Error = null,
    string? Name = null,
    string? Session = null,
    string? Cluster = null);

/// Outcome of a guard report. The master decides whether it counts and whether it
/// crosses the ban threshold; the launcher only relays.
public sealed record ReportResult(
    bool Ok,
    bool Counted = false,
    int Count = 0,
    bool Banned = false,
    string? Error = null);

/// The launcher's side of the master server's own (non-game) endpoints.
public static class MasterClient
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// Log in to a master server, creating the account if this is the first time this
    /// name has been used there. The password never goes to disk on either side: the
    /// master keeps only a PBKDF2 hash.
    public static async Task<LoginResult> LoginAsync(
        MasterConfig cfg, string name, string password,
        string? hardwareId = null, bool guard = false, CancellationToken ct = default)
    {
        var url = $"http://{cfg.MasterHost}:{cfg.HttpPort}/_revival/login";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { name, password, hardwareId, guard }),
            };
            if (cfg.Password.Length > 0)
                request.Headers.Add("X-Revival-Auth", cfg.Password);

            using var response = await Http.SendAsync(request, ct);

            // A cluster-password failure is rejected by the middleware before the login
            // endpoint runs, so it comes back as a status code rather than JSON.
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return new LoginResult(false, "Wrong cluster password for that master server.");

            var result = await response.Content.ReadFromJsonAsync<LoginResult>(cancellationToken: ct);
            return result ?? new LoginResult(false, "The master server sent an empty reply.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LoginResult(false, $"The master server at {cfg.MasterHost} did not answer in time.");
        }
        catch (Exception ex)
        {
            return new LoginResult(false, $"Could not reach the master server at {cfg.MasterHost}:{cfg.HttpPort} - {ex.Message}");
        }
    }

    /// Send one guard finding to the master server. Failures are non-fatal: a report
    /// that does not arrive means a detection is missed, not that the game should stop.
    public static async Task<ReportResult> ReportAsync(
        MasterConfig cfg, string session, string signature, string detail,
        CancellationToken ct = default)
    {
        var url = $"http://{cfg.MasterHost}:{cfg.HttpPort}/_revival/guard/report";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { session, signature, detail }),
            };
            if (cfg.Password.Length > 0)
                request.Headers.Add("X-Revival-Auth", cfg.Password);

            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return new ReportResult(false, Error: $"master replied {(int)response.StatusCode}");

            return await response.Content.ReadFromJsonAsync<ReportResult>(cancellationToken: ct)
                   ?? new ReportResult(false, Error: "empty reply");
        }
        catch (Exception ex) { return new ReportResult(false, Error: ex.Message); }
    }
}
