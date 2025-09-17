// API/Security/StaticWebAppsAuth.cs
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace API.Security;

public static class StaticWebAppsAuth
{
    private sealed class Principal
    {
        public string? IdentityProvider { get; set; }
        public string? UserId { get; set; }
        public string? UserDetails { get; set; }
        public string[]? UserRoles { get; set; }
        public Claim[]? Claims { get; set; }
    }

    private sealed class Claim
    {
        public string Type { get; set; } = "";
        public string Value { get; set; } = "";
    }

    // ---- Core parsing helpers ------------------------------------------------

    private static bool TryReadPrincipal(HttpRequestData req, out Principal? principal)
    {
        principal = null;

        // header name is case-insensitive, but we'll try both common casings
        if (!req.Headers.TryGetValues("X-MS-CLIENT-PRINCIPAL", out var vals) &&
            !req.Headers.TryGetValues("x-ms-client-principal", out vals))
        {
            return false;
        }

        var b64 = vals.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(b64)) return false;

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            principal = JsonSerializer.Deserialize<Principal>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return principal is not null;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetRoles(HttpRequestData req, out string[] roles)
    {
        roles = Array.Empty<string>();
        if (!TryReadPrincipal(req, out var p) || p is null) return false;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) roles array
        if (p.UserRoles is not null)
            foreach (var r in p.UserRoles) if (!string.IsNullOrWhiteSpace(r)) set.Add(r);

        // 2) claims-based roles (depends on provider)
        if (p.Claims is not null)
        {
            foreach (var c in p.Claims)
            {
                if (string.IsNullOrWhiteSpace(c?.Type) || string.IsNullOrWhiteSpace(c.Value)) continue;

                var t = c.Type;
                if (t.Equals("roles", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("role", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("http://schemas.microsoft.com/ws/2008/06/identity/claims/role", StringComparison.OrdinalIgnoreCase))
                {
                    set.Add(c.Value);
                }
            }
        }

        roles = set.ToArray();
        return roles.Length > 0;
    }

    public static bool IsInRole(HttpRequestData req, string role)
        => TryGetRoles(req, out var roles) && roles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));

    public static bool HasPrincipal(HttpRequestData req)
        => TryReadPrincipal(req, out _);

    // ---- Convenience: allow SWA admin OR x-api-key OR Development -----------

    /// <summary>
    /// True if:
    ///  - SWA header says user is in 'admin' role, OR
    ///  - request has x-api-key matching ServicesAdminApiKey/CmsAdminApiKey, OR
    ///  - environment is Development and no key is configured (for local dev).
    /// </summary>
    public static bool IsAuthorizedAdmin(HttpRequestData req, IConfiguration cfg, IHostEnvironment env)
    {
        if (IsInRole(req, "admin"))
            return true;

        var key = cfg["ServicesAdminApiKey"] ?? cfg["CmsAdminApiKey"];
        if (!string.IsNullOrWhiteSpace(key))
        {
            if (req.Headers.TryGetValues("x-api-key", out var vals) && vals.Contains(key))
                return true;

            // If key is configured but missing/incorrect, do NOT fall back to dev allowance.
            return false;
        }

        // No key configured: allow in Development only (useful for local)
        return env.IsDevelopment();
    }
}
