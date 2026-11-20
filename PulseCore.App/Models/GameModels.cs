using System.Text.Json.Serialization;

namespace PulseCore.App.Models;

public sealed record GameProfile(string Name, string Process);

public sealed class GameModeConfig
{
    public bool IsEnabled { get; set; }
    public List<GameProfile> Games { get; set; } = new();
}

public sealed record ActiveGameInfo(
    string Name,
    string Process,
    int Pid,
    DateTimeOffset StartedAt,
    string? PreviousPlanName,
    string? PlanName,
    bool PlanChanged);

public sealed record GameModeState(
    bool IsEnabled,
    IReadOnlyList<GameProfile> Games,
    ActiveGameInfo? Active,
    bool WatcherRunning,
    string ConfigPath);

public sealed record PingTargetResult(
    string Id,
    string Label,
    string Host,
    string? Address,
    string Method,
    int Sent,
    int Received,
    double LossPercent,
    double? AvgMs,
    double? MinMs,
    double? MaxMs,
    double? JitterMs,
    IReadOnlyList<double> SamplesMs,
    string Status);

public sealed record PingBurstResult(IReadOnlyList<PingTargetResult> Targets, long DurationMs);
