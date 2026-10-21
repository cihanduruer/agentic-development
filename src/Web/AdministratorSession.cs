using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;

namespace AgenticHotelBooking.Web;

public sealed class AdministratorSession(HttpClient signInHttp, IJSRuntime js) : IDisposable
{
    private const string StorageKey = "hotel.knowledge.session";
    private Task? initialization;
    public event Action? Changed;
    public bool RequiresSignIn { get; private set; } = true;
    public bool EmailSignInEnabled { get; private set; }
    public bool IsAuthenticated { get; private set; }
    public string Token { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    public async Task InitializeAsync()
    {
        await (initialization ??= InitializeCoreAsync());
        if (RequiresSignIn && IsAuthenticated && ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await SignOutAsync("Your session has expired. Sign in again to continue.");
        }
    }

    public async Task RetryAsync()
    {
        initialization = null;
        await InitializeAsync();
    }

    private async Task InitializeCoreAsync()
    {
        Error = null;
        try
        {
            var capabilities = await signInHttp.GetFromJsonAsync<Capabilities>("api/admin/auth/capabilities")
                ?? throw new JsonException("Empty administrator capabilities response.");
            RequiresSignIn = capabilities.RequiresSignIn;
            EmailSignInEnabled = capabilities.EmailSignInEnabled;
            if (!RequiresSignIn)
            {
                IsAuthenticated = true;
                return;
            }
            try
            {
                Token = await js.InvokeAsync<string?>("sessionStorage.getItem", StorageKey) ?? string.Empty;
            }
            catch (JSException)
            {
                Notice = "Browser storage is unavailable. Sign-in will last for this page only.";
            }
            if (Token.Length > 0)
            {
                await LoadProfileAsync();
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            IsAuthenticated = false;
            Token = string.Empty;
            Error = "Administrator access could not be verified. Please retry.";
        }
    }

    private async Task<bool> LoadProfileAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/admin/auth/session");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var response = await signInHttp.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await SignOutAsync("Your session has expired. Sign in again to continue.");
            return false;
        }
        response.EnsureSuccessStatusCode();
        var profile = await response.Content.ReadFromJsonAsync<Profile>() ?? throw new JsonException("Empty administrator profile.");
        if (string.IsNullOrWhiteSpace(profile.Email) || profile.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new JsonException("Invalid administrator profile.");
        }
        Email = profile.Email;
        ExpiresAt = profile.ExpiresAt;
        IsAuthenticated = true;
        Changed?.Invoke();
        return true;
    }

    public async Task<bool> RequestCodeAsync(string email)
    {
        Error = null;
        try
        {
            using var response = await signInHttp.PostAsJsonAsync("api/admin/auth/request-code", new { email });
            if (!response.IsSuccessStatusCode)
            {
                Error = await ReadErrorAsync(response, "The code could not be sent. Please wait a minute and retry.");
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            Error = "The sign-in service could not be reached. Please retry.";
            return false;
        }
    }

    public async Task<bool> VerifyCodeAsync(string email, string code)
    {
        Error = null;
        try
        {
            using var response = await signInHttp.PostAsJsonAsync("api/admin/auth/verify-code", new { email, code });
            if (!response.IsSuccessStatusCode)
            {
                Error = await ReadErrorAsync(response, "The code could not be verified. Please request a new code.");
                return false;
            }
            var session = await response.Content.ReadFromJsonAsync<SessionResponse>() ?? throw new JsonException("Empty sign-in response.");
            if (string.IsNullOrWhiteSpace(session.Token) || session.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                throw new JsonException("Invalid administrator session.");
            }
            Token = session.Token;
            Notice = null;
            if (!await LoadProfileAsync())
            {
                return false;
            }
            try
            {
                await js.InvokeVoidAsync("sessionStorage.setItem", StorageKey, Token);
            }
            catch (JSException)
            {
                Notice = "Signed in for this page only; your browser could not remember the session.";
            }
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            Token = string.Empty;
            IsAuthenticated = false;
            Error = "Sign-in could not be confirmed. Please retry.";
            return false;
        }
    }

    public async Task SignOutAsync(string? notice = null)
    {
        Token = string.Empty;
        Email = string.Empty;
        ExpiresAt = default;
        IsAuthenticated = false;
        Error = null;
        Notice = notice;
        try
        {
            await js.InvokeVoidAsync("sessionStorage.removeItem", StorageKey);
        }
        catch (JSException)
        {
            Notice = "Signed out of this page. Close this tab to clear any browser-held session.";
        }
        Changed?.Invoke();
    }

    public static bool IsAdministratorApi(Uri uri) =>
        uri.AbsolutePath.StartsWith("/api/admin/", StringComparison.OrdinalIgnoreCase) ||
        uri.AbsolutePath.StartsWith("/api/knowledge/", StringComparison.OrdinalIgnoreCase) ||
        uri.AbsolutePath is "/api/work-item-chat" or "/api/work-items" or "/api/delivery/status" or "/api/operations/events";

    public static string SafeReturnUrl(Uri origin, string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal) ||
            value.Any(char.IsControl) || Uri.UnescapeDataString(value).Contains('\\') ||
            !Uri.TryCreate(origin, value, out var target) || target.Authority != origin.Authority || target.Scheme != origin.Scheme)
        {
            return "/admin";
        }
        var path = target.AbsolutePath.TrimEnd('/');
        return path != "/admin/login" && (path == "/admin" || path.StartsWith("/admin/", StringComparison.Ordinal) ||
            path is "/architecture" or "/operations")
            ? target.PathAndQuery + target.Fragment
            : "/admin";
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, string fallback)
    {
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } message
                ? message : fallback;
        }
        catch (JsonException)
        {
            return $"{fallback} (HTTP {(int)response.StatusCode}.)";
        }
    }

    public void Dispose() => signInHttp.Dispose();
    private sealed record Capabilities(bool RequiresSignIn, bool EmailSignInEnabled);
    private sealed record Profile(string Email, DateTimeOffset ExpiresAt);
    private sealed record SessionResponse(string Token, DateTimeOffset ExpiresAt);
}

public sealed class AdministratorSessionHandler(AdministratorSession session, Uri apiOrigin) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var protectedRequest = request.RequestUri is { IsAbsoluteUri: true } uri &&
            uri.Authority == apiOrigin.Authority && uri.Scheme == apiOrigin.Scheme && AdministratorSession.IsAdministratorApi(uri);
        if (protectedRequest && session.Token.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        }
        var response = await base.SendAsync(request, cancellationToken);
        if (protectedRequest && response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await session.SignOutAsync("Your session has expired. Sign in again to continue.");
        }
        return response;
    }
}
