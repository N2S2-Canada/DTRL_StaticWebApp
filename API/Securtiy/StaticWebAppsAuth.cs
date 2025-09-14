using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;

namespace API.Security;

public static class StaticWebAppsAuth
{
    private sealed class PrincipalModel
    {
        public string? IdentityProvider { get; set; }
        public string? UserId { get; set; }
        public string? UserDetails { get; set; }
        public string[]? UserRoles { get; set; }
        public ClaimModel[]? Claims { get; set; }
    }
    private sealed class ClaimModel { public string Type { get; set; } = ""; public string Value { get; set; } = ""; }

    public static bool TryGetRoles(HttpRequestData req, out string[] roles)
    {
        roles = Array.Empty<string>();
        if (!req.Headers.TryGetValues("X-MS-CLIENT-PRINCIPAL", out var vals)) return false;

        var b64 = vals.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(b64)) return false;

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        var principal = JsonSerializer.Deserialize<PrincipalModel>(json);
        roles = principal?.UserRoles ?? Array.Empty<string>();
        return roles.Length > 0;
    }

    public static bool IsInRole(HttpRequestData req, string role)
        => TryGetRoles(req, out var roles) && roles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
}
