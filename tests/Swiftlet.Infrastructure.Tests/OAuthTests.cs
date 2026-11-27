using System.Net;
using System.Web;
using AwesomeAssertions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Accounts;
using Swiftlet.Infrastructure.OAuth;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for the OAuth 2.0 configuration, PKCE, token client, authorizer and token service.
/// </summary>
public class OAuthTests
{
    /// <summary>
    /// Verifies the PKCE challenge matches the example of RFC 7636 appendix B.
    /// </summary>
    [Fact]
    public void CreateChallenge_Rfc7636Example_MatchesExpectedValue()
    {
        Pkce.CreateChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")
            .Should().Be("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
        Pkce.CreateRandomString(64).Should().HaveLength(86).And.MatchRegex("^[A-Za-z0-9_-]+$");
    }

    /// <summary>
    /// Verifies environment variables override the settings file and a broken file counts as unconfigured.
    /// </summary>
    [Fact]
    public void Load_FileAndEnvironment_EnvironmentWinsAndBrokenFileIsIgnored()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"swiftlet-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "oauth.json");
        try
        {
            File.WriteAllText(path, """{"google":{"clientId":"file-google","clientSecret":"file-secret"},"microsoft":{"clientId":"file-ms"}}""");
            var fromFile = OAuthClientConfiguration.Load(path, _ => null);
            fromFile.ClientIdFor(OAuthProvider.Google).Should().Be("file-google");
            fromFile.ClientSecretFor(OAuthProvider.Google).Should().Be("file-secret");
            fromFile.ClientSecretFor(OAuthProvider.Microsoft).Should().BeNull();

            var merged = OAuthClientConfiguration.Load(
                path,
                name => name == OAuthClientConfiguration.MicrosoftClientIdVariable ? "env-ms" : null);
            merged.ClientIdFor(OAuthProvider.Microsoft).Should().Be("env-ms");
            merged.ClientIdFor(OAuthProvider.Google).Should().Be("file-google");

            File.WriteAllText(path, "{ not json");
            OAuthClientConfiguration.Load(path, _ => null).ClientIdFor(OAuthProvider.Google).Should().BeNull();
            OAuthClientConfiguration.Load(Path.Combine(directory, "missing.json"), _ => "  ").ClientIdFor(OAuthProvider.Google).Should().BeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Verifies the identity is read from the ID token, preferring the email claim.
    /// </summary>
    [Fact]
    public void ReadIdentity_IdToken_ReturnsEmailAndName()
    {
        OAuthTokenClient.ReadIdentity(OAuthTestData.Jwt("""{"email":"a@gmail.com","name":"Alice"}"""))
            .Should().Be(("a@gmail.com", "Alice"));
        OAuthTokenClient.ReadIdentity(OAuthTestData.Jwt("""{"preferred_username":"b@outlook.com"}"""))
            .Should().Be(("b@outlook.com", null));
        OAuthTokenClient.ReadIdentity(null).Should().Be((null, null));
        OAuthTokenClient.ReadIdentity("not-a-jwt").Should().Be((null, null));
    }

    /// <summary>
    /// Verifies the built-in Microsoft client id applies only when neither the environment nor the file sets one.
    /// </summary>
    [Fact]
    public void Load_NothingConfigured_UsesBuiltInMicrosoftClientIdOnly()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"swiftlet-test-{Guid.NewGuid():N}.json");

        var defaults = OAuthClientConfiguration.Load(missing, _ => null);
        var overridden = OAuthClientConfiguration.Load(
            missing,
            name => name == OAuthClientConfiguration.MicrosoftClientIdVariable ? "env-ms" : null);

        defaults.ClientIdFor(OAuthProvider.Microsoft).Should().Be(OAuthClientConfiguration.BuiltInMicrosoftClientId);
        defaults.ClientIdFor(OAuthProvider.Google).Should().BeNull();
        defaults.ClientSecretFor(OAuthProvider.Google).Should().BeNull();
        overridden.ClientIdFor(OAuthProvider.Microsoft).Should().Be("env-ms");
    }

