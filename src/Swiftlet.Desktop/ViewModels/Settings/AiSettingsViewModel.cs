using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Swiftlet.Application.Ai;
using Swiftlet.Desktop.Formatting;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;

namespace Swiftlet.Desktop.ViewModels.Settings;

/// <summary>
/// The view model of the AI page of the settings window. Unlike the other pages it saves only on request, because
/// sending mail content to a new destination needs the user's agreement.
/// </summary>
public sealed partial class AiSettingsViewModel : ObservableObject
{
    /// <summary>
    /// The settings store, read for the saved values.
    /// </summary>
    private readonly IAiSettings _settings;

    /// <summary>
    /// The request dispatcher.
    /// </summary>
    private readonly IRequestDispatcher _dispatcher;

    /// <summary>
    /// The dialog service.
    /// </summary>
    private readonly IDialogService _dialogs;

    /// <summary>
    /// The localizer.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiSettingsViewModel"/> class with the saved settings.
    /// </summary>
    /// <param name="settings">The settings store.</param>
    /// <param name="dispatcher">The request dispatcher.</param>
    /// <param name="dialogs">The dialog service.</param>
    /// <param name="localizer">The localizer.</param>
    public AiSettingsViewModel(
        IAiSettings settings,
        IRequestDispatcher dispatcher,
        IDialogService dialogs,
        ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _localizer = localizer;
        ProviderOptions =
        [
            new ChoiceOptionViewModel(nameof(AiProvider.Claude), () => localizer["Ai_ProviderClaude"]),
            new ChoiceOptionViewModel(nameof(AiProvider.OpenAiCompatible), () => localizer["Ai_ProviderOpenAi"]),
        ];

        var saved = settings.Ai;
        IsEnabled = saved.IsEnabled;

        // The provider is set first: selecting it fills in its suggested address and model.
        SelectedProvider = ProviderOptions.First(o => o.Code == saved.Provider.ToString());
        Endpoint = saved.Endpoint;
        Model = saved.Model;
        MaxInputCharacters = saved.MaxInputCharacters;
        KeyCheck = RefreshStoredKeyAsync();
    }

    /// <summary>
    /// Gets the providers.
    /// </summary>
    public IReadOnlyList<ChoiceOptionViewModel> ProviderOptions { get; }

    /// <summary>
    /// Gets or sets a value indicating whether the AI features are switched on.
    /// </summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    /// <summary>
    /// Gets or sets the selected provider.
    /// </summary>
    [ObservableProperty]
    public partial ChoiceOptionViewModel SelectedProvider { get; set; }

    /// <summary>
    /// Gets or sets the service address.
    /// </summary>
    [ObservableProperty]
    public partial string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the model identifier.
    /// </summary>
    [ObservableProperty]
    public partial string? Model { get; set; }

    /// <summary>
    /// Gets or sets the input limit; <see langword="null"/> while the box is empty.
    /// </summary>
    [ObservableProperty]
    public partial decimal? MaxInputCharacters { get; set; }

