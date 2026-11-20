using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PulseCore.App.Models;

namespace PulseCore.App.Services;

/// <summary>Mide latencia, jitter y pérdida contra servidores clave (ICMP; si no responde, TCP 443).</summary>
public sealed class NetworkPingService
{
    private sealed record Target(string Id, string Label, string Host);

    private static readonly Target[] Targets =
    [
        new("cloudflare", "Cloudflare DNS", "1.1.1.1"),
        new("google", "Google DNS", "8.8.8.8"),
        new("aws_sa", "AWS Sudamérica (São Paulo)", "s3.sa-east-1.amazonaws.com")
    ];

    private const int TimeoutMs = 1500;
    private static readonly byte[] Payload = new byte[32];

    public async Task<PingBurstResult> RunBurstAsync(int samples = 4, CancellationToken ct = default)
    {
        samples = Math.Clamp(samples, 1, 10);
        var sw = Stopwatch.StartNew();
        var results = await Task.WhenAll(Targets.Select(t => MeasureAsync(t, samples, ct)));
        sw.Stop();
        return new PingBurstResult(results, sw.ElapsedMilliseconds);
    }

    private static async Task<PingTargetResult> MeasureAsync(Target t, int samples, CancellationToken ct)
    {
        var times = new List<double>();
        var method = "ICMP";
        string? address = null;

        using (var ping = new Ping())
        {
            for (var i = 0; i < samples; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var reply = await ping.SendPingAsync(t.Host, TimeoutMs, Payload, null);
                    if (reply.Status == IPStatus.Success)
                    {
                        times.Add(reply.RoundtripTime);
                        address ??= reply.Address?.ToString();
                    }
                }
                catch (Exception ex) when (ex is PingException or InvalidOperationException or SocketException) { }
                if (i < samples - 1) await Task.Delay(120, ct);
            }
        }

        // Muchos endpoints de nube bloquean ICMP: medimos el tiempo de conexión TCP al 443.
        if (times.Count == 0)
        {
            var tcp = await MeasureTcpAsync(t.Host, samples, ct);
            if (tcp.Times.Count > 0)
            {
                times = tcp.Times;
                address = tcp.Address;
                method = "TCP 443";
            }
        }

        return Build(t, samples, times, address, method);
    }

    private static async Task<(List<double> Times, string? Address)> MeasureTcpAsync(string host, int samples, CancellationToken ct)
    {
        var times = new List<double>();
        IPAddress? ip;
        try
        {
            var addrs = IPAddress.TryParse(host, out var parsed)
                ? new[] { parsed }
                : await Dns.GetHostAddressesAsync(host, ct);
            ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs.FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return (times, null); }
        if (ip is null) return (times, null);

        for (var i = 0; i < samples; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeoutMs);
            using var client = new TcpClient();
            var sw = Stopwatch.StartNew();
            try
            {
                await client.ConnectAsync(ip, 443, cts.Token);
                sw.Stop();
                times.Add(Math.Round(sw.Elapsed.TotalMilliseconds, 1));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { }
            if (i < samples - 1) await Task.Delay(120, ct);
        }
        return (times, ip.ToString());
    }

    private static PingTargetResult Build(Target t, int sent, List<double> times, string? address, string method)
    {
        var received = times.Count;
        var loss = Math.Round((sent - received) * 100.0 / sent, 1);
        if (received == 0)
            return new PingTargetResult(t.Id, t.Label, t.Host, address, method, sent, 0, loss,
                null, null, null, null, [], "timeout");

        double? jitter = null;
        if (received >= 2)
        {
            double sum = 0;
            for (var i = 1; i < times.Count; i++) sum += Math.Abs(times[i] - times[i - 1]);
            jitter = Math.Round(sum / (times.Count - 1), 1);
        }

        return new PingTargetResult(t.Id, t.Label, t.Host, address, method, sent, received, loss,
            Math.Round(times.Average(), 1), Math.Round(times.Min(), 1), Math.Round(times.Max(), 1), jitter,
            times.Select(x => Math.Round(x, 1)).ToList(), "ok");
    }
}
