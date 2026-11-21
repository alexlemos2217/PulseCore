using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PulseCore.App.Models;

namespace PulseCore.App.Services;

/// <summary>
/// Detecta juegos instalados en Steam y Epic Games leyendo únicamente manifiestos locales.
/// Nunca lanza excepciones si alguna plataforma no está instalada.
/// </summary>
public sealed class GameDiscoveryService
{
    private static readonly Regex VdfKvRx = new(@"""([^""]+)""\s+""([^""]*)""", RegexOptions.Compiled);

    private static readonly string[] IgnoredSteamKeywords =
    [
        "Steamworks Common Redistributables",
        "Steam Linux Runtime",
        "Proton ",
        "Proton Experimental",
        "Proton Hotfix",
        "Dedicated Server",
        "Soundtrack",
        "SDK",
        "DirectX"
    ];

    public IReadOnlyList<InstalledGameInfo> ScanInstalledGames()
    {
        var results = new List<InstalledGameInfo>();
        try { results.AddRange(ScanSteam()); } catch (Exception) { }
        try { results.AddRange(ScanEpic()); } catch (Exception) { }

        return results
            .GroupBy(g => $"{g.Platform}|{g.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------- STEAM ----------
    private static IEnumerable<InstalledGameInfo> ScanSteam()
    {
        var steamPath = GetSteamInstallPath();
        if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
            yield break;

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(steamPath) };
        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdfPath))
        {
            try
            {
                var content = File.ReadAllText(vdfPath);
                foreach (Match m in VdfKvRx.Matches(content))
                {
                    if (!m.Groups[1].Value.Equals("path", StringComparison.OrdinalIgnoreCase)) continue;
                    var raw = m.Groups[2].Value.Replace(@"\\", @"\").Trim();
                    if (!string.IsNullOrWhiteSpace(raw) && Directory.Exists(raw))
                        libraries.Add(Path.GetFullPath(raw));
                }
            }
            catch (Exception) { }
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lib in libraries)
        {
            var steamApps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(steamApps)) continue;

            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"); }
            catch (Exception) { continue; }

            foreach (var file in manifests)
            {
                InstalledGameInfo? info = null;
                try
                {
                    var text = File.ReadAllText(file);
                    string? appId = null, name = null, installDir = null;
                    foreach (Match m in VdfKvRx.Matches(text))
                    {
                        var k = m.Groups[1].Value;
                        var v = m.Groups[2].Value;
                        if (appId is null && k.Equals("appid", StringComparison.OrdinalIgnoreCase)) appId = v;
                        else if (name is null && k.Equals("name", StringComparison.OrdinalIgnoreCase)) name = v;
                        else if (installDir is null && k.Equals("installdir", StringComparison.OrdinalIgnoreCase)) installDir = v;
                        if (appId is not null && name is not null && installDir is not null) break;
                    }

                    if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(name)) continue;
                    if (appId == "228980" || IsIgnoredSteamApp(name)) continue;
                    if (!seenIds.Add(appId)) continue;

                    var fullInstall = string.IsNullOrWhiteSpace(installDir)
                        ? steamApps
                        : Path.Combine(steamApps, "common", installDir);
                    var iconUrl = ResolveSteamIcon(steamPath, appId);
                    info = new InstalledGameInfo(name.Trim(), "Steam", $"steam://rungameid/{appId}", fullInstall, iconUrl);
                }
                catch (Exception) { }

                if (info is not null) yield return info;
            }
        }
    }

    private static string? GetSteamInstallPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var regPath = key?.GetValue("SteamPath") as string ?? key?.GetValue("SteamExe") as string;
            if (!string.IsNullOrWhiteSpace(regPath))
            {
                var normalized = regPath.Replace('/', '\\');
                if (File.Exists(normalized)) normalized = Path.GetDirectoryName(normalized)!;
                if (Directory.Exists(normalized)) return normalized;
            }
        }
        catch (Exception) { }

        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }

    private static bool IsIgnoredSteamApp(string name) =>
        IgnoredSteamKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

    // ---------- EPIC GAMES ----------
    private static IEnumerable<InstalledGameInfo> ScanEpic()
    {
        var manifestsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestsDir)) yield break;

        IEnumerable<string> items;
        try { items = Directory.EnumerateFiles(manifestsDir, "*.item"); }
        catch (Exception) { yield break; }

        foreach (var file in items)
        {
            InstalledGameInfo? info = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                if (root.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True)
                    continue;

                var displayName = Str(root, "DisplayName");
                var ns = Str(root, "CatalogNamespace");
                var itemId = Str(root, "CatalogItemId");
                var appName = Str(root, "AppName");
                var mainApp = Str(root, "MainGameAppName");
                var installLoc = Str(root, "InstallLocation") ?? "";
                var launchExe = Str(root, "LaunchExecutable");

                // Omitir DLCs o módulos que no son el juego principal
                if (!string.IsNullOrWhiteSpace(mainApp) && !string.IsNullOrWhiteSpace(appName)
                    && !mainApp.Equals(appName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(ns)
                    || string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(appName))
                    continue;

                var uri = $"com.epicgames.launcher://apps/{Uri.EscapeDataString(ns)}%3A{Uri.EscapeDataString(itemId)}%3A{Uri.EscapeDataString(appName)}?action=launch&silent=true";
                var iconUrl = ExtractEpicIconDataUrl(installLoc, launchExe);
                info = new InstalledGameInfo(displayName.Trim(), "Epic", uri, installLoc, iconUrl);
            }
            catch (Exception) { }

            if (info is not null) yield return info;
        }
    }

    private static string ResolveSteamIcon(string steamPath, string appId)
    {
        try
        {
            var cacheDir = Path.Combine(steamPath, "appcache", "librarycache");
            string[] candidates =
            [
                Path.Combine(cacheDir, $"{appId}_icon.jpg"),
                Path.Combine(cacheDir, appId, "icon.jpg"),
                Path.Combine(cacheDir, $"{appId}_library_600x900.jpg"),
                Path.Combine(cacheDir, appId, "library_600x900.jpg")
            ];
            foreach (var file in candidates)
            {
                if (!File.Exists(file)) continue;
                var bytes = File.ReadAllBytes(file);
                if (bytes.Length is > 0 and < 512_000)
                    return "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
            }
        }
        catch (Exception) { }

        return $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/capsule_184x69.jpg";
    }

    private static string? ExtractEpicIconDataUrl(string installLoc, string? launchExe)
    {
        if (string.IsNullOrWhiteSpace(installLoc) || string.IsNullOrWhiteSpace(launchExe)) return null;
        return ExtractExeIconDataUrl(Path.Combine(installLoc, launchExe.Replace('/', '\\')));
    }

    public static string? ExtractExeIconDataUrl(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        try
        {
            if (!File.Exists(exePath)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon is null) return null;
            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception) { return null; }
    }

    private static string? Str(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
