using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamNotes.Api.Data;

namespace TeamNotes.Api.Auth;

/// <summary>
/// Demo identity for local review: the caller names a seeded user in the <c>X-User-Id</c> header.
/// This is impersonation, not authentication. It only works in the Development environment AND when
/// <c>Demo:HeaderAuth</c> is true; otherwise it authenticates nobody, so every protected endpoint returns 401.
/// Replacing it means registering a real scheme (e.g. JWT bearer) that yields the same NameIdentifier claim.
/// </summary>
public sealed class DemoHeaderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    NotesDbContext db,
    IConfiguration configuration,
    IHostEnvironment environment)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DemoHeader";
    public const string HeaderName = "X-User-Id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!environment.IsDevelopment() || !configuration.GetValue<bool>("Demo:HeaderAuth"))
        {
            return AuthenticateResult.NoResult();
        }

        if (!Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return AuthenticateResult.NoResult();
        }

        // Only the canonical form (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx); no braces, whitespace or 32-hex variants.
        if (values.Count != 1 || !Guid.TryParseExact(values[0], "D", out var userId))
        {
            return AuthenticateResult.Fail($"{HeaderName} must be a single user id.");
        }

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, Context.RequestAborted);
        if (user is null)
        {
            return AuthenticateResult.Fail("Unknown user.");
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Name)],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    /// <summary>A 401 names the scheme the client should use (RFC 9110 section 15.5.2).</summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = SchemeName;
        return base.HandleChallengeAsync(properties);
    }
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>The authenticated caller's user id. Only call on endpoints that require authorization.</summary>
    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Endpoint reached without an authenticated user."));
}
