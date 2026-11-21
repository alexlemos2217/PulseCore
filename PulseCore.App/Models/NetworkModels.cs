namespace PulseCore.App.Models;

public sealed record DnsProviderInfo(
    string Id,
    string Name,
    string Primary,
    string Secondary,
    string Description,
    double? LatencyMs,
    bool IsRecommended,
    bool IsActive);

public sealed record DnsStateResult(
    string? InterfaceName,
    string CurrentDns,
    IReadOnlyList<DnsProviderInfo> Providers);

public sealed record DnsApplyResult(
    bool Success,
    string Message,
    string? InterfaceName,
    string CurrentDns,
    IReadOnlyList<DnsProviderInfo> Providers);

public sealed record PacketLossProgressEvent(
    int Sent,
    int Total,
    int Lost,
    double LossPercent,
    double? LastMs);

public sealed record PacketLossResult(
    string Host,
    int Sent,
    int Received,
    int Lost,
    double LossPercent,
    double? MinMs,
    double? AvgMs,
    double? MaxMs,
    double? JitterMs,
    string Stability);
