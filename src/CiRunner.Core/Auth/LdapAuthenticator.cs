using System.DirectoryServices.Protocols;
using System.Net;
using CiRunner.Core.Config;

namespace CiRunner.Core.Auth;

/// <summary>
/// Search-then-bind LDAPS authentication (spec §9): bind with a service account (or anonymously)
/// to find the user's DN via sAMAccountName, then bind again with that DN + the supplied password
/// to verify credentials. Always negotiates over SSL (LDAPS) - the spec explicitly forbids plain
/// simple-bind over 389 since the password would cross the wire unencrypted.
/// </summary>
public sealed class LdapAuthenticator : IAuthenticator
{
    private readonly LdapConfig _config;

    public LdapAuthenticator(LdapConfig config)
    {
        _config = config;
    }

    public Task<AuthResult?> AuthenticateAsync(string username, string password, CancellationToken ct)
    {
        // System.DirectoryServices.Protocols is synchronous; run it off the request thread so a
        // slow/unreachable LDAP server doesn't tie up an ASP.NET Core worker.
        return Task.Run(() => Authenticate(username, password), ct);
    }

    private AuthResult? Authenticate(string username, string password)
    {
        if (string.IsNullOrEmpty(_config.Server))
        {
            throw new InvalidOperationException("auth.ldap.server is not configured");
        }
        if (string.IsNullOrEmpty(_config.SearchBase))
        {
            throw new InvalidOperationException("auth.ldap.searchBase is not configured");
        }

        var uri = new Uri(_config.Server);
        var identifier = new LdapDirectoryIdentifier(uri.Host, uri.Port <= 0 ? 636 : uri.Port);

        using var searchConnection = new LdapConnection(identifier) { AuthType = AuthType.Basic };
        searchConnection.SessionOptions.SecureSocketLayer = true;
        searchConnection.SessionOptions.ProtocolVersion = 3;
        searchConnection.Bind(string.IsNullOrEmpty(_config.ServiceBindDn)
            ? new NetworkCredential()
            : new NetworkCredential(_config.ServiceBindDn, _config.ServiceBindPassword));

        var filter = _config.UserFilter.Replace("{username}", EscapeLdapFilterValue(username));
        var searchRequest = new SearchRequest(_config.SearchBase, filter, SearchScope.Subtree, _config.Attributes.ToArray());
        var searchResponse = (SearchResponse)searchConnection.SendRequest(searchRequest);
        if (searchResponse.Entries.Count == 0)
        {
            return null;
        }
        var entry = searchResponse.Entries[0];

        using var verifyConnection = new LdapConnection(identifier) { AuthType = AuthType.Basic };
        verifyConnection.SessionOptions.SecureSocketLayer = true;
        verifyConnection.SessionOptions.ProtocolVersion = 3;
        try
        {
            verifyConnection.Bind(new NetworkCredential(entry.DistinguishedName, password));
        }
        catch (LdapException)
        {
            return null; // wrong password, or bind otherwise rejected
        }

        string? Attr(string name) =>
            entry.Attributes.Contains(name) && entry.Attributes[name].Count > 0
                ? entry.Attributes[name][0]?.ToString()
                : null;

        return new AuthResult(username, Attr("displayName"), Attr("mail"), Attr("telephoneNumber"));
    }

    /// <summary>RFC 4515 filter-value escaping - defensive against filter injection via the login form.</summary>
    private static string EscapeLdapFilterValue(string value) => value
        .Replace("\\", "\\5c")
        .Replace("*", "\\2a")
        .Replace("(", "\\28")
        .Replace(")", "\\29")
        .Replace("\0", "\\00");
}
