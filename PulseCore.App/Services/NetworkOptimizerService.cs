using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using PulseCore.App.Models;

namespace PulseCore.App.Services;

/// <summary>
/// Red Avanzada: benchmark y cambio real de DNS sobre la interfaz activa (netsh) + test de Packet Loss (20 pings).
/// </summary>
public sealed class NetworkOptimizerService
{
    private sealed record DnsDef(string Id, string Name, string Primary, string Secondary, string Description);

    private static readonly DnsDef[] Providers =
    [
        new("cloudflare", "Cloudflare", "1.1.1.1", "1.0.0.1", "Enfoque en privacidad y mínima latencia global."),
        new("google", "Google DNS", "8.8.8.8", "8.8.4.4", "Alta estabilidad y excelente resolución para CDN."),
        new("quad9", "Quad9", "9.9.9.9", "149.112.112.112", "DNS rápido con bloqueo automático de malware."),
        new("auto", "Automático (ISP / DHCP)", "DHCP", "—", "Restaura los servidores DNS asignados por tu router/ISP.")
    ];

    private static readonly Regex SafeHostRx = new(@"^[A-Za-z0-9.\-]{1,100}$", RegexOptions.Compiled);
    private static readonly HttpClient SpeedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _lossGate = new(1, 1);
    private readonly SemaphoreSlim _speedGate = new(1, 1);

    // ==================== 1. DNS BENCHMARK & SWITCHER ====================
    public DnsStateResult GetDnsState(Dictionary<string, double?>? latencies = null)
    {
        var nic = FindActiveInterface();
        var currentServers = GetCurrentDnsServers(nic);
        var activeId = DetectActiveProviderId(currentServers);
        var currentLabel = currentServers.Count > 0 ? string.Join(", ", currentServers) : "Automático (DHCP)";

        string? bestId = null;
        if (latencies is not null)
        {
            bestId = latencies
                .Where(kv => kv.Value.HasValue)
                .OrderBy(kv => kv.Value!.Value)
                .Select(kv => kv.Key)
                .FirstOrDefault();
        }

        var list = Providers.Select(p =>
        {
            double? ms = latencies is not null && latencies.TryGetValue(p.Id, out var v) ? v : null;
            return new DnsProviderInfo(p.Id, p.Name, p.Primary, p.Secondary, p.Description, ms,
                bestId is not null && p.Id == bestId, p.Id == activeId);
        }).ToList();

        return new DnsStateResult(nic?.Name, currentLabel, list);
    }

