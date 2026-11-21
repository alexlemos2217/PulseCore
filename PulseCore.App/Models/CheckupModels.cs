namespace PulseCore.App.Models;

public sealed record CheckupItem(
    string Id,
    string Title,
    string Description,
    string Status,
    string CurrentValue,
    bool CanAutoFix);

public sealed record CheckupReport(
    IReadOnlyList<CheckupItem> Items,
    int Score,
    DateTimeOffset Timestamp);

public sealed record RestoreChangeRecord(
    string ChangeId,
    string ItemId,
    string SettingName,
    string PreviousValue,
    string NewValue,
    DateTimeOffset AppliedAt);

public sealed record CheckupActionResult(
    bool Success,
    string Message,
    CheckupReport Report,
    IReadOnlyList<RestoreChangeRecord> History);

public sealed record RestorePointResult(
    bool Success,
    string Message,
    long DurationMs);