    /// <summary>
    /// Verifies only a tenant id other than the personal-account tenant marks an organization account.
    /// </summary>
    /// <param name="payload">The ID token payload.</param>
    /// <param name="expected">Whether it is an organization account.</param>
    [Theory]
    [InlineData("""{"tid":"11111111-2222-3333-4444-555555555555"}""", true)]
    [InlineData("""{"tid":"9188040d-6c67-4c5b-b112-36a304b66dad"}""", false)]
    [InlineData("""{"tid":"9188040D-6C67-4C5B-B112-36A304B66DAD"}""", false)]
    [InlineData("""{"tid":""}""", false)]
    [InlineData("""{"email":"a@outlook.com"}""", false)]
    public void IsOrganizationAccount_TenantId_DistinguishesPersonalAccounts(string payload, bool expected)
    {
        OAuthTokenClient.IsOrganizationAccount(OAuthTestData.Jwt(payload)).Should().Be(expected);
    }

    /// <summary>
    /// Verifies the sign-in result marks organization accounts for Microsoft only.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="expected">Whether the result is an organization account.</param>
    [Theory]
    [InlineData(OAuthProvider.Microsoft, true)]
    [InlineData(OAuthProvider.Google, false)]
    public async Task AuthorizeAsync_TokenWithTenant_MarksMicrosoftOrganizationAccount(OAuthProvider provider, bool expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var idToken = OAuthTestData.Jwt("""{"email":"me@contoso.example","tid":"11111111-2222-3333-4444-555555555555"}""");
        var handler = StubHttpHandler.Json(OAuthTestData.TokenJson("access-1", "refresh-1", idToken));
        var browser = new FakeBrowserLauncher { OnOpen = uri => FollowRedirectAsync(uri, state => $"code=abc&state={state}") };

        var result = await CreateAuthorizer(handler, browser, OAuthTestData.Configured).AuthorizeAsync(provider, ct);
        await browser.UserAction!;

        result.IsOrganizationAccount.Should().Be(expected);
    }

    /// <summary>
    /// Verifies the code exchange sends the PKCE verifier and, for Google only, the client secret.
    /// </summary>
    [Fact]
    public async Task ExchangeCodeAsync_Providers_SendExpectedFormFields()
    {
        var ct = TestContext.Current.CancellationToken;
        var google = StubHttpHandler.Json(OAuthTestData.TokenJson("at-1"));
        var microsoft = StubHttpHandler.Json(OAuthTestData.TokenJson("at-2"));
        var clock = new ManualTimeProvider();

        var googleResult = await new OAuthTokenClient(new HttpClient(google), OAuthTestData.Configured, clock)
            .ExchangeCodeAsync(OAuthProvider.Google, "the-code", "the-verifier", "http://127.0.0.1:5000/", ct);
        await new OAuthTokenClient(new HttpClient(microsoft), OAuthTestData.Configured, clock)
            .ExchangeCodeAsync(OAuthProvider.Microsoft, "the-code", "the-verifier", "http://localhost:5000/", ct);

        googleResult.AccessToken.Should().Be("at-1");
        googleResult.ExpiresAt.Should().Be(clock.Now.AddHours(1));
        var googleForm = HttpUtility.ParseQueryString(google.Requests.Single().Body);
        google.Requests.Single().Url.Host.Should().Be("oauth2.googleapis.com");
        googleForm["grant_type"].Should().Be("authorization_code");
        googleForm["code"].Should().Be("the-code");
        googleForm["code_verifier"].Should().Be("the-verifier");
        googleForm["client_id"].Should().Be("google-id");
        googleForm["client_secret"].Should().Be("google-secret");

        var microsoftForm = HttpUtility.ParseQueryString(microsoft.Requests.Single().Body);
        microsoft.Requests.Single().Url.Host.Should().Be("login.microsoftonline.com");
        microsoftForm["client_id"].Should().Be("microsoft-id");
        microsoftForm["client_secret"].Should().BeNull();
    }

    /// <summary>
    /// Verifies OAuth errors from the token endpoint surface as typed exceptions.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_ErrorResponse_ThrowsTokenException()
    {
        var handler = StubHttpHandler.Json("""{"error":"invalid_grant","error_description":"Token has been revoked."}""", HttpStatusCode.BadRequest);
        var client = new OAuthTokenClient(new HttpClient(handler), OAuthTestData.Configured, new ManualTimeProvider());

        var act = async () => await client.RefreshAsync(OAuthProvider.Google, "rt", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<OAuthTokenException>()).Which.Error.Should().Be("invalid_grant");
    }

