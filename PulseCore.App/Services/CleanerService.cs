using System.Diagnostics;
using System.IO;
using PulseCore.App.Models;

namespace PulseCore.App.Services;

/// <summary>Escaneo (dry run) y limpieza segura de cachés. Omite archivos bloqueados sin fallar.</summary>
public sealed class CleanerService
{
    private sealed record Category(string Id, string Name, Func<string[]> Roots, bool IsDns = false);

    private static readonly Category[] Categories =
    [
        new("windows_temp", "Temporales de Windows", TempRoots),
        new("dx_shaders", "Shaders DirectX", () => [Local("D3DSCache")]),
        new("gpu_cache", "Caché GPU", () =>
        [
            Local(Path.Combine("NVIDIA", "DXCache")),
            Local(Path.Combine("NVIDIA", "GLCache")),
            Local(Path.Combine("AMD", "DxCache"))
        ]),
        new("dns_cache", "Caché DNS", () => [], IsDns: true)
    ];

    private static readonly EnumerationOptions EnumOpts = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint, // nunca seguir enlaces simbólicos/junctions
        ReturnSpecialDirectories = false
    };

    // ---------- API ----------
    public Task<ScanResult> ScanAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var sw = Stopwatch.StartNew();
        var results = new List<CleanCategoryResult>();
        foreach (var cat in Categories)
        {
            ct.ThrowIfCancellationRequested();
            if (cat.IsDns)
            {
                results.Add(new CleanCategoryResult(cat.Id, cat.Name, 0, 0, true));
                continue;
            }

            long bytes = 0;
            var files = 0;
            var exists = false;
            foreach (var root in SafeRoots(cat))
            {
                exists = true;
                var (b, f) = Measure(root, ct);
                bytes += b;
                files += f;
            }
            results.Add(new CleanCategoryResult(cat.Id, cat.Name, ToMb(bytes), files, exists));
        }
        sw.Stop();
        return new ScanResult(results, Math.Round(results.Sum(r => r.SizeMb), 2),
            results.Sum(r => r.Files), sw.ElapsedMilliseconds);
    }, ct);

    public Task<CleanSummary> CleanAsync(string[] categories, IProgress<CleanItemResult>? progress = null,
        CancellationToken ct = default) => Task.Run(() =>
    {
        var sw = Stopwatch.StartNew();
        var wanted = new HashSet<string>(categories ?? [], StringComparer.OrdinalIgnoreCase);
        var items = new List<CleanItemResult>();

        foreach (var cat in Categories.Where(c => wanted.Contains(c.Id)))
        {
            ct.ThrowIfCancellationRequested();
            CleanItemResult item;
            if (cat.IsDns)
            {
                var (ok, msg) = FlushDns();
                item = new CleanItemResult(cat.Id, cat.Name, 0, 0, 0, ok, msg);
            }
            else
            {
                long freed = 0;
                int deleted = 0, skipped = 0;
                foreach (var root in SafeRoots(cat))
                {
                    var (b, d, s) = CleanRoot(root, ct);
                    freed += b; deleted += d; skipped += s;
                }
                var msg = skipped > 0
                    ? $"{deleted} archivos eliminados, {skipped} omitidos (en uso o protegidos)."
                    : $"{deleted} archivos eliminados.";
                item = new CleanItemResult(cat.Id, cat.Name, ToMb(freed), deleted, skipped, true, msg);
            }
            items.Add(item);
            progress?.Report(item);
        }

        sw.Stop();
        return new CleanSummary(items, Math.Round(items.Sum(i => i.FreedMb), 2),
            items.Sum(i => i.FilesDeleted), sw.ElapsedMilliseconds);
    }, ct);

    public (bool Ok, string Message) FlushDns()
    {
        var r = ProcessRunner.Run("ipconfig", "/flushdns", 8000);
        return r.Ok
            ? (true, "Caché DNS vaciada correctamente.")
            : (false, $"No se pudo vaciar la caché DNS. {(string.IsNullOrWhiteSpace(r.Error) ? r.Output.Trim() : r.Error.Trim())}".Trim());
    }

    // ---------- Implementación ----------
    private static string[] TempRoots()
    {
        var roots = new List<string> { Path.GetTempPath() };
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"));
        return roots.ToArray();
    }

    private static string Local(string sub) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), sub);

    private static IEnumerable<string> SafeRoots(Category cat)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in cat.Roots())
        {
            string full;
            try { full = Path.GetFullPath(raw).TrimEnd('\\', '/'); }
            catch (Exception) { continue; }

            if (!IsSafeRoot(full) || !Directory.Exists(full) || !seen.Add(full)) continue;
            yield return full;
        }
    }

    /// <summary>Evita borrar raíces peligrosas si una variable de entorno apunta a un lugar inesperado.</summary>
    private static bool IsSafeRoot(string full)
    {
        if (full.Length < 12) return false;
        var pathRoot = Path.GetPathRoot(full)?.TrimEnd('\\', '/') ?? "";
        if (string.Equals(full, pathRoot, StringComparison.OrdinalIgnoreCase)) return false;

        string[] forbidden =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        ];
        return !forbidden.Any(f => !string.IsNullOrEmpty(f) &&
            string.Equals(full, f.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
    }

    private static (long Bytes, int Files) Measure(string root, CancellationToken ct)
    {
        long bytes = 0;
        var files = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", EnumOpts))
            {
                ct.ThrowIfCancellationRequested();
                try { bytes += new FileInfo(f).Length; files++; }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return (bytes, files);
    }

    private static (long FreedBytes, int Deleted, int Skipped) CleanRoot(string root, CancellationToken ct)
    {
        long freed = 0;
        int deleted = 0, skipped = 0;

        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", EnumOpts))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fi = new FileInfo(f);
                    var len = fi.Length;
                    if (fi.IsReadOnly) fi.IsReadOnly = false;
                    fi.Delete();
                    freed += len;
                    deleted++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    skipped++; // bloqueado, en uso o sin permisos
                }
            }

            // Carpetas vacías (Delete no recursivo: falla de forma segura si no están vacías).
            var dirs = Directory.EnumerateDirectories(root, "*", EnumOpts)
                .OrderByDescending(d => d.Length)
                .ToList();
            foreach (var d in dirs)
            {
                try { Directory.Delete(d, false); }
                catch (Exception) { /* no vacía o en uso */ }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        return (freed, deleted, skipped);
    }

    private static double ToMb(long bytes) => Math.Round(bytes / 1048576.0, 2);
}
