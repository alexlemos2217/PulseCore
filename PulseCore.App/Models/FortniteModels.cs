namespace PulseCore.App.Models;

public sealed record FortniteSettings(
    double? FrameRateLimit,
    bool? UseVSync,
    bool? MotionBlur,
    bool? ShowGrass,
    int? ShadowQuality,
    int? EffectsQuality,
    int? PostProcessQuality,
    int? AntiAliasingQuality,
    int? ViewDistanceQuality);

public sealed record FolderStats(string Path, bool Exists, double SizeMb, int Files);

public sealed record FortniteStatus(
    bool IsDetected,
    string ConfigPath,
    bool HasBackup,
    string? BackupPath,
    DateTimeOffset? BackupCreatedAt,
    FortniteSettings? Settings,
    bool IsReadOnly,
    bool GameRunning,
    FolderStats Demos,
    FolderStats Logs,
    string? Message);

public sealed record FortnitePresetResult(
    bool Success,
    string Message,
    string Preset,
    int FpsLimit,
    bool ReadOnly,
    bool BackupCreated,
    bool GameRunning,
    int ChangedKeys,
    FortniteStatus Status);

public sealed record FortniteRestoreResult(
    bool Success,
    string Message,
    bool GameRunning,
    FortniteStatus Status);

public sealed record FortniteCleanResult(
    bool Success,
    string Message,
    double FreedMb,
    int FilesDeleted,
    int FilesSkipped,
    long DurationMs,
    FortniteStatus Status);
