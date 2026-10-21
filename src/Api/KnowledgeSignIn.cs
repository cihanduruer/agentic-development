using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using AgenticHotelBooking.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AgenticHotelBooking.Api;

public sealed record KnowledgeSignInOptions(string[] AllowedEmails, string? SigningSecret);

/// <summary>Requests a short-lived code for an approved administrator email.</summary>
public sealed record KnowledgeCodeRequest(string Email);

/// <summary>Verifies the single-use code delivered to an administrator email.</summary>
public sealed record KnowledgeCodeVerification(string Email, string Code);

/// <summary>A time-limited browser-tab session; the token is never logged or placed in a URL.</summary>
public sealed record KnowledgeSignInSession(string Token, DateTimeOffset ExpiresAt);

/// <summary>The validated administrator identity and the current session expiry.</summary>
public sealed record AdministratorProfile(string Email, DateTimeOffset ExpiresAt);

public sealed class KnowledgeSignInException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class KnowledgeSignInService(
    KnowledgeSignInOptions options,
    KnowledgeEmailOptions emailOptions,
    IKnowledgeChallengeStore store,
    IKnowledgeEmailSender sender,
    TimeProvider time)
{
    private const string Issuer = "hotel-knowledge-sign-in";
    private const string Audience = "hotel-knowledge-management";
    private readonly HashSet<string> allowed = options.AllowedEmails.Select(email => email.Trim().ToLowerInvariant())
        .ToHashSet(StringComparer.Ordinal);

    public bool Enabled => allowed.Count > 0 && options.SigningSecret is { Length: >= 32 } &&
        Uri.TryCreate(emailOptions.Endpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme == Uri.UriSchemeHttps &&
        MailAddress.TryCreate(emailOptions.SenderAddress, out _);

    private byte[] Key => HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.SigningSecret!),
        Encoding.UTF8.GetBytes("Hotel Knowledge Center email sign-in v1"));

    private static string Normalize(string? email)
    {
        var value = email?.Trim().ToLowerInvariant();
        if (value is null || value.Length > 254 || !MailAddress.TryCreate(value, out var address) || address.Address != value)
        {
            throw new KnowledgeSignInException(400, "Enter a valid email address.");
        }
        return value;
    }

    private static string StoreKey(string email) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email)));
    private byte[] Hash(string email, string nonce, string code) =>
        HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes($"{email}\n{nonce}\n{code}"));

    private void RequireEnabled()
    {
        if (!Enabled)
        {
            throw new KnowledgeSignInException(503, "Email sign-in is not configured. Contact the administrator.");
        }
    }

    public async Task RequestCodeAsync(string email, CancellationToken cancellationToken)
    {
        RequireEnabled();
        email = Normalize(email);
        if (!allowed.Contains(email))
        {
            return;
        }

        var now = time.GetUtcNow();
        var existing = await store.GetAsync(StoreKey(email), cancellationToken);
        if (existing is not null && existing.Challenge.ResendAfter > now)
        {
            return;
        }

        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var challenge = new KnowledgeEmailChallenge(Hash(email, nonce, code), nonce,
            now.AddMinutes(5), now.AddMinutes(1), 0, false);
        if (!await store.TrySaveAsync(StoreKey(email), challenge, existing?.Version, cancellationToken))
        {
            throw new KnowledgeSignInException(429, "Another sign-in request is in progress. Please wait and retry.");
        }
        await sender.SendCodeAsync(email, code, cancellationToken);
    }

    public async Task<KnowledgeSignInSession> VerifyCodeAsync(string email, string code, CancellationToken cancellationToken)
    {
        RequireEnabled();
        email = Normalize(email);
        var existing = allowed.Contains(email) ? await store.GetAsync(StoreKey(email), cancellationToken) : null;
        var now = time.GetUtcNow();
        if (existing is null || existing.Challenge.Consumed || existing.Challenge.ExpiresAt <= now ||
            existing.Challenge.Attempts >= 5 || code is null || code.Length != 6 || code.Any(character => character is < '0' or > '9'))
        {
            throw Denied();
        }

        var challenge = existing.Challenge;
        var valid = CryptographicOperations.FixedTimeEquals(challenge.Hash, Hash(email, challenge.Nonce, code));
        if (!await store.TrySaveAsync(StoreKey(email),
            challenge with { Attempts = challenge.Attempts + 1, Consumed = valid }, existing.Version, cancellationToken))
        {
            throw new KnowledgeSignInException(429, "Another verification is in progress. Please retry.");
        }
        if (!valid)
        {
            throw Denied();
        }

        var expires = now.AddHours(8);
        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (!await store.TryCreateSessionAsync(StoreKey("session\n" + sessionId),
            new KnowledgeAdministratorSession(SHA256.HashData(Encoding.UTF8.GetBytes(email)), expires), cancellationToken))
        {
            throw new KnowledgeSignInException(503, "The verified session could not be recorded. Request a new code.");
        }
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = new Dictionary<string, object> { ["email"] = email, ["role"] = "KnowledgeAdministrator", ["jti"] = sessionId },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Key), SecurityAlgorithms.HmacSha256)
        });
        return new KnowledgeSignInSession(token, expires);
    }

    public async Task<bool> AuthorizeAsync(string token)
    {
        if (!Enabled || string.IsNullOrEmpty(token) || token.Length > 4096)
        {
            return false;
        }
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Key),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            LifetimeValidator = (notBefore, expires, _, _) =>
                notBefore is not null && expires is not null && notBefore <= time.GetUtcNow().UtcDateTime &&
                expires > time.GetUtcNow().UtcDateTime
        });
        var email = result.ClaimsIdentity?.FindFirst("email")?.Value;
        if (!result.IsValid || email is null || !allowed.Contains(email) ||
            result.ClaimsIdentity?.FindFirst("role")?.Value != "KnowledgeAdministrator" ||
            result.SecurityToken is not JsonWebToken validated || validated.Id is not { Length: 64 })
        {
            return false;
        }
        var session = await store.GetSessionAsync(StoreKey("session\n" + validated.Id), CancellationToken.None);
        return session is not null && session.ExpiresAt > time.GetUtcNow() &&
            session.ExpiresAt.ToUnixTimeSeconds() == new DateTimeOffset(validated.ValidTo, TimeSpan.Zero).ToUnixTimeSeconds() &&
            CryptographicOperations.FixedTimeEquals(session.EmailHash, SHA256.HashData(Encoding.UTF8.GetBytes(email)));
    }

    public async Task<AdministratorProfile?> GetProfileAsync(string token)
    {
        if (!await AuthorizeAsync(token))
        {
            return null;
        }
        var validated = new JsonWebTokenHandler().ReadJsonWebToken(token);
        return new AdministratorProfile(validated.GetClaim("email").Value, new DateTimeOffset(validated.ValidTo, TimeSpan.Zero));
    }

    private static KnowledgeSignInException Denied() => new(401, "The code is invalid, expired or already used. Request a new code.");
}

