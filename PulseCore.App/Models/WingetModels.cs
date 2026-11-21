namespace PulseCore.App.Models;

public sealed record WingetAppInfo(string Id, string Name, string Category, string Description);

public sealed record WingetCatalogResult(
    bool IsAvailable,
    string? Version,
    bool IsBusy,
    IReadOnlyList<WingetAppInfo> Apps);

public sealed record WingetOutputEvent(
    string Line,
    string Stream,
    int? Current,
    int? Total,
    string? CurrentId);

public sealed record WingetCompletedEvent(
    bool Success,
    string Action,
    int Succeeded,
    int Failed,
    int Total,
    string Message);