    /// <summary>
    /// Gets or sets the newly typed API key.
    /// </summary>
    [ObservableProperty]
    public partial string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether a key is stored for the shown destination.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyPlaceholder))]
    [NotifyCanExecuteChangedFor(nameof(RemoveKeyCommand))]
    public partial bool HasStoredKey { get; set; }

    /// <summary>
    /// Gets or sets the outcome of the last action, or <see langword="null"/>.
    /// </summary>
    [ObservableProperty]
    public partial string? StatusText { get; set; }

    /// <summary>
    /// Gets or sets the problem of the last action, or <see langword="null"/>.
    /// </summary>
    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    /// <summary>
    /// Gets the suggested models of the selected provider.
    /// </summary>
    public IReadOnlyList<string> ModelSuggestions => Provider == AiProvider.Claude ? AiModels.Claude : [];

    /// <summary>
    /// Gets the text shown in the empty key box.
    /// </summary>
    public string KeyPlaceholder
    {
        get
        {
            if (HasStoredKey)
            {
                return _localizer["Ai_KeySaved"];
            }

            return Provider == AiProvider.Claude ? _localizer["Ai_KeyEmpty"] : _localizer["Ai_KeyOptional"];
        }
    }

    /// <summary>
    /// Gets the latest check for a stored key, so tests can wait for it.
    /// </summary>
    internal Task KeyCheck { get; private set; }

    /// <summary>
    /// Gets the selected provider.
    /// </summary>
    private AiProvider Provider => Enum.Parse<AiProvider>(SelectedProvider.Code);

    /// <summary>
    /// Cancels a running connection test; call it when the window has closed.
    /// </summary>
    public void Detach()
    {
        TestCommand.Cancel();
    }

    /// <summary>
    /// Re-reads the texts built by this view model after the language changed.
    /// </summary>
    public void RefreshLanguage()
    {
        foreach (var option in ProviderOptions)
        {
            option.RefreshLanguage();
        }

        OnPropertyChanged(nameof(KeyPlaceholder));
    }

    /// <summary>
    /// Shows the address and model of the newly selected provider: the saved ones if it is the saved provider,
    /// otherwise the suggested ones. A typed key belongs to the previous provider and is dropped.
    /// </summary>
    /// <param name="value">The new selection.</param>
    partial void OnSelectedProviderChanged(ChoiceOptionViewModel value)
    {
        if (value is null)
        {
            return;
        }

        var saved = _settings.Ai;
        var isSaved = saved.Provider == Provider;
        OnPropertyChanged(nameof(ModelSuggestions));
        OnPropertyChanged(nameof(KeyPlaceholder));
        ApiKey = string.Empty;
        Endpoint = isSaved ? saved.Endpoint : AiEndpoint.DefaultFor(Provider);
        Model = isSaved ? saved.Model : Provider == AiProvider.Claude ? AiModels.DefaultClaudeModel : string.Empty;
        KeyCheck = RefreshStoredKeyAsync();
    }

    /// <summary>
    /// Checks for a stored key of the newly typed destination.
    /// </summary>
    /// <param name="value">The new address.</param>
    partial void OnEndpointChanged(string value)
    {
        KeyCheck = RefreshStoredKeyAsync();
    }

    /// <summary>
    /// Validates the values, asks before content could reach a new destination, and saves.
    /// </summary>
    /// <returns>A task that completes when the settings are saved or the problem is shown.</returns>
    [RelayCommand]
    private async Task SaveAsync()
    {
        ClearOutcome();
        var provider = Provider;
        var address = Endpoint;
        var model = Model?.Trim() ?? string.Empty;
        var key = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim();
        var enabled = IsEnabled;

        try
        {
            if (enabled)
            {
                if (await ValidateAsync(provider, address, model, key) is not { } endpoint)
                {
                    return;
                }

                if (_settings.Ai.ConfirmedDestination != AiEndpoint.Destination(provider, endpoint)
                    && !await _dialogs.ConfirmAsync(
                        _localizer["Ai_ConsentTitle"],
                        _localizer.Format("Ai_ConsentMessage", endpoint.Authority),
                        _localizer["Ai_ConsentAllow"]))
                {
                    return;
                }
            }

            var limit = MaxInputCharacters is { } typed
                ? (int)Math.Clamp(typed, AiConfiguration.MinInputCharacters, AiConfiguration.MaxAllowedInputCharacters)
                : AiConfiguration.DefaultMaxInputCharacters;
            await _dispatcher.SendAsync(new SaveAiSettingsCommand(enabled, provider, address, model, limit, key));
            MaxInputCharacters = limit;
            ApiKey = string.Empty;
            await RefreshStoredKeyAsync();
            StatusText = _localizer["Ai_Saved"];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is the UI boundary: show the failure in the window instead of crashing.
            ErrorText = _localizer.Format("Error_Format", _localizer["Settings_SaveFailed"], ex.Message);
        }
    }

    /// <summary>
    /// Asks the service for a tiny answer, using the values shown, without saving them.
    /// </summary>
    /// <param name="cancellationToken">Cancelled when the window closes.</param>
    /// <returns>A task that completes when the outcome is shown.</returns>
    [RelayCommand]
    private async Task TestAsync(CancellationToken cancellationToken)
    {
        ClearOutcome();
        var provider = Provider;
        var address = Endpoint;
        var model = Model?.Trim() ?? string.Empty;
        var key = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim();

        try
        {
            if (await ValidateAsync(provider, address, model, key) is null)
            {
                return;
            }

            StatusText = _localizer["Ai_Testing"];
            await _dispatcher.SendAsync(new TestAiConnectionCommand(provider, address, model, key), cancellationToken);
            StatusText = _localizer["Ai_TestSucceeded"];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The window closed; there is nobody to tell.
            StatusText = null;
        }
        catch (AiException ex)
        {
            StatusText = null;
            ErrorText = AiFailureText.Describe(_localizer, ex.Failure);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is the UI boundary: reading the stored key can fail too.
            StatusText = null;
            ErrorText = _localizer.Format("Error_Format", _localizer["Ai_ErrorUnknown"], ex.Message);
        }
    }

    /// <summary>
    /// Removes the stored key of the shown provider.
    /// </summary>
    /// <returns>A task that completes when the key is removed or the problem is shown.</returns>
    [RelayCommand(CanExecute = nameof(HasStoredKey))]
    private async Task RemoveKeyAsync()
    {
        ClearOutcome();
        try
        {
            await _dispatcher.SendAsync(new RemoveAiKeyCommand(Provider));
            await RefreshStoredKeyAsync();
            StatusText = _localizer["Ai_KeyRemoved"];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is the UI boundary: show the failure in the window instead of crashing.
            ErrorText = _localizer.Format("Error_Format", _localizer["Settings_SaveFailed"], ex.Message);
        }
    }

    /// <summary>
    /// Checks that a request can be made with the values and shows what is missing.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="address">The typed address.</param>
    /// <param name="model">The model.</param>
    /// <param name="key">The typed key, if any.</param>
    /// <returns>The address, or <see langword="null"/> when something is missing.</returns>
    private async Task<Uri?> ValidateAsync(AiProvider provider, string address, string model, string? key)
    {
        if (!AiEndpoint.TryParse(address, out var endpoint))
        {
            ErrorText = _localizer["Ai_InvalidEndpoint"];
            return null;
        }

        if (model.Length == 0)
        {
            ErrorText = _localizer["Ai_ModelRequired"];
            return null;
        }

        if (provider == AiProvider.Claude
            && key is null
            && !await _dispatcher.SendAsync(new HasAiKeyQuery(provider, address)))
        {
            ErrorText = _localizer["Ai_KeyRequired"];
            return null;
        }

        return endpoint;
    }

    /// <summary>
    /// Looks up whether a key is stored for the shown destination. An answer that arrives after the destination changed
    /// is dropped.
    /// </summary>
    /// <returns>A task that completes when the answer is applied.</returns>
    private async Task RefreshStoredKeyAsync()
    {
        var provider = Provider;
        var address = Endpoint;
        bool stored;
        try
        {
            stored = AiEndpoint.TryParse(address, out _)
                && await _dispatcher.SendAsync(new HasAiKeyQuery(provider, address));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is the UI boundary: an unreadable secret store is shown, and the key can be typed again.
            ErrorText = _localizer.Format("Error_Format", _localizer["Ai_ErrorUnknown"], ex.Message);
            stored = false;
        }

        if (provider == Provider && address == Endpoint)
        {
            HasStoredKey = stored;
        }
    }

    /// <summary>
    /// Clears the messages of the previous action.
    /// </summary>
    private void ClearOutcome()
    {
        StatusText = null;
        ErrorText = null;
    }
}