public static partial class KnowledgeSignInEndpoints
{
    public static void MapKnowledgeSignIn(this WebApplication app)
    {
        app.MapGet("/api/admin/auth/capabilities", (KnowledgeSignInService signIn, IHostEnvironment environment, IConfiguration configuration) =>
            Results.Ok(new { requiresSignIn = KnowledgeAccess.RequiresAdministratorSignIn(environment, configuration), emailSignInEnabled = signIn.Enabled }))
            .WithSummary("Get administrator sign-in availability without exposing approved addresses.");

        app.MapGet("/api/admin/auth/session", async (HttpRequest request, KnowledgeSignInService signIn) =>
        {
            var authorization = request.Headers.Authorization.ToString();
            var profile = authorization.StartsWith("Bearer ", StringComparison.Ordinal)
                ? await signIn.GetProfileAsync(authorization[7..]) : null;
            return profile is null ? Results.Unauthorized() : Results.Ok(profile);
        }).WithSummary("Validate the shared administrator session before rendering protected application pages.");

        foreach (var prefix in new[] { "/api/admin/auth", "/api/knowledge/auth" })
        {
            app.MapPost(prefix + "/request-code", async (
                KnowledgeCodeRequest request, KnowledgeSignInService signIn, ILoggerFactory logs, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                {
                    await signIn.RequestCodeAsync(request.Email, cancellationToken);
                    return Results.Accepted(value: new { message = "If this email is approved, a code will arrive shortly. Check your spam folder. You can request another code after one minute." });
                }, logs))
                .RequireRateLimiting("knowledge-sign-in")
                .WithSummary("Email a five-minute, single-use code to an approved application administrator.");

            app.MapPost(prefix + "/verify-code", async (
                KnowledgeCodeVerification request, KnowledgeSignInService signIn, ILoggerFactory logs, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () => Results.Ok(await signIn.VerifyCodeAsync(request.Email, request.Code, cancellationToken)), logs))
                .RequireRateLimiting("knowledge-sign-in")
                .WithSummary("Exchange an administrator's email code for an eight-hour application administrator session.");
        }
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action, ILoggerFactory logs)
    {
        var logger = logs.CreateLogger("KnowledgeSignIn");
        try
        {
            return await action();
        }
        catch (KnowledgeSignInException exception)
        {
            SignInRejected(logger, exception.StatusCode);
            return Results.Problem(statusCode: exception.StatusCode, title: exception.Message);
        }
        catch (Exception exception) when (exception is Azure.RequestFailedException or HttpRequestException or
            Azure.Identity.AuthenticationFailedException or Azure.Identity.CredentialUnavailableException or InvalidOperationException)
        {
            SignInUnavailable(logger, exception);
            return Results.Problem(statusCode: 502, title: "Email sign-in could not reach storage or the email service. Please retry shortly.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Knowledge Center sign-in rejected with HTTP {StatusCode}.")]
    private static partial void SignInRejected(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Knowledge Center email sign-in service failed.")]
    private static partial void SignInUnavailable(ILogger logger, Exception exception);
}
