using System.Text.RegularExpressions;
using PulseCore.App.Models;

namespace PulseCore.App.Services;

/// <summary>Lee y cambia el plan de energía real de Windows vía powercfg (sin ventanas de consola).</summary>
public sealed class PowerPlanService
{
    public const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string PowerSaverGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";
    public const string UltimateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    private const string GuidPattern = @"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}";
    private static readonly Regex GuidRx = new(GuidPattern, RegexOptions.Compiled);
    private static readonly Regex ListRx = new($@"({GuidPattern})\s+\(([^)]*)\)(\s*\*)?", RegexOptions.Compiled);

    public string? GetActiveGuid()
    {
        var r = ProcessRunner.Run("powercfg", "/getactivescheme");
        var m = GuidRx.Match(r.Output);
        return r.Ok && m.Success ? m.Value.ToLowerInvariant() : null;
    }

    public IReadOnlyList<PowerPlanInfo> GetPlans()
    {
        var active = GetActiveGuid();
        var r = ProcessRunner.Run("powercfg", "/list");
        var plans = new List<PowerPlanInfo>();
        if (!r.Ok) return plans;

        foreach (Match m in ListRx.Matches(r.Output))
        {
            var guid = m.Groups[1].Value.ToLowerInvariant();
            var parsedName = m.Groups[2].Value.Trim();
            var isActive = active is not null ? guid == active : m.Groups[3].Success;
            plans.Add(new PowerPlanInfo(guid, FriendlyName(guid, parsedName), isActive, KindOf(guid)));
        }
        return plans;
    }

    public PowerPlanResult SetPowerPlan(string guid)
    {
        var before = GetPlans();
        var prev = before.FirstOrDefault(p => p.IsActive);

        if (!Guid.TryParse(guid, out var parsed))
            return Fail("GUID de plan inválido.", before, prev);

        var normalized = parsed.ToString().ToLowerInvariant();
        var target = before.FirstOrDefault(p => p.Guid == normalized);
        if (target is null)
            return Fail("Ese plan de energía no existe en este equipo.", before, prev);
        if (target.IsActive)
            return new PowerPlanResult(true, $"“{target.Name}” ya estaba activo.", target.Guid, target.Name,
                prev?.Guid, prev?.Name, before);

        var run = ProcessRunner.Run("powercfg", $"/setactive {normalized}");
        var after = GetPlans();
        var nowActive = after.FirstOrDefault(p => p.IsActive);

        if (run.Ok && nowActive?.Guid == normalized)
            return new PowerPlanResult(true, $"Plan cambiado: {prev?.Name ?? "?"} → {nowActive.Name}",
                nowActive.Guid, nowActive.Name, prev?.Guid, prev?.Name, after);

        var err = string.IsNullOrWhiteSpace(run.Error) ? run.Output.Trim() : run.Error.Trim();
        return new PowerPlanResult(false, $"No se pudo cambiar el plan. {err}".Trim(),
            nowActive?.Guid, nowActive?.Name, prev?.Guid, prev?.Name, after);
    }

    private static PowerPlanResult Fail(string msg, IReadOnlyList<PowerPlanInfo> plans, PowerPlanInfo? active) =>
        new(false, msg, active?.Guid, active?.Name, active?.Guid, active?.Name, plans);

    private static string KindOf(string guid) => guid switch
    {
        BalancedGuid => "balanced",
        HighPerformanceGuid => "high",
        PowerSaverGuid => "saver",
        UltimateGuid => "ultimate",
        _ => "custom"
    };

    private static string FriendlyName(string guid, string parsed) => guid switch
    {
        BalancedGuid => "Equilibrado",
        HighPerformanceGuid => "Alto rendimiento",
        PowerSaverGuid => "Economizador",
        UltimateGuid => "Máximo rendimiento",
        _ => string.IsNullOrWhiteSpace(parsed) ? guid : parsed
    };
}