    /// <summary>
    /// Creates an authorizer whose browser simulates the user finishing the sign-in.
    /// </summary>
    /// <param name="handler">The HTTP handler for the token endpoint.</param>
    /// <param name="browser">The fake browser.</param>
    /// <param name="configuration">The client configuration.</param>
    /// <returns>The authorizer.</returns>
    private static OAuthAuthorizer CreateAuthorizer(StubHttpHandler handler, FakeBrowserLauncher browser, OAuthClientConfiguration configuration)
    {
        var tokenClient = new OAuthTokenClient(new HttpClient(handler), configuration, new ManualTimeProvider());
        return new OAuthAuthorizer(tokenClient, browser, configuration, "C:\\settings\\oauth.json");
    }

    /// <summary>
    /// Simulates the browser: follows the redirect of the authorization request back to the local server.
    /// </summary>
    /// <param name="authorizationUri">The opened sign-in address.</param>
    /// <param name="parameters">The query string of the redirect to send, built from the request's redirect address and state.</param>
    /// <returns>A task that completes when the callback server answered.</returns>
    private static async Task FollowRedirectAsync(Uri authorizationUri, Func<string, string> parameters)
    {
        var query = HttpUtility.ParseQueryString(authorizationUri.Query);
        var redirect = query["redirect_uri"]!;
        using var http = new HttpClient();
        using var response = await http.GetAsync($"{redirect}?{parameters(query["state"]!)}");
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Verifies the full sign-in for both providers: authorization request, redirect, code exchange and identity.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="redirectHost">The expected redirect host.</param>
    /// <param name="scopePart">A scope that must be requested.</param>
    [Theory]
    [InlineData(OAuthProvider.Google, "127.0.0.1", "https://mail.google.com/")]
    [InlineData(OAuthProvider.Microsoft, "localhost", "IMAP.AccessAsUser.All")]
    public async Task AuthorizeAsync_UserCompletesSignIn_ReturnsIdentityAndTokens(OAuthProvider provider, string redirectHost, string scopePart)
    {
        var ct = TestContext.Current.CancellationToken;
        var idToken = OAuthTestData.Jwt("""{"email":"me@example.com","name":"Me Myself"}""");
        var handler = StubHttpHandler.Json(OAuthTestData.TokenJson("access-1", "refresh-1", idToken));
        var browser = new FakeBrowserLauncher { OnOpen = uri => FollowRedirectAsync(uri, state => $"code=abc&state={state}") };

        var result = await CreateAuthorizer(handler, browser, OAuthTestData.Configured).AuthorizeAsync(provider, ct);
        await browser.UserAction!;

        result.EmailAddress.Should().Be("me@example.com");
        result.DisplayName.Should().Be("Me Myself");
        result.Tokens.AccessToken.Should().Be("access-1");
        result.Tokens.RefreshToken.Should().Be("refresh-1");

        var request = HttpUtility.ParseQueryString(browser.Opened.Single().Query);
        request["response_type"].Should().Be("code");
        request["code_challenge_method"].Should().Be("S256");
        request["scope"].Should().Contain(scopePart);
        new Uri(request["redirect_uri"]!).Host.Should().Be(redirectHost);

        var exchange = HttpUtility.ParseQueryString(handler.Requests.Single().Body);
        exchange["code"].Should().Be("abc");
        Pkce.CreateChallenge(exchange["code_verifier"]!).Should().Be(request["code_challenge"]);
        exchange["redirect_uri"].Should().Be(request["redirect_uri"]);
    }

    /// <summary>
    /// Verifies a response with a wrong state is rejected before any token request.
    /// </summary>
    [Fact]
    public async Task AuthorizeAsync_StateMismatch_ThrowsWithoutTokenRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = StubHttpHandler.Json(OAuthTestData.TokenJson("a"));
        var browser = new FakeBrowserLauncher { OnOpen = uri => FollowRedirectAsync(uri, _ => "code=abc&state=forged") };

        var act = async () => await CreateAuthorizer(handler, browser, OAuthTestData.Configured).AuthorizeAsync(OAuthProvider.Google, ct);

        (await act.Should().ThrowAsync<OAuthException>()).Which.Message.Should().Contain("state");
        handler.Requests.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies an error returned by the provider (for example the user denying access) is reported.
    /// </summary>
    [Fact]
    public async Task AuthorizeAsync_UserDenies_ThrowsProviderError()
    {
        var ct = TestContext.Current.CancellationToken;
        var browser = new FakeBrowserLauncher { OnOpen = uri => FollowRedirectAsync(uri, state => $"error=access_denied&state={state}") };

        var act = async () => await CreateAuthorizer(StubHttpHandler.Json("{}"), browser, OAuthTestData.Configured)
            .AuthorizeAsync(OAuthProvider.Google, ct);

        (await act.Should().ThrowAsync<OAuthException>()).Which.Message.Should().Contain("access_denied");
    }

    /// <summary>
    /// Verifies a provider without a client id fails with a hint, before opening the browser.
    /// </summary>
    [Fact]
    public async Task AuthorizeAsync_NotConfigured_ThrowsWithHintAndOpensNothing()
    {
        var browser = new FakeBrowserLauncher();
        var authorizer = CreateAuthorizer(StubHttpHandler.Json("{}"), browser, new OAuthClientConfiguration(null, null, null));

        var act = async () => await authorizer.AuthorizeAsync(OAuthProvider.Microsoft, TestContext.Current.CancellationToken);

        var exception = (await act.Should().ThrowAsync<OAuthNotConfiguredException>()).Which;
        exception.Provider.Should().Be(OAuthProvider.Microsoft);
        exception.Hint.Should().Contain("oauth.json").And.Contain(OAuthClientConfiguration.MicrosoftClientIdVariable);
        browser.Opened.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a missing refresh token or email is reported instead of creating a half-working account.
    /// </summary>
    /// <param name="refreshToken">The refresh token in the token response, or <see langword="null"/>.</param>
    /// <param name="idPayload">The ID token payload.</param>
    /// <param name="expectedMessage">A fragment of the expected error.</param>
    [Theory]
    [InlineData(null, """{"email":"me@example.com"}""", "refresh token")]
    [InlineData("rt", """{"name":"No Email"}""", "email address")]
    public async Task AuthorizeAsync_IncompleteResponse_Throws(string? refreshToken, string idPayload, string expectedMessage)
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = StubHttpHandler.Json(OAuthTestData.TokenJson("a", refreshToken, OAuthTestData.Jwt(idPayload)));
        var browser = new FakeBrowserLauncher { OnOpen = uri => FollowRedirectAsync(uri, state => $"code=abc&state={state}") };

        var act = async () => await CreateAuthorizer(handler, browser, OAuthTestData.Configured).AuthorizeAsync(OAuthProvider.Google, ct);

        (await act.Should().ThrowAsync<OAuthException>()).Which.Message.Should().Contain(expectedMessage);
    }

    /// <summary>
    /// Verifies cancelling while the browser sign-in is pending stops the wait.
    /// </summary>
    [Fact]
    public async Task AuthorizeAsync_CancelledWhileWaiting_ThrowsOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        var browser = new FakeBrowserLauncher { OnOpen = _ => Task.CompletedTask };
        var authorizer = CreateAuthorizer(StubHttpHandler.Json("{}"), browser, OAuthTestData.Configured);

        var pending = authorizer.AuthorizeAsync(OAuthProvider.Google, cancellation.Token);
        await cancellation.CancelAsync();

        await pending.Invoking(p => p).Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Verifies the callback server answers unrelated requests with 404 and keeps waiting for the real redirect.
    /// </summary>
    [Fact]
    public async Task WaitForCallbackAsync_UnrelatedRequestFirst_IgnoresItAndReturnsParameters()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = LoopbackCallbackServer.Start();
        var waiting = server.WaitForCallbackAsync(ct);
        using var http = new HttpClient();

        using var favicon = await http.GetAsync($"http://127.0.0.1:{server.Port}/favicon.ico", ct);
        using var redirect = await http.GetAsync($"http://127.0.0.1:{server.Port}/?code=a%20b&state=xyz", ct);
        var parameters = await waiting;

        favicon.StatusCode.Should().Be(HttpStatusCode.NotFound);
        redirect.StatusCode.Should().Be(HttpStatusCode.OK);
        parameters["code"].Should().Be("a b");
        parameters["state"].Should().Be("xyz");
    }

