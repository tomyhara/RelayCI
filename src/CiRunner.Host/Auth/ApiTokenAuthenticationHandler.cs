using System.Security.Claims;
using System.Text.Encodings.Web;
using CiRunner.Core.Auth;
using CiRunner.Core.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CiRunner.Host.Auth;

/// <summary>
/// Bearer token scheme for scripted access (spec §9 "APIトークン"). Validates the SHA-256 hash of
/// the presented token against api_tokens and stamps the token's fixed role directly - unlike
/// cookie sessions, a token's role does not need a per-request refresh since revoking/reissuing a
/// token is itself how its access is changed.
/// </summary>
public sealed class ApiTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly ApiTokenRepository _tokens;

    public ApiTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiTokenRepository tokens)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var token = header["Bearer ".Length..].Trim();
        if (token.Length == 0)
        {
            return Task.FromResult(AuthenticateResult.Fail("empty bearer token"));
        }

        var hash = ApiTokenHasher.Hash(token);
        var record = _tokens.FindActiveByHashAndTouch(hash);
        if (record is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("invalid or revoked API token"));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, record.Name),
            new Claim("role", record.Role),
            new Claim("authMethod", "token"),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
