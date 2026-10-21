using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AgenticHotelBooking.Api;
using AgenticHotelBooking.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AgenticHotelBooking.IntegrationTests;

public sealed class KnowledgeSignInTests
{
    private const string Admin = "admin@example.test";

    [Theory]
    [InlineData("GET", "/api/knowledge/blocks")]
    [InlineData("GET", "/api/admin/auth/session")]
    [InlineData("POST", "/api/work-item-chat")]
    [InlineData("POST", "/API/WORK-ITEM-CHAT/")]
    [InlineData("POST", "/api/work-items")]
    [InlineData("POST", "/api/work-items/")]
    [InlineData("GET", "/api/delivery/status")]
    [InlineData("GET", "/api/delivery/status/")]
    [InlineData("GET", "/api/delivery/approvals/77?workItemId=975")]
    [InlineData("GET", "/API/DELIVERY/APPROVALS/77/?workItemId=975")]
    [InlineData("POST", "/api/delivery/approvals")]
    [InlineData("POST", "/API/DELIVERY/APPROVALS/")]
    [InlineData("GET", "/api/operations/events")]
    [InlineData("GET", "/api/operations/events/")]
    [InlineData("POST", "/hubs/operations/negotiate?negotiateVersion=1")]
    public async Task AdministratorApisRejectAnonymousForgedAndMcpCredentials(string method, string path)
    {
        using var fixture = new Fixture();
        using var client = fixture.App.CreateClient();
        foreach (var token in new string?[] { null, "forged-session", new string('r', 64) })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagementDerivedSignatureCannotForgeAnEmailVerifiedSession(bool includeSessionId)
    {
        using var fixture = new Fixture();
        using var client = fixture.App.CreateClient();
        var forged = ForgeSession(fixture.Clock.GetUtcNow(), fixture.Clock.GetUtcNow().AddHours(8),
            includeSessionId ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) : null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", forged);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/auth/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/delivery/approvals/77?workItemId=975")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/delivery/approvals",
            new AgenticHotelBooking.Application.ApproveDevelopmentRequest(975, 77, new string('a', 40), new string('b', 64)))).StatusCode);
    }

    [Fact]
    public async Task VerifiedSessionCannotBeExtendedUsingManagementSigningMaterial()
    {
        using var fixture = new Fixture();
        var service = fixture.App.Services.GetRequiredService<KnowledgeSignInService>();
        await service.RequestCodeAsync(Admin, default);
        var session = await service.VerifyCodeAsync(Admin, fixture.Email.Code!, default);
        var identifier = new JsonWebTokenHandler().ReadJsonWebToken(session.Token).Id;
        var extended = ForgeSession(fixture.Clock.GetUtcNow(), session.ExpiresAt.AddHours(1), identifier);
        Assert.False(await service.AuthorizeAsync(extended));
        Assert.True(await service.AuthorizeAsync(session.Token));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await service.RequestCodeAsync(Admin, default);
        var second = await service.VerifyCodeAsync(Admin, fixture.Email.Code!, default);
        Assert.True(await service.AuthorizeAsync(session.Token));
        Assert.True(await service.AuthorizeAsync(second.Token));
    }

    private static string ForgeSession(DateTimeOffset now, DateTimeOffset expires, string? sessionId)
    {
        var key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(new string('m', 64)),
            Encoding.UTF8.GetBytes("Hotel Knowledge Center email sign-in v1"));
        var claims = new Dictionary<string, object> { ["email"] = Admin, ["role"] = "KnowledgeAdministrator" };
        if (sessionId is not null) claims["jti"] = sessionId;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "hotel-knowledge-sign-in",
            Audience = "hotel-knowledge-management",
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256)
        });
    }

    [Fact]
    public async Task SharedLoginAuthorizesProfileKnowledgeAndOperationsWhileGuestCatalogRemainsPublic()
    {
        using var fixture = new Fixture();
        using var client = fixture.App.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/hotels")).StatusCode);
        var capabilities = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/admin/auth/capabilities");
        Assert.True(capabilities.GetProperty("requiresSignIn").GetBoolean());
        Assert.True(capabilities.GetProperty("emailSignInEnabled").GetBoolean());
        var send = await client.PostAsJsonAsync("/api/admin/auth/request-code", new { email = Admin });
        Assert.Equal(HttpStatusCode.Accepted, send.StatusCode);
        var verify = await client.PostAsJsonAsync("/api/admin/auth/verify-code", new { email = Admin, code = fixture.Email.Code });
        verify.EnsureSuccessStatusCode();
        var session = await verify.Content.ReadFromJsonAsync<KnowledgeSignInSession>();
        Assert.NotNull(session);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        var profileResponse = await client.GetAsync("/api/admin/auth/session");
        profileResponse.EnsureSuccessStatusCode();
        Assert.Equal("no-store", profileResponse.Headers.CacheControl?.ToString());
        var profile = await profileResponse.Content.ReadFromJsonAsync<AdministratorProfile>();
        Assert.NotNull(profile);
        Assert.Equal(Admin, profile.Email);
        Assert.Equal(session.ExpiresAt.ToUnixTimeSeconds(), profile.ExpiresAt.ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/knowledge/blocks")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/operations/events")).StatusCode);
        var approval = await client.GetFromJsonAsync<AgenticHotelBooking.Application.DevelopmentApprovalState>(
            "/api/delivery/approvals/77?workItemId=975");
        Assert.NotNull(approval);
        Assert.False(approval.CanApprove);
        Assert.Equal("Unavailable", approval.Status);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/delivery/approvals",
            new AgenticHotelBooking.Application.ApproveDevelopmentRequest(975, 77, new string('a', 40)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync("/hubs/operations/negotiate?negotiateVersion=1", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/mcp", new { })).StatusCode);
        fixture.Clock.Advance(TimeSpan.FromHours(8));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/auth/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/operations/events")).StatusCode);
    }

    [Fact]
    public async Task ApprovedEmailReceivesCodeAndSessionAuthorizesManagementButNotMcp()
    {
        using var fixture = new Fixture();
        using var client = fixture.App.CreateClient();
        var response = await client.PostAsJsonAsync("/api/knowledge/auth/request-code", new { email = " ADMIN@example.test " });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Matches("^[0-9]{6}$", fixture.Email.Code!);
        var verify = await client.PostAsJsonAsync("/api/knowledge/auth/verify-code", new { email = Admin, code = fixture.Email.Code });
        verify.EnsureSuccessStatusCode();
        var session = await verify.Content.ReadFromJsonAsync<KnowledgeSignInSession>();
        Assert.NotNull(session);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/knowledge/blocks")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/mcp", new { })).StatusCode);
        var replay = await client.PostAsJsonAsync("/api/knowledge/auth/verify-code", new { email = Admin, code = fixture.Email.Code });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task UnknownEmailGetsSameResponseWithoutEmailOrAccess()
    {
        using var fixture = new Fixture();
        using var client = fixture.App.CreateClient();
        var unknown = await client.PostAsJsonAsync("/api/knowledge/auth/request-code", new { email = "outsider@example.test" });
        var approved = await client.PostAsJsonAsync("/api/knowledge/auth/request-code", new { email = Admin });
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(await approved.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Equal(1, fixture.Email.Sent);
        var verify = await client.PostAsJsonAsync("/api/knowledge/auth/verify-code", new { email = "outsider@example.test", code = fixture.Email.Code });
        Assert.Equal(HttpStatusCode.Unauthorized, verify.StatusCode);
    }

    [Fact]
    public async Task ResendsAreBoundedAndNewCodeInvalidatesPreviousChallenge()
    {
        using var fixture = new Fixture();
        var service = fixture.App.Services.GetRequiredService<KnowledgeSignInService>();
        await service.RequestCodeAsync(Admin, default);
        var oldVersion = fixture.Store.Current!.Version;
        await service.RequestCodeAsync(Admin, default);
        Assert.Equal(1, fixture.Email.Sent);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await service.RequestCodeAsync(Admin, default);
        Assert.Equal(2, fixture.Email.Sent);
        Assert.NotEqual(oldVersion, fixture.Store.Current.Version);
        Assert.Equal(0, fixture.Store.Current.Challenge.Attempts);
    }

    [Fact]
    public async Task FiveWrongGuessesLockChallengeEvenWhenCorrectCodeFollows()
    {
        using var fixture = new Fixture();
        var service = fixture.App.Services.GetRequiredService<KnowledgeSignInService>();
        await service.RequestCodeAsync(Admin, default);
        var wrong = fixture.Email.Code == "000000" ? "000001" : "000000";
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(401, (await Assert.ThrowsAsync<KnowledgeSignInException>(
                () => service.VerifyCodeAsync(Admin, wrong, default))).StatusCode);
        }
        Assert.Equal(401, (await Assert.ThrowsAsync<KnowledgeSignInException>(
            () => service.VerifyCodeAsync(Admin, fixture.Email.Code!, default))).StatusCode);
    }

    [Fact]
    public async Task ExpiredCodesAndSessionsAreDenied()
    {
        using var fixture = new Fixture();
        var service = fixture.App.Services.GetRequiredService<KnowledgeSignInService>();
        await service.RequestCodeAsync(Admin, default);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(401, (await Assert.ThrowsAsync<KnowledgeSignInException>(
            () => service.VerifyCodeAsync(Admin, fixture.Email.Code!, default))).StatusCode);
        await service.RequestCodeAsync(Admin, default);
        var session = await service.VerifyCodeAsync(Admin, fixture.Email.Code!, default);
        Assert.True(await service.AuthorizeAsync(session.Token));
        Assert.False(await service.AuthorizeAsync(session.Token + "tampered"));
        fixture.Clock.Advance(TimeSpan.FromHours(8));
        Assert.False(await service.AuthorizeAsync(session.Token));
    }

    [Fact]
    public async Task ConcurrentVerificationConsumesCodeOnce()
    {
        using var fixture = new Fixture();
        var service = fixture.App.Services.GetRequiredService<KnowledgeSignInService>();
        await service.RequestCodeAsync(Admin, default);
        async Task<bool> Verify()
        {
            try
            {
                await service.VerifyCodeAsync(Admin, fixture.Email.Code!, default);
                return true;
            }
            catch (KnowledgeSignInException)
            {
                return false;
            }
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(Verify)));
        Assert.Single(results, result => result);
    }

    [Fact]
    public async Task MissingConfigurationAndDeliveryFailureAreExplicitFailures()
    {
        using var missing = new Fixture(configured: false);
        using var client = missing.App.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await client.PostAsJsonAsync("/api/knowledge/auth/request-code", new { email = Admin })).StatusCode);
        using var failed = new Fixture();
        failed.Email.Fail = true;
        using var failedClient = failed.App.CreateClient();
        Assert.Equal(HttpStatusCode.BadGateway,
            (await failedClient.PostAsJsonAsync("/api/knowledge/auth/request-code", new { email = Admin })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await failedClient.GetAsync("/api/knowledge/blocks")).StatusCode);
    }

    [Fact]
    public async Task EndpointRateLimitRejectsExcessiveAnonymousRequests()
    {
        using var fixture = new Fixture();
        using var client = fixture.App.CreateClient();
        for (var request = 0; request < 20; request++)
        {
            Assert.Equal(HttpStatusCode.Accepted,
                (await client.PostAsJsonAsync("/api/knowledge/auth/request-code", new { email = "unknown@example.test" })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await client.PostAsJsonAsync("/api/knowledge/auth/request-code", new { email = "unknown@example.test" })).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagementCredentialCannotApproveEvenInDevelopment(bool development)
    {
        using var fixture = new Fixture(development: development);
        using var client = fixture.App.CreateClient();
        client.DefaultRequestHeaders.Add("X-Knowledge-Token", new string('m', 64));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/API/DELIVERY/APPROVALS/77/?workItemId=975")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/API/DELIVERY/APPROVALS/",
                new AgenticHotelBooking.Application.ApproveDevelopmentRequest(975, 77, new string('a', 40)))).StatusCode);
    }

    private sealed class Fixture : IDisposable
    {
        public EmailSender Email { get; } = new();
        public ChallengeStore Store { get; } = new();
        public Clock Clock { get; } = new();
        public WebApplicationFactory<Program> App { get; }
        public Fixture(bool configured = true, bool development = false)
        {
            App = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(development ? "Development" : "Production");
                builder.UseSetting("DevelopmentApproval:GitHubToken", "");
                builder.UseSetting("OperationsAuth:Authority", "https://login.example/tenant/v2.0");
                builder.UseSetting("OperationsAuth:Audience", "operations-test");
                builder.UseSetting("OperationsAuth:RequiredRole", "Operations.Ingest");
                builder.UseSetting("KnowledgeAccess:ManagementToken", new string('m', 64));
                builder.UseSetting("KnowledgeAccess:McpToken", new string('r', 64));
                builder.UseSetting("KnowledgeSignIn:AllowedEmails", configured ? Admin : "");
                builder.UseSetting("KnowledgeSignIn:EmailEndpoint", "https://email.example/");
                builder.UseSetting("KnowledgeSignIn:SenderAddress", "noreply@example.test");
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IKnowledgeEmailSender>(Email);
                    services.AddSingleton<IKnowledgeChallengeStore>(Store);
                    services.AddSingleton<TimeProvider>(Clock);
                    services.AddSingleton<IKnowledgeDocumentStore>(new Documents());
                });
            });
        }
        public void Dispose() => App.Dispose();
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }

    private sealed class EmailSender : IKnowledgeEmailSender
    {
        public string? Code { get; private set; }
        public int Sent { get; private set; }
        public bool Fail { get; set; }
        public Task SendCodeAsync(string email, string code, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new HttpRequestException("Fixture email outage.");
            }
            Code = code;
            Sent++;
            return Task.CompletedTask;
        }
    }

    private sealed class ChallengeStore : IKnowledgeChallengeStore
    {
        private readonly object gate = new();
        private readonly Dictionary<string, VersionedKnowledgeChallenge> challenges = [];
        private readonly Dictionary<string, KnowledgeAdministratorSession> sessions = [];
        public VersionedKnowledgeChallenge? Current { get; private set; }
        public Task<VersionedKnowledgeChallenge?> GetAsync(string key, CancellationToken cancellationToken)
        {
            lock (gate) { return Task.FromResult(challenges.GetValueOrDefault(key)); }
        }
        public Task<bool> TrySaveAsync(string key, KnowledgeEmailChallenge challenge, string? version, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (version != challenges.GetValueOrDefault(key)?.Version) { return Task.FromResult(false); }
                Current = new VersionedKnowledgeChallenge(challenge, Guid.NewGuid().ToString());
                challenges[key] = Current;
                return Task.FromResult(true);
            }
        }
        public Task<KnowledgeAdministratorSession?> GetSessionAsync(string key, CancellationToken cancellationToken)
        {
            lock (gate) { return Task.FromResult(sessions.GetValueOrDefault(key)); }
        }
        public Task<bool> TryCreateSessionAsync(string key, KnowledgeAdministratorSession session, CancellationToken cancellationToken)
        {
            lock (gate) { return Task.FromResult(sessions.TryAdd(key, session)); }
        }
    }

    private sealed class Documents : IKnowledgeDocumentStore
    {
        public Task<IReadOnlyList<KnowledgeDocument>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnowledgeDocument>>([]);
        public Task<KnowledgeDocument?> GetAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveAsync(KnowledgeDocument document, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IKnowledgeWriteLease> LockAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
