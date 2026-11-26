using AwesomeAssertions;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Infrastructure.Persistence.Encryption;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for <see cref="StartupViewModel"/>.
/// </summary>
public sealed class StartupViewModelTests
{
    /// <summary>
    /// The scripted database protection.
    /// </summary>
    private readonly FakeDatabaseProtection _protection = new();

    /// <summary>
    /// The scripted dialogs.
    /// </summary>
    private readonly FakeDialogService _dialogs = new();

    /// <summary>
    /// How often the view model reported completion.
    /// </summary>
    private int _completed;

    /// <summary>
    /// Creates the view model with English texts.
    /// </summary>
    /// <returns>The view model.</returns>
    private StartupViewModel Create()
    {
        var viewModel = new StartupViewModel(
            _protection,
            TestLocalizer.English(),
            _dialogs,
            new FakeUiThread(),
            _ => Task.FromResult<string?>("me@example.com"));
        viewModel.Completed += (_, _) => _completed++;
        return viewModel;
    }

    /// <summary>
    /// Scripts the next preparation result.
    /// </summary>
    /// <param name="problem">The problem.</param>
    /// <param name="accountId">The account concerned.</param>
    /// <param name="conversion">The conversion failure.</param>
    private void Next(DatabaseProtectionProblem problem, Guid? accountId = null, DatabaseConversionFailure? conversion = null)
    {
        _protection.Results.Enqueue(new DatabaseProtectionResult(problem, accountId, conversion, "detail text"));
    }

    /// <summary>
    /// Verifies a successful preparation completes the startup.
    /// </summary>
    [Fact]
    public async Task RunAsync_Success_RaisesCompleted()
    {
        var viewModel = Create();

        await viewModel.RunCommand.ExecuteAsync(null);

        _completed.Should().Be(1);
        _protection.PrepareCalls.Should().Be(1);
    }

    /// <summary>
    /// Verifies each problem offers exactly the actions that fit it.
    /// </summary>
    /// <param name="problem">The problem.</param>
    /// <param name="canContinue">Whether continuing unencrypted is offered.</param>
    /// <param name="canReset">Whether a reset is offered.</param>
    [Theory]
    [InlineData(DatabaseProtectionProblem.ConversionFailed, true, false)]
    [InlineData(DatabaseProtectionProblem.KeyStoreUnavailable, false, false)]
    [InlineData(DatabaseProtectionProblem.Busy, false, false)]
    [InlineData(DatabaseProtectionProblem.DatabaseError, false, false)]
    [InlineData(DatabaseProtectionProblem.KeyLost, false, true)]
    [InlineData(DatabaseProtectionProblem.AccountUnreadable, false, true)]
    public async Task RunAsync_Problem_ShowsMatchingButtons(DatabaseProtectionProblem problem, bool canContinue, bool canReset)
    {
        Next(problem, Guid.NewGuid());
        var viewModel = Create();

        await viewModel.RunCommand.ExecuteAsync(null);

        viewModel.CanRetry.Should().BeTrue();
        viewModel.CanContinueUnencrypted.Should().Be(canContinue);
        viewModel.CanReset.Should().Be(canReset);
        viewModel.IsBusy.Should().BeFalse();
        viewModel.Message.Should().NotBeEmpty().And.NotContain("!Startup");
        _completed.Should().Be(0);
    }

    /// <summary>
    /// Verifies a keychain missing since the start only offers to quit, because retrying cannot find it.
    /// </summary>
    [Fact]
    public async Task RunAsync_KeyStoreMissing_AsksForRestartWithoutRetry()
    {
        Next(DatabaseProtectionProblem.KeyStoreMissing);
        var viewModel = Create();

        await viewModel.RunCommand.ExecuteAsync(null);

        viewModel.CanRetry.Should().BeFalse();
        viewModel.CanContinueUnencrypted.Should().BeFalse();
        viewModel.CanReset.Should().BeFalse();
        viewModel.Message.Should().Contain("start Swiftlet again");
    }

    /// <summary>
    /// Verifies the reason of a failed conversion is explained.
    /// </summary>
    /// <param name="failure">The failure.</param>
    /// <param name="expected">A part of the expected message.</param>
    [Theory]
    [InlineData(DatabaseConversionFailure.NotEnoughSpace, "free disk space")]
    [InlineData(DatabaseConversionFailure.InUse, "in use")]
    [InlineData(DatabaseConversionFailure.Failed, "detail text")]
    public async Task RunAsync_ConversionFailed_ExplainsReason(DatabaseConversionFailure failure, string expected)
    {
        Next(DatabaseProtectionProblem.ConversionFailed, conversion: failure);
        var viewModel = Create();

        await viewModel.RunCommand.ExecuteAsync(null);

        viewModel.Message.Should().Contain(expected);
    }

