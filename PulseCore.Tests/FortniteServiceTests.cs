using System.IO;
using PulseCore.App.Services;
using Xunit;

namespace PulseCore.Tests;

public sealed class FortniteServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _savedDir;
    private readonly string _backupDir;
    private readonly string _iniPath;

    public FortniteServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"PulseCore_FnTests_{Guid.NewGuid():N}");
        _savedDir = Path.Combine(_root, "Saved");
        _backupDir = Path.Combine(_root, "Backups");
        _iniPath = Path.Combine(_savedDir, "Config", "WindowsClient", "GameUserSettings.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(_iniPath)!);
    }

    private const string SampleIni = """
        [/Script/FortniteGame.FortGameUserSettings]
        bUseVSync=True
        bMotionBlur=True
        bShowGrass=True
        FrameRateLimit=60.000000
        CustomUnrelatedKey=KeepMe

        [ScalabilityGroups]
        sg.ShadowQuality=3
        sg.EffectsQuality=3
        sg.PostProcessQuality=3
        sg.AntiAliasingQuality=3
        sg.ViewDistanceQuality=3
        """;

    [Fact]
    public void GetStatus_WhenIniMissing_ReturnsNotDetectedWithoutThrowing()
    {
        var svc = new FortniteService(_savedDir, _backupDir);
        var status = svc.GetStatus();

        Assert.False(status.IsDetected);
        Assert.False(status.HasBackup);
        Assert.Null(status.Settings);
    }

    [Fact]
    public void ApplyPreset_Competitive_UpdatesExpectedKeysCreatesBackupAndRespectsReadOnly()
    {
        File.WriteAllText(_iniPath, SampleIni);
        var svc = new FortniteService(_savedDir, _backupDir);

        var res = svc.ApplyPreset("competitive", fpsLimit: 240, setReadOnly: true);

        Assert.True(res.Success);
        Assert.True(res.BackupCreated);
        Assert.True(res.Status.HasBackup);
        Assert.True(res.Status.IsReadOnly);

        var s = res.Status.Settings!;
        Assert.Equal(240, s.FrameRateLimit);
        Assert.False(s.UseVSync);
        Assert.False(s.MotionBlur);
        Assert.False(s.ShowGrass);
        Assert.Equal(0, s.ShadowQuality);
        Assert.Equal(0, s.EffectsQuality);
        Assert.Equal(0, s.PostProcessQuality);
        Assert.Equal(0, s.AntiAliasingQuality);
        Assert.Equal(2, s.ViewDistanceQuality);

        // El archivo conserva claves no relacionadas
        Assert.Contains("CustomUnrelatedKey=KeepMe", File.ReadAllText(_iniPath));
        // El backup original quedó intacto con los valores previos
        var backupText = File.ReadAllText(res.Status.BackupPath!);
        Assert.Contains("bUseVSync=True", backupText);
        Assert.Contains("sg.ShadowQuality=3", backupText);
    }

    [Fact]
    public void ApplyPreset_SecondTime_DoesNotOverwriteOriginalBackup_AndRestoreRevertsExactOriginal()
    {
        File.WriteAllText(_iniPath, SampleIni);
        var svc = new FortniteService(_savedDir, _backupDir);

        var first = svc.ApplyPreset("competitive", 165, setReadOnly: true);
        Assert.True(first.BackupCreated);

        // Aplicar un segundo preset (sobre archivo ReadOnly) no sobrescribe el backup original
        var second = svc.ApplyPreset("quality", 0, setReadOnly: false);
        Assert.True(second.Success);
        Assert.False(second.BackupCreated);
        Assert.False(second.Status.IsReadOnly);
        Assert.Equal(0, second.Status.Settings!.FrameRateLimit);
        Assert.True(second.Status.Settings.ShowGrass);
        Assert.Equal(2, second.Status.Settings.ShadowQuality);
        Assert.Equal(3, second.Status.Settings.ViewDistanceQuality);

        // Restaurar devuelve el archivo original exacto
        var restored = svc.RestoreBackup();
        Assert.True(restored.Success);
        Assert.Equal(60, restored.Status.Settings!.FrameRateLimit);
        Assert.True(restored.Status.Settings.UseVSync);
        Assert.Equal(3, restored.Status.Settings.ShadowQuality);
    }

    [Fact]
    public void CleanFortniteFiles_DeletesOnlySelectedFoldersAndReportsFreedBytes()
    {
        File.WriteAllText(_iniPath, SampleIni);
        var demos = Path.Combine(_savedDir, "Demos");
        var logs = Path.Combine(_savedDir, "Logs");
        Directory.CreateDirectory(demos);
        Directory.CreateDirectory(logs);
        File.WriteAllBytes(Path.Combine(demos, "match1.replay"), new byte[1024 * 256]);
        File.WriteAllBytes(Path.Combine(logs, "client.log"), new byte[1024 * 128]);

        var svc = new FortniteService(_savedDir, _backupDir);
        var clean = svc.CleanFortniteFiles(cleanDemos: false, cleanLogs: true);

        Assert.True(clean.Success);
        Assert.Equal(1, clean.FilesDeleted);
        Assert.True(clean.FreedMb > 0);
        // Demos no fue seleccionado -> sigue intacto
        Assert.True(File.Exists(Path.Combine(demos, "match1.replay")));
        Assert.False(File.Exists(Path.Combine(logs, "client.log")));
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_iniPath)) File.SetAttributes(_iniPath, FileAttributes.Normal);
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch { }
    }
}
