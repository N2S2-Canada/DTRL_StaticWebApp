using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;

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
    private sealed class Claim { public string Type { get; set; } = ""; public string Value { get; set; } = ""; }

    public static bool TryGetRoles(HttpRequestData req, out string[] roles)
    {
        roles = Array.Empty<string>();
        if (!req.Headers.TryGetValues("X-MS-CLIENT-PRINCIPAL", out var vals)) return false;
        var b64 = vals.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(b64)) return false;

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        var p = JsonSerializer.Deserialize<Principal>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (p is null) return false;

        var list = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) Explicit userRoles (classic SWA)
        if (p.UserRoles is not null) foreach (var r in p.UserRoles) list.Add(r);

        // 2) Claims-based roles (some providers)
        if (p.Claims is not null)
        {
            foreach (var c in p.Claims)
            {
                if (string.Equals(c.Type, "roles", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Type, "role", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Type, "http://schemas.microsoft.com/ws/2008/06/identity/claims/role", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(c.Value);
                }
            }
        }

        roles = list.ToArray();
        return roles.Length > 0;
    }

    public static bool IsInRole(HttpRequestData req, string role)
        => TryGetRoles(req, out var roles) && roles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
}
