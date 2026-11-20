namespace PulseCore.App.Models;

public sealed record CpuInfo(string Name, double UsagePercent, double? TemperatureC, double? AvgFrequencyMhz);

public sealed record GpuInfo(
    string Name,
    double? UsagePercent,
    double? TemperatureC,
    double? VramUsedMb,
    double? VramTotalMb);

public sealed record RamInfo(double UsedGb, double TotalGb, double Percent);

public sealed record StorageInfo(string Drive, double UsedGb, double TotalGb, double Percent);

public sealed record TelemetrySnapshot(
    CpuInfo Cpu,
    GpuInfo Gpu,
    RamInfo Ram,
    StorageInfo Storage,
    DateTimeOffset Timestamp,
    bool IsElevated,
    string Source,
    string? Notice);
