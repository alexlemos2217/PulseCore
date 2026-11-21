namespace PulseCore.App.Models;

public sealed record DebloatItem(
    string Id,
    string Name,
    string Description,
    string Level,
    bool IsApplied);

public sealed record DebloatToggleResult(
    bool Success,
    string Message,
    IReadOnlyList<DebloatItem> Items,
    IReadOnlyList<RestoreChangeRecord> History);

public sealed record StartupAppItem(
    string Id,
    string Name,
    string Command,
    string Location,
    bool IsEnabled);

public sealed record StartupToggleResult(
    bool Success,
    string Message,
    IReadOnlyList<StartupAppItem> Apps);

public sealed record RepairOutputEvent(string Tool, string Line, string Stream);

public sealed record RepairCompletedEvent(bool Success, string Tool, int ExitCode, string Message);
