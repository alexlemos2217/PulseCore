using System.IO;
using PulseCore.App.Services;
using Xunit;

namespace PulseCore.Tests;

public sealed class CleanerServiceTests : IDisposable
{
    private readonly string _tempDir;

    public CleanerServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PulseCore_CleanTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ScanDirectory_DryRun_MeasuresFilesRecursivelyWithoutDeletingAnything()
    {
        var sub = Path.Combine(_tempDir, "nested");
        Directory.CreateDirectory(sub);
        var f1 = Path.Combine(_tempDir, "a.tmp");
        var f2 = Path.Combine(sub, "b.cache");
        File.WriteAllBytes(f1, new byte[512 * 1024]); // 0.5 MB
        File.WriteAllBytes(f2, new byte[512 * 1024]); // 0.5 MB

        var svc = new CleanerService();
        var result = svc.ScanDirectory("test_temp", "Test Temp", _tempDir);

        Assert.True(result.Exists);
        Assert.Equal(2, result.Files);
        Assert.Equal(1.0, result.SizeMb);
        // Dry Run: los archivos deben seguir intactos
        Assert.True(File.Exists(f1));
        Assert.True(File.Exists(f2));
    }

    [Fact]
    public void ScanDirectory_WhenDirectoryDoesNotExist_ReturnsZeroSafely()
    {
        var missing = Path.Combine(_tempDir, "does_not_exist");
        var svc = new CleanerService();

        var result = svc.ScanDirectory("missing", "Missing", missing);

        Assert.False(result.Exists);
        Assert.Equal(0, result.Files);
        Assert.Equal(0, result.SizeMb);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows")]
    public void IsSafePath_RejectsCriticalSystemRoots(string dangerousPath)
    {
        Assert.False(CleanerService.IsSafePath(dangerousPath));
    }

    [Fact]
    public void IsSafePath_AllowsDedicatedTempSubfolder()
    {
        Assert.True(CleanerService.IsSafePath(_tempDir));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }
}
