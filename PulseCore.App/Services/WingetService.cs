using System.Diagnostics;
using System.Text;
using PulseCore.App.Models;

namespace PulseCore.App.Services;

/// <summary>
/// Gestor seguro de instalaciones y actualizaciones mediante winget (sin ventanas de consola).
/// </summary>
public sealed class WingetService
{
    // Códigos de salida frecuentes de winget que indican "ya instalado / sin actualizaciones disponibles".
    private const int ExitNoApplicableUpgrade = unchecked((int)0x8A15002B);
    private const int ExitAlreadyInstalled = unchecked((int)0x8A150061);

    private static readonly WingetAppInfo[] Catalog =
    [
        // Gaming
        new("Discord.Discord", "Discord", "Gaming", "Chat de voz, texto y streaming de baja latencia para squads."),
        new("Valve.Steam", "Steam", "Gaming", "Plataforma principal de juegos PC, Workshop y biblioteca."),
        new("EpicGames.EpicGamesLauncher", "Epic Games Launcher", "Gaming", "Cliente oficial para Fortnite, Unreal Engine y juegos gratis."),
        new("OBSProject.OBSStudio", "OBS Studio", "Gaming", "Grabación y transmisión en vivo con aceleración NVENC/AMF."),
        new("Blizzard.BattleNet", "Battle.net", "Gaming", "Launcher oficial de Call of Duty, Overwatch, Diablo y WoW."),

        // Navegadores y Multimedia
        new("Brave.Brave", "Brave Browser", "Navegadores y Multimedia", "Navegador Chromium rápido con bloqueador de anuncios nativo."),
        new("Google.Chrome", "Google Chrome", "Navegadores y Multimedia", "Navegador web rápido con sincronización de cuenta Google."),
        new("Spotify.Spotify", "Spotify", "Navegadores y Multimedia", "Música y podcasts en streaming para acompañar tus sesiones."),
        new("VideoLAN.VLC", "VLC Media Player", "Navegadores y Multimedia", "Reproductor multimedia liviano compatible con casi todo."),

        // Utilidades
        new("7zip.7zip", "7-Zip", "Utilidades", "Compresor y descompresor de archivos ultrarrápido y gratuito."),
        new("Microsoft.VisualStudioCode", "Visual Studio Code", "Utilidades", "Editor de código moderno y ligero de Microsoft."),
        new("Microsoft.PowerToys", "Microsoft PowerToys", "Utilidades", "Suite oficial de utilidades pro para Windows (FancyZones, etc.).")
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsBusy => _gate.CurrentCount == 0;

    public WingetCatalogResult GetCatalog()
    {
        var (available, version) = CheckWinget();
        return new WingetCatalogResult(available, version, IsBusy, Catalog);
    }

    private static (bool Available, string? Version) CheckWinget()
    {
        var r = ProcessRunner.Run("winget", "--version", 6000);
        if (!r.Ok) return (false, null);
        var ver = r.Output.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
        return (true, ver);
    }

    public async Task InstallPackagesAsync(
        List<string> packageIds,
        Action<WingetOutputEvent> onOutput,
        Action<WingetCompletedEvent> onCompleted,
        CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            onCompleted(new WingetCompletedEvent(false, "install", 0, 0, 0,
                "Ya hay una operación de winget en curso. Esperá a que finalice."));
            return;
        }

        try
        {
            var (available, _) = CheckWinget();
            if (!available)
            {
                onOutput(new WingetOutputEvent("winget no está disponible en este equipo. Instalá 'App Installer' desde Microsoft Store.", "err", null, null, null));
                onCompleted(new WingetCompletedEvent(false, "install", 0, 0, 0, "winget no encontrado en el sistema."));
                return;
            }

            // Validar contra el catálogo curado para evitar inyección de argumentos
            var validApps = (packageIds ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id => Catalog.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                .Where(a => a is not null)
                .Cast<WingetAppInfo>()
                .ToList();

            if (validApps.Count == 0)
            {
                onCompleted(new WingetCompletedEvent(false, "install", 0, 0, 0, "No seleccionaste ninguna aplicación válida."));
                return;
            }

            int ok = 0, fail = 0;
            for (var i = 0; i < validApps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var app = validApps[i];
                var step = i + 1;

                onOutput(new WingetOutputEvent(
                    $"[{step}/{validApps.Count}] Instalando {app.Name} ({app.Id})…",
                    "info", step, validApps.Count, app.Id));

                var args = $"install --id {app.Id} --exact --silent --accept-package-agreements --accept-source-agreements";
                var code = await RunStreamingAsync(args, (line, stream) =>
                    onOutput(new WingetOutputEvent(line, stream, step, validApps.Count, app.Id)), ct);

                if (code == 0 || code == ExitNoApplicableUpgrade || code == ExitAlreadyInstalled)
                {
                    ok++;
                    var note = code == 0 ? "instalado correctamente." : "ya estaba instalado y actualizado.";
                    onOutput(new WingetOutputEvent($"✓ {app.Name}: {note}", "ok", step, validApps.Count, app.Id));
                }
                else
                {
                    fail++;
                    onOutput(new WingetOutputEvent($"✕ {app.Name}: finalizó con código {code}.", "err", step, validApps.Count, app.Id));
                }
            }

            var msg = fail == 0
                ? $"Se completaron {ok} de {validApps.Count} aplicaciones correctamente."
                : $"{ok} instaladas/actualizadas, {fail} con error.";
            onCompleted(new WingetCompletedEvent(fail == 0, "install", ok, fail, validApps.Count, msg));
        }
        catch (OperationCanceledException)
        {
            onCompleted(new WingetCompletedEvent(false, "install", 0, 0, 0, "Instalación cancelada."));
        }
        catch (Exception ex)
        {
            onOutput(new WingetOutputEvent($"Error inesperado: {ex.Message}", "err", null, null, null));
            onCompleted(new WingetCompletedEvent(false, "install", 0, 1, 1, ex.Message));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpgradeAllAsync(
        Action<WingetOutputEvent> onOutput,
        Action<WingetCompletedEvent> onCompleted,
        CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            onCompleted(new WingetCompletedEvent(false, "upgrade", 0, 0, 0,
                "Ya hay una operación de winget en curso. Esperá a que finalice."));
            return;
        }

        try
        {
            var (available, _) = CheckWinget();
            if (!available)
            {
                onOutput(new WingetOutputEvent("winget no está disponible en este equipo.", "err", null, null, null));
                onCompleted(new WingetCompletedEvent(false, "upgrade", 0, 0, 0, "winget no encontrado en el sistema."));
                return;
            }

            onOutput(new WingetOutputEvent("Buscando y actualizando todas las aplicaciones instaladas vía winget…", "info", null, null, null));

            var args = "upgrade --all --silent --accept-package-agreements --accept-source-agreements";
            var code = await RunStreamingAsync(args, (line, stream) =>
                onOutput(new WingetOutputEvent(line, stream, null, null, null)), ct);

            var ok = code == 0 || code == ExitNoApplicableUpgrade;
            var msg = code == ExitNoApplicableUpgrade
                ? "Todas tus aplicaciones ya están actualizadas."
                : ok
                    ? "Actualización global de aplicaciones completada."
                    : $"winget finalizó con código {code} (algunas apps pueden requerir cerrarse o actualizarse manualmente).";

            onOutput(new WingetOutputEvent(ok ? $"✓ {msg}" : $"! {msg}", ok ? "ok" : "warn", null, null, null));
            onCompleted(new WingetCompletedEvent(ok, "upgrade", ok ? 1 : 0, ok ? 0 : 1, 1, msg));
        }
        catch (OperationCanceledException)
        {
            onCompleted(new WingetCompletedEvent(false, "upgrade", 0, 0, 0, "Actualización cancelada."));
        }
        catch (Exception ex)
        {
            onOutput(new WingetOutputEvent($"Error inesperado: {ex.Message}", "err", null, null, null));
            onCompleted(new WingetCompletedEvent(false, "upgrade", 0, 1, 1, ex.Message));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<int> RunStreamingAsync(string arguments, Action<string, string> onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("winget", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar winget.");

        var readOut = PumpReaderAsync(proc.StandardOutput, "out", onLine, ct);
        var readErr = PumpReaderAsync(proc.StandardError, "err", onLine, ct);

        await Task.WhenAll(readOut, readErr, proc.WaitForExitAsync(ct));
        return proc.ExitCode;
    }

    private static async Task PumpReaderAsync(System.IO.StreamReader reader, string stream, Action<string, string> onLine, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var raw = await reader.ReadLineAsync(ct);
            if (raw is null) break;
            var clean = CleanProgressNoise(raw);
            if (!string.IsNullOrWhiteSpace(clean))
                onLine(clean, stream);
        }
    }

    /// <summary>Filtra líneas de spinner/barras ASCII repetitivas de winget para mantener la consola legible.</summary>
    private static string CleanProgressNoise(string line)
    {
        var s = line.Trim();
        if (s.Length == 0) return "";
        if (s is "-" or "\\" or "|" or "/") return "";
        if (s.All(c => c is '█' or '▒' or '░' or ' ' or '%' or '.' or '─')) return "";
        return s;
    }
}
