using System.Security.Claims;
using CiRunner.Core.Config;
using CiRunner.Core.Data;
using Microsoft.AspNetCore.Authentication;

namespace CiRunner.Host.Auth;

/// <summary>
/// Resolves the role claim fresh from user_roles on every request (spec §9: "セッション Cookie
/// にはロールを焼き込まず、リクエスト毎に DB 参照") for cookie-authenticated users. API-token
/// principals already carry their fixed role claim from issuance and are left untouched.
/// </summary>
public sealed class RoleClaimsTransformation : IClaimsTransformation
{
    private readonly UserRoleRepository _roles;
    private readonly RunnerConfig _config;

    public RoleClaimsTransformation(UserRoleRepository roles, RunnerConfig config)
    {
        _roles = roles;
        _config = config;
    }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
        {
            return Task.FromResult(principal);
        }
        if (identity.HasClaim(c => c.Type == "authMethod" && c.Value == "token"))
        {
            return Task.FromResult(principal);
        }

        var username = identity.Name;
        var role = username is null ? null : ResolveRole(username);

        foreach (var stale in identity.FindAll("role").ToList())
        {
            identity.RemoveClaim(stale);
        }
        if (role is not null)
        {
            identity.AddClaim(new Claim("role", role));
        }

        return Task.FromResult(principal);
    }

    private string? ResolveRole(string username)
    {
        var assigned = _roles.Find(username)?.Role;
        if (assigned is not null)
        {
            return assigned;
        }
        return _config.Auth.DefaultRole == "deny" ? null : _config.Auth.DefaultRole;
    }
}