    /// <summary>
    /// Creates an account with stored tokens and a token service over a fake token endpoint.
    /// </summary>
    /// <param name="handler">The token endpoint handler.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="expiresIn">The remaining lifetime of the stored access token.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The service, the secret store and the account.</returns>
    private static async Task<(OAuthTokenService Service, InMemorySecretStore Secrets, MailAccount Account)> CreateServiceAsync(
        StubHttpHandler handler,
        ManualTimeProvider clock,
        TimeSpan expiresIn,
        CancellationToken ct)
    {
        var secrets = new InMemorySecretStore();
        var account = OAuthTestData.OAuthAccount();
        await OAuthTokenStorage.SaveAsync(secrets, account.Id, new OAuthTokens("old-access", "old-refresh", clock.Now.Add(expiresIn)), ct);
        var client = new OAuthTokenClient(new HttpClient(handler), OAuthTestData.Configured, clock);
        return (new OAuthTokenService(secrets, client, clock), secrets, account);
    }

    /// <summary>
    /// Verifies a token that is still valid is returned without contacting the provider.
    /// </summary>
    [Fact]
    public async Task GetAccessTokenAsync_TokenStillValid_ReturnsStoredWithoutRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = StubHttpHandler.Json(OAuthTestData.TokenJson("new"));
        var (service, _, account) = await CreateServiceAsync(handler, new ManualTimeProvider(), TimeSpan.FromMinutes(30), ct);

