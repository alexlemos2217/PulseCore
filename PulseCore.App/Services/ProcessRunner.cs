using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PulseCore.App.Services;

/// <summary>Ejecuta utilidades de consola de Windows sin mostrar ventana y con timeout.</summary>
internal static class ProcessRunner
{
    public sealed record Result(int ExitCode, string Output, string Error, bool TimedOut)
    {
        public bool Ok => !TimedOut && ExitCode == 0;
    }

    private static readonly Encoding OemEncoding = ResolveOem();

    private static Encoding ResolveOem()
    {
        try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch (Exception) { return Encoding.UTF8; }
    }

    public static Result Run(string fileName, string arguments, int timeoutMs = 10000)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = OemEncoding,
            StandardErrorEncoding = OemEncoding,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new Result(-1, "", "No se pudo iniciar el proceso.", false);

            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch (Exception) { }
                return new Result(-1, "", "Tiempo de espera agotado.", true);
            }
            p.WaitForExit();
            return new Result(p.ExitCode, stdout.Result, stderr.Result, false);
        }
        catch (Exception ex)
        {
            return new Result(-1, "", ex.Message, false);
        }
    }
}
