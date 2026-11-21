using System.IO;
using System.Management;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PulseCore.App.Services;

public sealed record LicenseStatusDto(
    bool IsPro,
    string? MaskedKey,
    string? Email,
    string CheckoutUrl,
    string Hwid,
    string StatusMessage);

public sealed record LicenseActionResult(
    bool Success,
    string Message,
    LicenseStatusDto Status);

/// <summary>
/// Sistema de licencias PulseCore Pro conectado a Gumroad API, ligado a HWID y almacenado cifrado con Windows DPAPI.
/// </summary>
public sealed class LicenseService
{
    public const string GumroadProductId = "njqMLzXT4i02YiX8rNnnlw==";
    public const string CheckoutUrl = "https://lemosphere67.gumroad.com/l/pulsecore-pro";
    public const int MaxActivations = 2;

    private const string VerifyEndpoint = "https://api.gumroad.com/v2/licenses/verify";
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("PulseCore.License.v2.DPAPI");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private sealed class StoredLicense
    {
        public string LicenseKey { get; set; } = "";
        public string Hwid { get; set; } = "";
        public string? CustomerEmail { get; set; }
        public DateTimeOffset ActivatedAtUtc { get; set; }
        public DateTimeOffset LastValidatedUtc { get; set; }
    }

    private readonly string _licensePath;
    private readonly string _hwid;
    private readonly object _lock = new();
    private StoredLicense? _stored;

    public bool IsPro { get { lock (_lock) return _stored is not null; } }
    public string? MaskedKey { get { lock (_lock) return _stored is null ? null : MaskKey(_stored.LicenseKey); } }
    public string? CustomerEmail { get { lock (_lock) return _stored?.CustomerEmail; } }
    public string StatusMessage { get; private set; } = "Modo Free";

    public bool NeedsBackgroundRevalidation
    {
        get
        {
            lock (_lock)
            {
                return _stored is not null && (DateTimeOffset.UtcNow - _stored.LastValidatedUtc) > TimeSpan.FromDays(7);
            }
        }
    }

