using AwesomeAssertions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Accounts;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for the browser sign-in commands of <see cref="AddAccountViewModel"/>.
/// </summary>
public class SignInViewModelTests
{
    /// <summary>
    /// Verifies each sign-in button sends the matching provider and closes the dialog on success.
    /// </summary>
    /// <param name="useGoogle">Whether the Google button is pressed.</param>
    /// <param name="expected">The expected provider.</param>
    [Theory]
    [InlineData(true, OAuthProvider.Google)]
    [InlineData(false, OAuthProvider.Microsoft)]
    public async Task SignInCommand_Succeeds_SendsProviderAndCloses(bool useGoogle, OAuthProvider expected)
    {
        var dispatcher = new FakeDispatcher();
        dispatcher.On<AddOAuthAccountCommand>(_ => Guid.NewGuid());
        var viewModel = new AddAccountViewModel(dispatcher, TestLocalizer.Chinese());
        bool? closed = null;
        viewModel.CloseRequested += (_, added) => closed = added;

        await (useGoogle ? viewModel.SignInWithGoogleCommand : viewModel.SignInWithMicrosoftCommand).ExecuteAsync(null);

        closed.Should().BeTrue();
        dispatcher.Sent<AddOAuthAccountCommand>().Should().ContainSingle().Which.Provider.Should().Be(expected);
        viewModel.IsBusy.Should().BeFalse();
        viewModel.ProviderHint.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a missing client id is explained in the current language and keeps the dialog open.
    /// </summary>
    [Fact]
    public async Task SignInCommand_NotConfigured_ShowsLocalizedHint()
    {
        var dispatcher = new FakeDispatcher();
        dispatcher.On<AddOAuthAccountCommand>(_ => throw new OAuthNotConfiguredException(OAuthProvider.Google, "Edit oauth.json."));
        var chinese = new AddAccountViewModel(dispatcher, TestLocalizer.Chinese());
        var english = new AddAccountViewModel(dispatcher, TestLocalizer.English());
        var closed = false;
        chinese.CloseRequested += (_, _) => closed = true;

        await chinese.SignInWithGoogleCommand.ExecuteAsync(null);
        await english.SignInWithGoogleCommand.ExecuteAsync(null);

        chinese.ErrorMessage.Should().Be("尚未配置 Google 登录。Edit oauth.json.");
        english.ErrorMessage.Should().Be("Google sign-in is not configured yet. Edit oauth.json.");
        closed.Should().BeFalse();
    }

    /// <summary>
    /// Verifies other sign-in failures are shown with their reason and leave the dialog usable.
    /// </summary>
    [Fact]
    public async Task SignInCommand_Fails_ShowsReasonAndReenablesButtons()
    {
        var dispatcher = new FakeDispatcher();
        dispatcher.On<AddOAuthAccountCommand>(_ => throw new OAuthException("access_denied"));
        var viewModel = new AddAccountViewModel(dispatcher, TestLocalizer.English());

        await viewModel.SignInWithMicrosoftCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.Should().Be("Sign-in failed: access_denied");
        viewModel.IsBusy.Should().BeFalse();
        viewModel.SignInWithGoogleCommand.CanExecute(null).Should().BeTrue();
    }

    /// <summary>
    /// Verifies the buttons are disabled while a sign-in is pending and cancelling abandons it and closes the dialog.
    /// </summary>
    [Fact]
    public async Task CancelCommand_WhileWaitingForBrowser_CancelsSignInAndCloses()
    {
        var started = new TaskCompletionSource();
        var dispatcher = new FakeDispatcher();
        dispatcher.OnAsync<AddOAuthAccountCommand>(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Guid.Empty;
        });
        var viewModel = new AddAccountViewModel(dispatcher, TestLocalizer.Chinese());
        var closed = new List<bool>();
        viewModel.CloseRequested += (_, added) => closed.Add(added);

        var pending = viewModel.SignInWithGoogleCommand.ExecuteAsync(null);
        await started.Task;
        viewModel.IsBusy.Should().BeTrue();
        viewModel.ProviderHint.Should().Contain("浏览器");
        viewModel.SignInWithMicrosoftCommand.CanExecute(null).Should().BeFalse();

        viewModel.CancelCommand.Execute(null);
        await pending;

        closed.Should().Equal(false);
        viewModel.ErrorMessage.Should().BeEmpty();
        viewModel.IsBusy.Should().BeFalse();
    }
}