    /// <summary>
    /// Verifies an unreadable account is named.
    /// </summary>
    [Fact]
    public async Task RunAsync_AccountUnreadable_NamesTheAccount()
    {
        Next(DatabaseProtectionProblem.AccountUnreadable, Guid.NewGuid());
        var viewModel = Create();

        await viewModel.RunCommand.ExecuteAsync(null);

        viewModel.Message.Should().Contain("me@example.com");
    }

    /// <summary>
    /// Verifies a running conversion shows its number and no actions.
    /// </summary>
    [Fact]
    public async Task RunAsync_Progress_ShowsConvertingMessage()
    {
        var viewModel = Create();
        string? message = null;
        var busy = false;
        var retry = true;
        _protection.OnPrepare = progress =>
        {
            progress!.Report(new DatabaseConversionProgress(1, 3));
            message = viewModel.Message;
            busy = viewModel.IsBusy;
            retry = viewModel.CanRetry;
        };

        await viewModel.RunCommand.ExecuteAsync(null);

        message.Should().Contain("1 of 3");
        busy.Should().BeTrue();
        retry.Should().BeFalse();
    }

    /// <summary>
    /// Verifies an unexpected failure is shown and can be retried.
    /// </summary>
    [Fact]
    public async Task RunAsync_PrepareThrows_ShowsDatabaseErrorWithRetry()
    {
        _protection.OnPrepare = _ => throw new IOException("disk gone");
        var viewModel = Create();

        await viewModel.RunCommand.ExecuteAsync(null);

        viewModel.Message.Should().Contain("disk gone");
        viewModel.CanRetry.Should().BeTrue();
        viewModel.CanReset.Should().BeFalse();
        viewModel.IsBusy.Should().BeFalse();
        _completed.Should().Be(0);
    }

    /// <summary>
    /// Verifies continuing without encryption completes the startup.
    /// </summary>
    [Fact]
    public async Task ContinueAsync_AfterFailure_ContinuesAndCompletes()
    {
        Next(DatabaseProtectionProblem.ConversionFailed, conversion: DatabaseConversionFailure.InUse);
        var viewModel = Create();
        await viewModel.RunCommand.ExecuteAsync(null);

        await viewModel.ContinueCommand.ExecuteAsync(null);

        _protection.ContinueCalls.Should().Be(1);
        _completed.Should().Be(1);
    }

    /// <summary>
    /// Verifies a declined confirmation resets nothing.
    /// </summary>
    [Fact]
    public async Task ResetAsync_Declined_DoesNotReset()
    {
        Next(DatabaseProtectionProblem.KeyLost);
        var viewModel = Create();
        await viewModel.RunCommand.ExecuteAsync(null);

        await viewModel.ResetCommand.ExecuteAsync(null);

        _dialogs.Confirmations.Should().ContainSingle();
        _protection.ResetAllCalls.Should().Be(0);
        _protection.PrepareCalls.Should().Be(1);
        viewModel.CanReset.Should().BeTrue();
    }

    /// <summary>
    /// Verifies resetting an unreadable account touches only that account and prepares again.
    /// </summary>
    [Fact]
    public async Task ResetAsync_AccountUnreadable_ResetsThatAccountAndRunsAgain()
    {
        var account = Guid.NewGuid();
        Next(DatabaseProtectionProblem.AccountUnreadable, account);
        _dialogs.ConfirmResult = true;
        var viewModel = Create();
        await viewModel.RunCommand.ExecuteAsync(null);

        await viewModel.ResetCommand.ExecuteAsync(null);

        _protection.ResetAccounts.Should().Equal(account);
        _protection.ResetAllCalls.Should().Be(0);
        _completed.Should().Be(1);
    }

    /// <summary>
    /// Verifies a lost key resets everything.
    /// </summary>
    [Fact]
    public async Task ResetAsync_KeyLost_ResetsAll()
    {
        Next(DatabaseProtectionProblem.KeyLost);
        _dialogs.ConfirmResult = true;
        var viewModel = Create();
        await viewModel.RunCommand.ExecuteAsync(null);

        await viewModel.ResetCommand.ExecuteAsync(null);

        _protection.ResetAllCalls.Should().Be(1);
        _protection.ResetAccounts.Should().BeEmpty();
        _completed.Should().Be(1);
    }

    /// <summary>
    /// Verifies quitting is passed on.
    /// </summary>
    [Fact]
    public void Quit_Executed_RaisesQuitRequested()
    {
        var viewModel = Create();
        var requested = false;
        viewModel.QuitRequested += (_, _) => requested = true;

        viewModel.QuitCommand.Execute(null);

        requested.Should().BeTrue();
    }
}
