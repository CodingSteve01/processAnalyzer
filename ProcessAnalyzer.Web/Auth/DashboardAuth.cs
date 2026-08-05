using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using ProcessAnalyzer.Web.Options;

namespace ProcessAnalyzer.Web.Auth;

/// <summary>
/// A password in front of the dashboard.
/// <para>
/// With names switched on, this tool shows who approves whose leave and who depends on whom. That is not something
/// anybody who can reach the port should read, and "it is only on the internal network" has never been an access
/// control. So there is a login.
/// </para>
/// <para>
/// One shared password, not a user directory. The tool has no per-person content and no roles of its own — every
/// reader sees the same screens — so accounts would be management overhead that buys nothing. What it does buy is
/// stated plainly in the README: this proves somebody knows the password, not who they are.
/// </para>
/// </summary>
public static class DashboardAuth
{
    public const string SchemeName = "dashboard";

    // Paths that must answer before anybody has logged in: the probe a container orchestrator calls, and the login
    // page itself with the few assets it needs to render.
    private static readonly string[] AlwaysOpen =
    [
        "/health",
        "/api/version",
        "/api/login",
        "/login.html",
        "/styles.css",
        "/js/login.js",
    ];

    public static IServiceCollection AddDashboardAuth(this IServiceCollection services, ProcessAnalyzerOptions options)
    {
        services
            .AddAuthentication(SchemeName)
            .AddCookie(
                SchemeName,
                cookie =>
                {
                    cookie.Cookie.Name = "pa.session";
                    cookie.Cookie.HttpOnly = true;
                    cookie.Cookie.SameSite = SameSiteMode.Strict;
                    // Lax rather than Always: the tool is reached over plain http inside the network today, and
                    // Always would hand out a cookie the browser then refuses to send back — a login that appears
                    // to succeed and never sticks.
                    cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    cookie.ExpireTimeSpan = TimeSpan.FromHours(12);
                    cookie.SlidingExpiration = true;
                    cookie.LoginPath = "/login.html";
                    // An API call must fail as an API call. Redirecting XHR to a login page gives the frontend an
                    // HTML body where it expects JSON, and the error it then shows is about parsing, not about auth.
                    cookie.Events.OnRedirectToLogin = context =>
                    {
                        if (context.Request.Path.StartsWithSegments("/api"))
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return Task.CompletedTask;
                        }

                        context.Response.Redirect(context.RedirectUri);
                        return Task.CompletedTask;
                    };
                }
            );

        services.AddAuthorization();
        return services;
    }

    /// <summary>
    /// Requires a session for everything except the handful of paths that have to answer without one.
    /// </summary>
    /// <remarks>
    /// Written as a plain middleware rather than per-endpoint attributes: a new endpoint added later is protected by
    /// default. The opposite arrangement — public unless somebody remembers to lock it — is how analytical tools end
    /// up with one forgotten open route.
    /// </remarks>
    public static WebApplication UseDashboardAuth(this WebApplication app, ProcessAnalyzerOptions options)
    {
        if (!options.RequireLogin)
        {
            app.Logger.LogWarning(
                "Dashboard login is OFF. Anybody who can reach this port sees every analysis, including names when "
                    + "ShowActorIdentity is on. Set ProcessAnalyzer__RequireLogin=true and a password hash."
            );
            return app;
        }

        if (string.IsNullOrWhiteSpace(options.DashboardPasswordHash))
            throw new InvalidOperationException(
                "RequireLogin is on but no DashboardPasswordHash is configured. Refusing to start rather than "
                    + "serving an unprotected dashboard: a login that cannot verify anything is worse than none, "
                    + "because it looks like protection. Generate one with: dotnet run -- hash-password"
            );

        app.UseAuthentication();
        app.UseAuthorization();

        app.Use(
            async (context, next) =>
            {
                var path = context.Request.Path.Value ?? string.Empty;
                var open = AlwaysOpen.Any(candidate => path.Equals(candidate, StringComparison.OrdinalIgnoreCase));

                if (open || context.User.Identity?.IsAuthenticated == true)
                {
                    await next();
                    return;
                }

                if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                context.Response.Redirect("/login.html");
            }
        );

        return app;
    }

    public static WebApplication MapLoginEndpoints(this WebApplication app, ProcessAnalyzerOptions options)
    {
        app.MapPost(
            "/api/login",
            async (LoginRequest request, HttpContext context) =>
            {
                // Constant-time comparison and a deliberate delay: without them the endpoint answers a wrong password
                // measurably faster than a right one, and an internal tool is exactly where nobody would notice
                // somebody measuring.
                await Task.Delay(Random.Shared.Next(120, 260));

                if (!Verify(request.Password, options.DashboardPasswordHash))
                    return Results.Unauthorized();

                var identity = new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, "dashboard")],
                    CookieAuthenticationDefaults.AuthenticationScheme
                );
                await context.SignInAsync(SchemeName, new ClaimsPrincipal(identity));
                return Results.Ok(new { ok = true });
            }
        );

        app.MapPost(
            "/api/logout",
            async (HttpContext context) =>
            {
                await context.SignOutAsync(SchemeName);
                return Results.Ok(new { ok = true });
            }
        );

        return app;
    }

    /// <summary>Hashes a password for the configuration file. PBKDF2 with a random salt, 210 000 iterations.</summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    private const int Iterations = 210_000;

    private static bool Verify(string? password, string stored)
    {
        if (string.IsNullOrEmpty(password))
            return false;

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations))
            return false;

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <param name="Password">What the user typed.</param>
    public sealed record LoginRequest(string? Password);
}