    public async Task<DnsStateResult> BenchmarkDnsAsync(CancellationToken ct = default)
    {
        var targets = Providers.Where(p => p.Id != "auto").ToArray();
        var tasks = targets.Select(async p => (p.Id, Ms: await MeasureAvgPingAsync(p.Primary, 3, ct)));
        var results = await Task.WhenAll(tasks);

        var map = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, ms) in results) map[id] = ms;
        return GetDnsState(map);
    }

    public DnsApplyResult ApplyDns(string? providerId)
    {
        providerId = (providerId ?? "").Trim().ToLowerInvariant();
        var def = Providers.FirstOrDefault(p => p.Id == providerId);
        if (def is null)
        {
            var st = GetDnsState();
            return new DnsApplyResult(false, "Proveedor DNS no reconocido.", st.InterfaceName, st.CurrentDns, st.Providers);
        }

        var nic = FindActiveInterface();
        if (nic is null)
        {
            var st = GetDnsState();
            return new DnsApplyResult(false, "No se detectó una interfaz de red activa con salida a Internet.", null, st.CurrentDns, st.Providers);
        }

        var safeIface = nic.Name.Replace("\"", "");
        ProcessRunner.Result r;
        if (def.Id == "auto")
        {
            r = ProcessRunner.Run("netsh", $"interface ip set dns name=\"{safeIface}\" source=dhcp", 10000);
        }
        else
        {
            r = ProcessRunner.Run("netsh", $"interface ip set dns name=\"{safeIface}\" static {def.Primary}", 10000);
            if (r.Ok)
                _ = ProcessRunner.Run("netsh", $"interface ip add dns name=\"{safeIface}\" {def.Secondary} index=2", 10000);
        }

        if (r.Ok) _ = ProcessRunner.Run("ipconfig", "/flushdns", 6000);

        var updated = GetDnsState();
        if (r.Ok)
        {
            var msg = def.Id == "auto"
                ? $"DNS restaurado a Automático (DHCP) en “{nic.Name}”."
                : $"DNS cambiado a {def.Name} ({def.Primary} / {def.Secondary}) en “{nic.Name}”.";
            return new DnsApplyResult(true, msg, updated.InterfaceName, updated.CurrentDns, updated.Providers);
        }

        var err = string.IsNullOrWhiteSpace(r.Error) ? r.Output.Trim() : r.Error.Trim();
        return new DnsApplyResult(false, $"No se pudo aplicar el DNS (requiere Administrador): {err}",
            updated.InterfaceName, updated.CurrentDns, updated.Providers);
    }

    // ==================== 2. TEST DE PACKET LOSS (20 PINGS) ====================
    public async Task<PacketLossResult> RunPacketLossTestAsync(
        string? host,
        Action<PacketLossProgressEvent> onProgress,
        CancellationToken ct = default)
    {
        host = string.IsNullOrWhiteSpace(host) ? "1.1.1.1" : host.Trim();
        if (!SafeHostRx.IsMatch(host)) host = "1.1.1.1";

        if (!await _lossGate.WaitAsync(0, ct))
            return new PacketLossResult(host, 0, 0, 0, 0, null, null, null, null, "Ocupado");

        try
        {
            const int total = 20;
            var rtts = new List<double>();
            int lost = 0;

            using var ping = new Ping();
            var payload = new byte[32];

            for (var i = 1; i <= total; i++)
            {
                ct.ThrowIfCancellationRequested();
                double? lastMs = null;
                try
                {
                    var reply = await ping.SendPingAsync(host, 1200, payload, null);
                    if (reply.Status == IPStatus.Success)
                    {
                        lastMs = reply.RoundtripTime;
                        rtts.Add(reply.RoundtripTime);
                    }
                    else lost++;
                }
                catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException)
                {
                    lost++;
                }

                var lossPct = Math.Round(lost * 100.0 / i, 1);
                onProgress(new PacketLossProgressEvent(i, total, lost, lossPct, lastMs));
                if (i < total) await Task.Delay(200, ct);
            }

            var received = rtts.Count;
            var finalLoss = Math.Round(lost * 100.0 / total, 1);
            double? min = received > 0 ? Math.Round(rtts.Min(), 1) : null;
            double? avg = received > 0 ? Math.Round(rtts.Average(), 1) : null;
            double? max = received > 0 ? Math.Round(rtts.Max(), 1) : null;
            double? jitter = null;
            if (received >= 2)
            {
                double sum = 0;
                for (var i = 1; i < rtts.Count; i++) sum += Math.Abs(rtts[i] - rtts[i - 1]);
                jitter = Math.Round(sum / (rtts.Count - 1), 1);
            }

            var stability = finalLoss == 0 && (jitter ?? 0) < 8 ? "Excelente"
                : finalLoss <= 2 && (jitter ?? 0) < 18 ? "Buena"
                : finalLoss <= 10 ? "Inestable"
                : "Crítica";

            return new PacketLossResult(host, total, received, lost, finalLoss, min, avg, max, jitter, stability);
        }
        finally
        {
            _lossGate.Release();
        }
    }

    // ==================== 3. SPEEDTEST INTEGRADO (CLOUDFLARE) ====================
    public async Task<SpeedTestResult> RunSpeedTestAsync(
        Action<SpeedTestProgressEvent> onProgress,
        CancellationToken ct = default)
    {
        if (!await _speedGate.WaitAsync(0, ct))
            return new SpeedTestResult(false, 0, 0, 0, "Ya hay un Speedtest en curso.");

        var totalSw = Stopwatch.StartNew();
        try
        {
            // 1. Descarga (25 MB)
            const long dlTargetBytes = 25_000_000;
            double downloadMbps = 0;
            onProgress(new SpeedTestProgressEvent("download", 2, 0));

            using (var req = new HttpRequestMessage(HttpMethod.Get, $"https://speed.cloudflare.com/__down?bytes={dlTargetBytes}"))
            {
                req.Headers.TryAddWithoutValidation("User-Agent", "PulseCore/1.8.0");
                using var resp = await SpeedHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var buf = new byte[65536];
                long readTotal = 0;
                var dlSw = Stopwatch.StartNew();
                long lastReportMs = 0;

                while (true)
                {
                    var n = await stream.ReadAsync(buf, ct);
                    if (n <= 0) break;
                    readTotal += n;
                    var elapsedMs = dlSw.ElapsedMilliseconds;
                    if (elapsedMs - lastReportMs >= 180 && elapsedMs > 0)
                    {
                        lastReportMs = elapsedMs;
                        var mbps = Math.Round((readTotal * 8.0) / (elapsedMs / 1000.0) / 1_000_000.0, 1);
                        var pct = Math.Min(50, (readTotal / (double)dlTargetBytes) * 50.0);
                        onProgress(new SpeedTestProgressEvent("download", pct, mbps));
                    }
                }
                dlSw.Stop();
                if (dlSw.Elapsed.TotalSeconds > 0)
                    downloadMbps = Math.Round((readTotal * 8.0) / dlSw.Elapsed.TotalSeconds / 1_000_000.0, 1);
            }
            onProgress(new SpeedTestProgressEvent("download", 50, downloadMbps));

            // 2. Subida (5 MB)
            const int ulBytes = 5 * 1024 * 1024;
            onProgress(new SpeedTestProgressEvent("upload", 55, 0));
            var ulBuf = new byte[ulBytes];
            Random.Shared.NextBytes(ulBuf.AsSpan(0, 4096));

            double uploadMbps = 0;
            var ulSw = Stopwatch.StartNew();
            using (var content = new ByteArrayContent(ulBuf))
            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://speed.cloudflare.com/__up") { Content = content })
            {
                req.Headers.TryAddWithoutValidation("User-Agent", "PulseCore/1.8.0");
                using var resp = await SpeedHttp.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
            }
            ulSw.Stop();
            if (ulSw.Elapsed.TotalSeconds > 0)
                uploadMbps = Math.Round((ulBytes * 8.0) / ulSw.Elapsed.TotalSeconds / 1_000_000.0, 1);

            onProgress(new SpeedTestProgressEvent("upload", 100, uploadMbps));
            totalSw.Stop();

            return new SpeedTestResult(true, downloadMbps, uploadMbps, totalSw.ElapsedMilliseconds,
                $"Descarga {downloadMbps} Mbps · Subida {uploadMbps} Mbps");
        }
        catch (OperationCanceledException)
        {
            return new SpeedTestResult(false, 0, 0, totalSw.ElapsedMilliseconds, "Speedtest cancelado.");
        }
        catch (Exception ex)
        {
            return new SpeedTestResult(false, 0, 0, totalSw.ElapsedMilliseconds, $"Error en Speedtest: {ex.Message}");
        }
        finally
        {
            _speedGate.Release();
        }
    }

    // ---------- Helpers ----------
    private static async Task<double?> MeasureAvgPingAsync(string host, int count, CancellationToken ct)
    {
        var times = new List<double>();
        using var ping = new Ping();
        var payload = new byte[32];
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var r = await ping.SendPingAsync(host, 1200, payload, null);
                if (r.Status == IPStatus.Success) times.Add(r.RoundtripTime);
            }
            catch (Exception) { }
            if (i < count - 1) await Task.Delay(80, ct);
        }
        return times.Count > 0 ? Math.Round(times.Average(), 1) : null;
    }

    private static NetworkInterface? FindActiveInterface()
    {
        try
        {
            var all = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .ToList();

            return all.FirstOrDefault(n =>
                       n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                   ?? all.FirstOrDefault();
        }
        catch (Exception) { return null; }
    }

    private static List<string> GetCurrentDnsServers(NetworkInterface? nic)
    {
        if (nic is null) return [];
        try
        {
            return nic.GetIPProperties().DnsAddresses
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .ToList();
        }
        catch (Exception) { return []; }
    }

    private static string DetectActiveProviderId(List<string> servers)
    {
        var first = servers.FirstOrDefault();
        if (first is null) return "auto";
        if (first is "1.1.1.1" or "1.0.0.1") return "cloudflare";
        if (first is "8.8.8.8" or "8.8.4.4") return "google";
        if (first is "9.9.9.9" or "149.112.112.112") return "quad9";
        return "auto";
    }
}
