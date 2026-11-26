using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Infrastructure.Persistence.Encryption;

namespace Swiftlet.Desktop.ViewModels;

/// <summary>
/// The view model of the startup window, shown while the databases are encrypted or when they cannot be used.
/// </summary>
/// <param name="protection">Prepares the databases.</param>
/// <param name="localizer">The localizer.</param>
/// <param name="dialogs">Asks for confirmation.</param>
/// <param name="uiThread">Brings progress to the UI thread.</param>
/// <param name="accountName">Gets the name of an account, if it can be read.</param>
/// <param name="logger">The logger; optional.</param>
public sealed partial class StartupViewModel(
    IDatabaseProtection protection,
    ILocalizer localizer,
    IDialogService dialogs,
    IUiThread uiThread,
    Func<Guid, Task<string?>> accountName,
    ILogger<StartupViewModel>? logger = null) : ObservableObject
{
    /// <summary>
    /// Forwards conversion progress to the view model.
    /// </summary>
    /// <param name="report">Handles one report.</param>
    private sealed class Progress(Action<DatabaseConversionProgress> report) : IProgress<DatabaseConversionProgress>
    {
        /// <inheritdoc />
        public void Report(DatabaseConversionProgress value)
        {
            report(value);
        }
    }

    /// <summary>
    /// The result being shown.
    /// </summary>
    private DatabaseProtectionResult? _result;

    /// <summary>
    /// Raised when the databases are ready and the application can start.
    /// </summary>
    public event EventHandler? Completed;

    /// <summary>
    /// Raised when the user chose to quit.
    /// </summary>
    public event EventHandler? QuitRequested;

    /// <summary>
    /// Gets or sets the explanation shown to the user.
    /// </summary>
    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether work is running.
    /// </summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether trying again is offered.
    /// </summary>
    [ObservableProperty]
    public partial bool CanRetry { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether going on without encryption is offered.
    /// </summary>
    [ObservableProperty]
    public partial bool CanContinueUnencrypted { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a reset is offered.
    /// </summary>
    [ObservableProperty]
    public partial bool CanReset { get; set; }

    /// <summary>
    /// Prepares the databases and shows what stands in the way.
    /// </summary>
    /// <returns>A task that completes when the preparation has ended.</returns>
    [RelayCommand]
    private async Task RunAsync()
    {
        Begin();
        DatabaseProtectionResult result;
        try
        {
            var progress = new Progress(ShowProgress);

            // SQLite works synchronously, so the preparation runs elsewhere to keep the window responsive.
            result = await Task.Run(() => protection.PrepareAsync(progress));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is the UI boundary: the failure is shown with a retry instead of ending the application.
            logger?.LogError(ex, "Preparing the databases failed");
            result = new DatabaseProtectionResult(DatabaseProtectionProblem.DatabaseError, Detail: ex.Message);
        }

        if (result.Problem == DatabaseProtectionProblem.None)
        {
            Completed?.Invoke(this, EventArgs.Empty);
            return;
        }

        await ShowAsync(result);
    }

    /// <summary>
    /// Starts the application with the data that could not be encrypted left as it is.
    /// </summary>
    /// <returns>A completed task.</returns>
    [RelayCommand]
    private Task ContinueAsync()
    {
        protection.ContinueUnencrypted();
        CanRetry = CanContinueUnencrypted = CanReset = false;
        Completed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    /// <summary>
    /// After a confirmation, puts the unreadable data aside and prepares again.
    /// </summary>
    /// <returns>A task that completes when the reset has been handled.</returns>
    [RelayCommand]
    private async Task ResetAsync()
    {
        if (_result is not { } shown
            || !await dialogs.ConfirmAsync(
                localizer["Startup_ResetConfirmTitle"], localizer["Startup_ResetConfirm"], localizer["Startup_ResetConfirmTitle"]))
        {
            return;
        }

        Begin();
        try
        {
            await Task.Run(() => shown is { Problem: DatabaseProtectionProblem.AccountUnreadable, AccountId: { } account }
                ? protection.ResetAccountAsync(account)
                : protection.ResetAllAsync());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is the UI boundary: the failure is shown with a retry instead of ending the application.
            logger?.LogError(ex, "Resetting the local data failed");
            await ShowAsync(new DatabaseProtectionResult(DatabaseProtectionProblem.DatabaseError, Detail: ex.Message));
            return;
        }

        await RunAsync();
    }

    /// <summary>
    /// Asks the application to end.
    /// </summary>
    [RelayCommand]
    private void Quit()
    {
        QuitRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Switches to the working state.
    /// </summary>
    private void Begin()
    {
        IsBusy = true;
        CanRetry = CanContinueUnencrypted = CanReset = false;
        Message = localizer["Startup_Title"];
    }

    /// <summary>
    /// Shows which database is being converted.
    /// </summary>
    /// <param name="progress">The report, possibly from another thread.</param>
    private void ShowProgress(DatabaseConversionProgress progress)
    {
        _ = uiThread.InvokeAsync(() =>
        {
            // A report that arrives after the work ended must not replace the explanation of its outcome.
            if (IsBusy)
            {
                Message = localizer.Format("Startup_Converting", progress.Current, progress.Total);
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Shows a problem and the actions that fit it.
    /// </summary>
    /// <param name="result">The result.</param>
    /// <returns>A task that completes when the problem is shown.</returns>
    private async Task ShowAsync(DatabaseProtectionResult result)
    {
        var message = result.Problem switch
        {
            DatabaseProtectionProblem.ConversionFailed => result.Conversion switch
            {
                DatabaseConversionFailure.NotEnoughSpace => localizer["Startup_NotEnoughSpace"],
                DatabaseConversionFailure.InUse => localizer["Startup_InUse"],
                _ => localizer.Format("Startup_ConversionFailed", result.Detail),
            },
            DatabaseProtectionProblem.KeyStoreUnavailable => localizer["Startup_KeyStoreUnavailable"],
            DatabaseProtectionProblem.KeyStoreMissing => localizer["Startup_KeyStoreMissing"],
            DatabaseProtectionProblem.KeyLost => localizer["Startup_KeyLost"],
            DatabaseProtectionProblem.AccountUnreadable => localizer.Format("Startup_AccountUnreadable", await NameOfAsync(result.AccountId)),
            DatabaseProtectionProblem.Busy => localizer["Startup_Busy"],
            _ => localizer.Format("Startup_DatabaseError", result.Detail),
        };

        _result = result;
        Message = message;
        IsBusy = false;

        // Which secret store exists is decided when the process starts, so only a new start can find a missing keychain.
        CanRetry = result.Problem != DatabaseProtectionProblem.KeyStoreMissing;
        CanContinueUnencrypted = result.Problem == DatabaseProtectionProblem.ConversionFailed;
        CanReset = result.Problem is DatabaseProtectionProblem.KeyLost or DatabaseProtectionProblem.AccountUnreadable;
    }

    /// <summary>
    /// Gets what to call an account.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <returns>Its name, or its identifier when the name cannot be read.</returns>
    private async Task<string> NameOfAsync(Guid? accountId)
    {
        if (accountId is not { } id)
        {
            return string.Empty;
        }

        try
        {
            return await accountName(id) ?? id.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The name only makes the message friendlier; the problem itself must still be shown.
            logger?.LogWarning(ex, "Could not read the name of account {AccountId}", id);
            return id.ToString();
        }
    }
}