        (await service.GetAccessTokenAsync(account, ct)).Should().Be("old-access");

        handler.Requests.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a token about to expire is refreshed and the rotated refresh token is saved.
    /// </summary>
    [Fact]
    public async Task GetAccessTokenAsync_TokenAboutToExpire_RefreshesAndSavesRotatedToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider();
        var handler = StubHttpHandler.Json(OAuthTestData.TokenJson("new-access", "new-refresh"));
        var (service, secrets, account) = await CreateServiceAsync(handler, clock, TimeSpan.FromSeconds(30), ct);

        (await service.GetAccessTokenAsync(account, ct)).Should().Be("new-access");

        var form = HttpUtility.ParseQueryString(handler.Requests.Single().Body);
        form["grant_type"].Should().Be("refresh_token");
        form["refresh_token"].Should().Be("old-refresh");
        var saved = await OAuthTokenStorage.LoadAsync(secrets, account.Id, ct);
        saved.Should().Be(new OAuthTokens("new-access", "new-refresh", clock.Now.AddHours(1)));
    }

    /// <summary>
    /// Verifies the old refresh token is kept when the provider does not return a new one (Google).
    /// </summary>
    [Fact]
    public async Task GetAccessTokenAsync_NoNewRefreshToken_KeepsOldOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = StubHttpHandler.Json(OAuthTestData.TokenJson("new-access", refreshToken: null));
        var (service, secrets, account) = await CreateServiceAsync(handler, new ManualTimeProvider(), TimeSpan.Zero, ct);

        await service.GetAccessTokenAsync(account, ct);

        (await OAuthTokenStorage.LoadAsync(secrets, account.Id, ct))!.RefreshToken.Should().Be("old-refresh");
    }

    /// <summary>
    /// Verifies concurrent callers share a single refresh.
    /// </summary>
    [Fact]
    public async Task GetAccessTokenAsync_ConcurrentCallers_RefreshOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = StubHttpHandler.Json(OAuthTestData.TokenJson("new-access"));
        var (service, _, account) = await CreateServiceAsync(handler, new ManualTimeProvider(), TimeSpan.Zero, ct);

        var tokens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => service.GetAccessTokenAsync(account, ct)));

        tokens.Should().OnlyContain(t => t == "new-access");
        handler.Requests.Should().ContainSingle();
    }

    /// <summary>
    /// Verifies a revoked refresh token asks the user to sign in again.
    /// </summary>
    [Fact]
    public async Task GetAccessTokenAsync_RefreshTokenRevoked_RequiresReauthentication()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = StubHttpHandler.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        var (service, _, account) = await CreateServiceAsync(handler, new ManualTimeProvider(), TimeSpan.Zero, ct);

        var act = async () => await service.GetAccessTokenAsync(account, ct);

        (await act.Should().ThrowAsync<OAuthReauthenticationRequiredException>()).Which.EmailAddress.Should().Be("me@gmail.com");
    }

    /// <summary>
    /// Verifies missing tokens and non-OAuth accounts are rejected.
    /// </summary>
    [Fact]
    public async Task GetAccessTokenAsync_NoTokensOrPasswordAccount_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider();
        var client = new OAuthTokenClient(new HttpClient(StubHttpHandler.Json("{}")), OAuthTestData.Configured, clock);
        var service = new OAuthTokenService(new InMemorySecretStore(), client, clock);

        var noTokens = async () => await service.GetAccessTokenAsync(OAuthTestData.OAuthAccount(), ct);
        var password = async () => await service.GetAccessTokenAsync(TestData.Account("me@example.com"), ct);

        await noTokens.Should().ThrowAsync<OAuthReauthenticationRequiredException>();
        await password.Should().ThrowAsync<InvalidOperationException>();
    }
}
