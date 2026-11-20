using System.IO;
using PulseCore.App.Services;
using Xunit;

namespace PulseCore.Tests;

public sealed class GameModeServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public GameModeServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PulseCore_GmTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "games.json");
    }

    [Fact]
    public void FirstLoad_CreatesDefaultThreeGamesAndPersistsJson()
    {
        using var svc = new GameModeService(new PowerPlanService(), new MemoryOptimizerService(), _configPath);
        var state = svc.GetState();

        Assert.True(File.Exists(_configPath));
        Assert.False(state.IsEnabled);
        Assert.Equal(3, state.Games.Count);
        Assert.Contains(state.Games, g => g.Process.Equals("FortniteClient-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(state.Games, g => g.Process.Equals("Cyberpunk2077.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(state.Games, g => g.Process.Equals("Valorant-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AddRemoveAndToggle_PersistsAcrossInstancesAndValidatesInput()
    {
        using (var svc = new GameModeService(new PowerPlanService(), new MemoryOptimizerService(), _configPath))
        {
            // Agrega normalizando .exe
            var add = svc.AddGame("Counter-Strike 2", "cs2");
            Assert.True(add.Ok);

            // Rechaza duplicado
            var dup = svc.AddGame("CS2 Dup", "CS2.exe");
            Assert.False(dup.Ok);

            // Rechaza rutas con barras
            var bad = svc.AddGame("Mal", @"C:\Games\bad.exe");
            Assert.False(bad.Ok);

            // Elimina uno de los default y activa vigilancia
            var rem = svc.RemoveGame("Cyberpunk2077.exe");
            Assert.True(rem.Ok);
            var tog = svc.SetEnabled(true);
            Assert.True(tog.Ok);
        }

        // Segunda instancia lee exactamente el estado persistido
        using var reloaded = new GameModeService(new PowerPlanService(), new MemoryOptimizerService(), _configPath);
        var state = reloaded.GetState();

        Assert.True(state.IsEnabled);
        Assert.Equal(3, state.Games.Count);
        Assert.Contains(state.Games, g => g.Process == "cs2.exe" && g.Name == "Counter-Strike 2");
        Assert.DoesNotContain(state.Games, g => g.Process.Equals("Cyberpunk2077.exe", StringComparison.OrdinalIgnoreCase));
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
