using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace PulseCore.App.Services;

public sealed record UpdateInfo(
    string LatestVersion,
    string CurrentVersion,
    string ReleaseNotes,
    string DownloadUrl);

/// <summary>
/// Consulta la API pública de GitHub Releases en segundo plano (timeout 5s) para avisar si existe una versión más nueva.
/// </summary>
public sealed class UpdateService
{
    public const string GitHubOwner = "alexlemos2217";
    public const string GitHubRepo = "PulseCore";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static string CurrentVersionString
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "1.7.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", $"PulseCore/{CurrentVersionString}");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()?.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(tag)) return null;

            if (!TryParseSemVer(tag, out var latestVer) || !TryParseSemVer(CurrentVersionString, out var currentVer))
                return null;

            if (latestVer <= currentVer) return null;

            var notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
                ? (b.GetString() ?? "").Trim()
                : "";
            if (notes.Length > 260) notes = notes[..257].TrimEnd() + "…";

            var downloadUrl = root.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String
                ? h.GetString() ?? ""
                : $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest";

            // Preferir instalador .exe adjunto en la release si existe
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var dl = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() ?? "" : "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && dl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = dl;
                        break;
                    }
                }
            }

            return new UpdateInfo(tag.TrimStart('v', 'V'), CurrentVersionString, notes, downloadUrl);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TryParseSemVer(string raw, out Version version)
    {
        var clean = raw.Trim().TrimStart('v', 'V');
        var dash = clean.IndexOf('-');
        if (dash >= 0) clean = clean[..dash];
        return Version.TryParse(clean, out version!);
    }
}