    public LicenseService(string? licensePath = null)
    {
        _licensePath = string.IsNullOrWhiteSpace(licensePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseCore", "license.dat")
            : licensePath;
        _hwid = ComputeHardwareId();
        LoadLocal();
    }

    public string GetHardwareId() => _hwid;

    public LicenseStatusDto GetStatus()
    {
        lock (_lock)
        {
            return new LicenseStatusDto(
                _stored is not null,
                _stored is null ? null : MaskKey(_stored.LicenseKey),
                _stored?.CustomerEmail,
                CheckoutUrl,
                _hwid,
                StatusMessage);
        }
    }

    // ==================== ACTIVACIÓN CONTRA GUMROAD ====================
    public async Task<LicenseActionResult> ActivateAsync(string? licenseKey, CancellationToken ct = default)
    {
        var key = (licenseKey ?? "").Trim();
        if (key.Length < 6)
            return new LicenseActionResult(false, "Ingresá una clave de licencia válida.", GetStatus());

        try
        {
            var (ok, refunded, chargebacked, uses, email, apiError) = await CallGumroadAsync(key, incrementUses: true, ct);
            if (!ok)
                return new LicenseActionResult(false, apiError ?? "La clave de licencia no es válida o no pertenece a PulseCore Pro.", GetStatus());

            if (refunded || chargebacked)
                return new LicenseActionResult(false, "Esta licencia fue reembolsada o revocada y ya no está activa.", GetStatus());

            if (uses > MaxActivations)
                return new LicenseActionResult(false, $"Esta clave superó el límite máximo de {MaxActivations} activaciones.", GetStatus());

            var now = DateTimeOffset.UtcNow;
            var record = new StoredLicense
            {
                LicenseKey = key,
                Hwid = _hwid,
                CustomerEmail = email,
                ActivatedAtUtc = now,
                LastValidatedUtc = now
            };

            lock (_lock)
            {
                _stored = record;
                StatusMessage = "PulseCore Pro activo";
            }
            SaveEncrypted(record);
            return new LicenseActionResult(true, "¡Licencia PulseCore Pro activada correctamente!", GetStatus());
        }
        catch (TaskCanceledException)
        {
            return new LicenseActionResult(false, "Tiempo de espera agotado al conectar con Gumroad. Verificá tu conexión.", GetStatus());
        }
        catch (HttpRequestException)
        {
            return new LicenseActionResult(false, "No se pudo conectar con Gumroad. Verificá tu conexión a Internet.", GetStatus());
        }
        catch (Exception ex)
        {
            return new LicenseActionResult(false, $"Error al activar la licencia: {ex.Message}", GetStatus());
        }
    }

    // ==================== REVALIDACIÓN SILENCIOSA ====================
    public async Task<bool> ValidateAsync(CancellationToken ct = default)
    {
        StoredLicense? current;
        lock (_lock) current = _stored;
        if (current is null) return false;

        try
        {
            var (ok, refunded, chargebacked, _, email, _) = await CallGumroadAsync(current.LicenseKey, incrementUses: false, ct);
            if (!ok || refunded || chargebacked)
            {
                DeactivateLocal("La licencia fue revocada o reembolsada.");
                return false;
            }

            lock (_lock)
            {
                if (_stored is not null)
                {
                    _stored.LastValidatedUtc = DateTimeOffset.UtcNow;
                    if (!string.IsNullOrWhiteSpace(email)) _stored.CustomerEmail = email;
                    StatusMessage = "PulseCore Pro activo";
                    SaveEncrypted(_stored);
                }
            }
            return true;
        }
        catch (Exception)
        {
            // Sin conexión a Internet: grace period offline manteniendo el estado Pro activo.
            return true;
        }
    }

    public LicenseStatusDto DeactivateLocal(string? reason = null)
    {
        lock (_lock)
        {
            _stored = null;
            StatusMessage = reason ?? "Modo Free";
        }
        try { if (File.Exists(_licensePath)) File.Delete(_licensePath); } catch (Exception) { }
        return GetStatus();
    }

    // ==================== GUMROAD HTTP ====================
    private static async Task<(bool Ok, bool Refunded, bool Chargebacked, int Uses, string? Email, string? Error)>
        CallGumroadAsync(string key, bool incrementUses, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["product_id"] = GumroadProductId,
            ["license_key"] = key,
            ["increment_uses_count"] = incrementUses ? "true" : "false"
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, VerifyEndpoint) { Content = form };
        req.Headers.TryAddWithoutValidation("User-Agent", "PulseCore/2.0.0");

        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
        if (!success)
        {
            var msg = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : "Clave de licencia inválida.";
            return (false, false, false, 0, null, msg);
        }

        var uses = root.TryGetProperty("uses", out var u) && u.TryGetInt32(out var ui) ? ui : 1;
        bool refunded = false, chargebacked = false;
        string? email = null;

        if (root.TryGetProperty("purchase", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            refunded = p.TryGetProperty("refunded", out var r) && r.ValueKind == JsonValueKind.True;
            chargebacked = p.TryGetProperty("chargebacked", out var c) && c.ValueKind == JsonValueKind.True;
            if (p.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String)
                email = e.GetString();
        }

        return (true, refunded, chargebacked, uses, email, null);
    }

    // ==================== ALMACENAMIENTO CIFRADO DPAPI ====================
    private void LoadLocal()
    {
        try
        {
            if (!File.Exists(_licensePath)) return;
            var encrypted = File.ReadAllBytes(_licensePath);
            var decrypted = ProtectedData.Unprotect(encrypted, DpapiEntropy, DataProtectionScope.CurrentUser);
            var stored = JsonSerializer.Deserialize<StoredLicense>(decrypted, JsonOpts);
            if (stored is null || string.IsNullOrWhiteSpace(stored.LicenseKey)) return;

            if (!string.Equals(stored.Hwid, _hwid, StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = "Licencia ligada a otro equipo (Modo Free)";
                return;
            }

            lock (_lock)
            {
                _stored = stored;
                StatusMessage = "PulseCore Pro activo";
            }
        }
        catch (Exception)
        {
            StatusMessage = "Modo Free";
        }
    }

    private void SaveEncrypted(StoredLicense record)
    {
        try
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(record, JsonOpts);
            var encrypted = ProtectedData.Protect(plain, DpapiEntropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_licensePath)!);
            var tmp = _licensePath + ".tmp";
            File.WriteAllBytes(tmp, encrypted);
            File.Move(tmp, _licensePath, true);
        }
        catch (Exception) { }
    }

    // ==================== HWID + MÁSCARA ====================
    private static string ComputeHardwareId()
    {
        var board = QueryWmi("SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber");
        var cpu = QueryWmi("SELECT ProcessorId FROM Win32_Processor", "ProcessorId");
        var raw = string.IsNullOrWhiteSpace(board) && string.IsNullOrWhiteSpace(cpu)
            ? $"{Environment.MachineName}|{Environment.UserName}"
            : $"{board}|{cpu}";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..24];
    }

    private static string QueryWmi(string query, string prop)
    {
        try
        {
            using var s = new ManagementObjectSearcher(query);
            foreach (var obj in s.Get())
            {
                var v = obj[prop]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        catch (Exception) { }
        return "";
    }

    private static string MaskKey(string key)
    {
        var clean = key.Trim();
        if (clean.Length <= 8) return "****-****";
        return $"{clean[..4]}-****-****-{clean[^4..]}";
    }
}
