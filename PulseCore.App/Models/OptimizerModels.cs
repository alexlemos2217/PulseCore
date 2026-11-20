namespace PulseCore.App.Models;

public sealed record PowerPlanInfo(string Guid, string Name, bool IsActive, string Kind);

public sealed record PowerPlanResult(
    bool Success,
    string Message,
    string? ActiveGuid,
    string? ActiveName,
    string? PreviousGuid,
    string? PreviousName,
    IReadOnlyList<PowerPlanInfo> Plans);

public sealed record RamOptimizeResult(
    double FreedMb,
    int ProcessesTrimmed,
    long DurationMs,
    double UsedBeforeMb,
    double UsedAfterMb);

public sealed record ProcessMemInfo(string Name, int Pid, double MemoryMb);

public sealed record CleanCategoryResult(string Id, string Name, double SizeMb, int Files, bool Exists);

public sealed record ScanResult(
    IReadOnlyList<CleanCategoryResult> Categories,
    double TotalMb,
    int TotalFiles,
    long DurationMs);

public sealed record CleanItemResult(
    string Id,
    string Name,
    double FreedMb,
    int FilesDeleted,
    int FilesSkipped,
    bool Success,
    string Message);

public sealed record CleanSummary(
    IReadOnlyList<CleanItemResult> Items,
    double TotalFreedMb,
    int TotalFiles,
    long DurationMs);

public sealed record QuickBoostResult(
    bool PlanAvailable,
    bool PlanChanged,
    string PlanName,
    string? PreviousPlanGuid,
    string? PreviousPlanName,
    string PlanMessage,
    RamOptimizeResult Ram,
    bool DnsFlushed,
    string DnsMessage,
    long DurationMs);
