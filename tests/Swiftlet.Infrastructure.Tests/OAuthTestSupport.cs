using System.Text;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Accounts;
using Swiftlet.Infrastructure.OAuth;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// An HTTP handler answering from a delegate and recording each request with its form body.
/// </summary>
/// <param name="respond">Produces the response for a request and its body text.</param>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    /// <summary>
    /// Gets the recorded requests as (URL, body) pairs.
    /// </summary>
    public List<(Uri Url, string Body)> Requests { get; } = [];

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add((request.RequestUri!, body));
        }

        return respond(request, body);
    }

    /// <summary>
    /// Creates a handler that always answers with the given JSON.
    /// </summary>
    /// <param name="json">The response body.</param>
    /// <param name="status">The status code.</param>
    /// <returns>The handler.</returns>
    public static StubHttpHandler Json(string json, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
    {
        return new StubHttpHandler((_, _) => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>
/// A browser launcher that records the address and lets the test act as the user.
/// </summary>
internal sealed class FakeBrowserLauncher : IBrowserLauncher
{
    /// <summary>
    /// Gets or sets what happens when the browser is asked to open the sign-in page.
    /// </summary>
    public Func<Uri, Task>? OnOpen { get; set; }

    /// <summary>
    /// Gets the opened addresses.
    /// </summary>
    public List<Uri> Opened { get; } = [];

    /// <summary>
    /// Gets the task started by <see cref="OnOpen"/>, so tests can await the simulated user.
    /// </summary>
    public Task? UserAction { get; private set; }

    /// <inheritdoc />
    public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        Opened.Add(uri);

        // The user's browser acts concurrently; awaiting it here would deadlock because the callback server is not accepting yet.
        UserAction = OnOpen is null ? Task.CompletedTask : Task.Run(() => OnOpen(uri), cancellationToken);
        return Task.CompletedTask;
    }
}

/// <summary>
/// An authorizer returning a scripted result.
/// </summary>
internal sealed class FakeOAuthAuthorizer : IOAuthAuthorizer
{
    /// <summary>
    /// Gets or sets the result returned.
    /// </summary>
    public OAuthAuthorization? Result { get; set; }

    /// <summary>
    /// Gets or sets an exception thrown instead of returning a result.
    /// </summary>
    public Exception? Failure { get; set; }

    /// <summary>
    /// Gets the providers asked for.
    /// </summary>
    public List<OAuthProvider> Requested { get; } = [];

    /// <summary>
    /// Gets or sets an action run while the "browser" is open, before the result is returned.
    /// </summary>
    public Action? OnAuthorize { get; set; }

    /// <inheritdoc />
    public Task<OAuthAuthorization> AuthorizeAsync(OAuthProvider provider, CancellationToken cancellationToken = default)
    {
        Requested.Add(provider);
        OnAuthorize?.Invoke();
        return Failure is null ? Task.FromResult(Result!) : Task.FromException<OAuthAuthorization>(Failure);
    }
}

/// <summary>
/// Helpers for building OAuth test data.
/// </summary>
internal static class OAuthTestData
{
    /// <summary>
    /// A client configuration with both providers configured.
    /// </summary>
    public static readonly OAuthClientConfiguration Configured = new("google-id", "google-secret", "microsoft-id");

    /// <summary>
    /// Builds an unsigned JWT with the given payload.
    /// </summary>
    /// <param name="payloadJson">The payload JSON.</param>
    /// <returns>The token.</returns>
    public static string Jwt(string payloadJson)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"e30.{payload}.sig";
    }

    /// <summary>
    /// Builds a token endpoint response body.
    /// </summary>
    /// <param name="accessToken">The access token.</param>
    /// <param name="refreshToken">The refresh token, or <see langword="null"/> to omit it.</param>
    /// <param name="idToken">The ID token, or <see langword="null"/> to omit it.</param>
    /// <param name="expiresIn">The lifetime in seconds.</param>
    /// <returns>The JSON.</returns>
    public static string TokenJson(string accessToken, string? refreshToken = "refresh-1", string? idToken = null, int expiresIn = 3600)
    {
        var parts = new List<string> { $"\"access_token\":\"{accessToken}\"", $"\"expires_in\":{expiresIn}" };
        if (refreshToken is not null)
        {
            parts.Add($"\"refresh_token\":\"{refreshToken}\"");
        }

        if (idToken is not null)
        {
            parts.Add($"\"id_token\":\"{idToken}\"");
        }

        return $"{{{string.Join(',', parts)}}}";
    }

    /// <summary>
    /// Creates an OAuth account.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="email">The email address.</param>
    /// <returns>The account.</returns>
    public static MailAccount OAuthAccount(OAuthProvider provider = OAuthProvider.Google, string email = "me@gmail.com")
    {
        return MailAccount.Create(
            "Me",
            email,
            AuthMethod.OAuth2,
            new ServerEndpoint("imap.gmail.com", 993, true),
            new ServerEndpoint("smtp.gmail.com", 465, true),
            DateTimeOffset.UtcNow,
            provider);
    }
}
