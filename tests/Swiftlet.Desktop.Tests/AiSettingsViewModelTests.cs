using AwesomeAssertions;
using Mediator;
using Swiftlet.Application.Ai;
using Swiftlet.Desktop.Settings;
using Swiftlet.Desktop.ViewModels.Settings;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests <see cref="AiSettingsViewModel"/> and the storage of the AI settings.
/// </summary>
public sealed class AiSettingsViewModelTests
{
    /// <summary>
    /// The saved settings.
    /// </summary>
    private readonly DisabledAiSettings _settings = new();

    /// <summary>
    /// The dispatcher.
    /// </summary>
    private readonly FakeDispatcher _dispatcher = new();

    /// <summary>
    /// The dialogs.
    /// </summary>
    private readonly FakeDialogService _dialogs = new();

    /// <summary>
    /// Whether the fake key store holds a key.
    /// </summary>
    private bool _hasKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiSettingsViewModelTests"/> class.
    /// </summary>
    public AiSettingsViewModelTests()
    {
        _dispatcher.On<HasAiKeyQuery>(_ => _hasKey);
        _dispatcher.On<SaveAiSettingsCommand>(_ => Unit.Value);
        _dispatcher.On<TestAiConnectionCommand>(_ => Unit.Value);
        _dispatcher.On<RemoveAiKeyCommand>(_ =>
        {
            _hasKey = false;
            return Unit.Value;
        });
    }

    /// <summary>
    /// A new installation shows Claude, switched off.
    /// </summary>
    [Fact]
    public void Constructor_DefaultSettings_ShowsClaudeSwitchedOff()
    {
        var viewModel = Create();

        viewModel.IsEnabled.Should().BeFalse();
        viewModel.Endpoint.Should().Be("https://api.anthropic.com");
        viewModel.Model.Should().Be("claude-opus-5-5");
        viewModel.ModelSuggestions.Should().Equal(AiModels.Claude);
    }

    /// <summary>
    /// Selecting another provider shows its suggestions and drops the typed key; selecting the saved one again restores
    /// the saved values.
    /// </summary>
    [Fact]
    public void SelectedProvider_Changed_FillsInAddressAndModel()
    {
        var viewModel = Create();
        viewModel.ApiKey = "key";

        viewModel.SelectedProvider = viewModel.ProviderOptions[1];

        viewModel.Endpoint.Should().Be("http://localhost:11434/v1");
        viewModel.Model.Should().BeEmpty();
        viewModel.ModelSuggestions.Should().BeEmpty();
        viewModel.ApiKey.Should().BeEmpty();

        viewModel.SelectedProvider = viewModel.ProviderOptions[0];
        viewModel.Model.Should().Be("claude-opus-5-5");
    }

    /// <summary>
    /// Switching on for a new destination asks first and saves nothing when declined.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task SaveCommand_NewDestinationDeclined_SavesNothing()
    {
        var viewModel = Create();
        viewModel.IsEnabled = true;
        viewModel.ApiKey = "key";

        await viewModel.SaveCommand.ExecuteAsync(null);

        _dialogs.Confirmations.Should().ContainSingle().Which.Message.Should().Contain("api.anthropic.com");
        _dispatcher.Sent<SaveAiSettingsCommand>().Should().BeEmpty();
        viewModel.StatusText.Should().BeNull();
    }

    /// <summary>
    /// After agreeing, the values are saved and the typed key is cleared.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task SaveCommand_NewDestinationAllowed_Saves()
    {
        _dialogs.ConfirmResult = true;
        var viewModel = Create();
        viewModel.IsEnabled = true;
        viewModel.ApiKey = " key ";
        viewModel.MaxInputCharacters = 20;

        await viewModel.SaveCommand.ExecuteAsync(null);

        _dispatcher.Sent<SaveAiSettingsCommand>().Should().ContainSingle().Which.Should().Match<SaveAiSettingsCommand>(c =>
            c.IsEnabled && c.ApiKey == "key" && c.Model == "claude-opus-5-5"
            && c.MaxInputCharacters == AiConfiguration.MinInputCharacters);
        viewModel.ApiKey.Should().BeEmpty();
        viewModel.StatusText.Should().Be("Saved.");
    }

    /// <summary>
    /// A confirmed destination is not asked about again.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task SaveCommand_ConfirmedDestination_DoesNotAsk()
    {
        _settings.SetAi(AiConfiguration.Default with
        {
            IsEnabled = true,
            ConfirmedDestination = "Claude|https://api.anthropic.com",
        });
        _hasKey = true;
        var viewModel = Create();

        await viewModel.SaveCommand.ExecuteAsync(null);

        _dialogs.Confirmations.Should().BeEmpty();
        _dispatcher.Sent<SaveAiSettingsCommand>().Should().ContainSingle().Which.ApiKey.Should().BeNull();
    }

    /// <summary>
    /// Switching off needs neither a valid address nor agreement.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task SaveCommand_Disabled_SavesWithoutAsking()
    {
        var viewModel = Create();
        viewModel.Endpoint = "nonsense";

        await viewModel.SaveCommand.ExecuteAsync(null);

        _dialogs.Confirmations.Should().BeEmpty();
        _dispatcher.Sent<SaveAiSettingsCommand>().Should().ContainSingle().Which.IsEnabled.Should().BeFalse();
    }

    /// <summary>
    /// Missing values are shown and nothing is saved.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <param name="model">The model.</param>
    /// <param name="expected">The expected message.</param>
    /// <returns>A task that completes when the test finished.</returns>
    [Theory]
    [InlineData("http://example.com", "m", "Enter an https address, or an http address on this computer.")]
    [InlineData("https://api.anthropic.com", " ", "Enter a model.")]
    [InlineData("https://api.anthropic.com", "m", "Enter the API key for this address.")]
    public async Task SaveCommand_MissingValue_ShowsIt(string address, string model, string expected)
    {
        _dialogs.ConfirmResult = true;
        var viewModel = Create();
        viewModel.IsEnabled = true;
        viewModel.Endpoint = address;
        viewModel.Model = model;

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ErrorText.Should().Be(expected);
        _dispatcher.Sent<SaveAiSettingsCommand>().Should().BeEmpty();
    }

    /// <summary>
    /// The test button sends the shown values without saving them.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task TestCommand_ValidValues_ReportsSuccess()
    {
        var viewModel = Create();
        viewModel.ApiKey = "key";

        await viewModel.TestCommand.ExecuteAsync(null);

        _dispatcher.Sent<TestAiConnectionCommand>().Should().ContainSingle().Which.ApiKey.Should().Be("key");
        _dispatcher.Sent<SaveAiSettingsCommand>().Should().BeEmpty();
        viewModel.StatusText.Should().Be("The service answered.");
    }

    /// <summary>
    /// A failed test shows the reason.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task TestCommand_Refused_ShowsTheReason()
    {
        _dispatcher.On<TestAiConnectionCommand>(_ => throw new AiException(AiFailure.Unauthorized, 401));
        var viewModel = Create();
        viewModel.ApiKey = "key";

        await viewModel.TestCommand.ExecuteAsync(null);

        viewModel.ErrorText.Should().Be("The service rejected the API key.");
        viewModel.StatusText.Should().BeNull();
    }

    /// <summary>
    /// Closing the window cancels a running test without showing an error.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task Detach_TestRunning_CancelsIt()
    {
        _dispatcher.OnAsync<TestAiConnectionCommand>(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Unit.Value;
        });
        var viewModel = Create();
        viewModel.ApiKey = "key";
        var running = viewModel.TestCommand.ExecuteAsync(null);

        viewModel.Detach();
        await running;

        viewModel.ErrorText.Should().BeNull();
        viewModel.StatusText.Should().BeNull();
    }

    /// <summary>
    /// The remove button is offered only for a stored key and removes it.
    /// </summary>
    /// <returns>A task that completes when the test finished.</returns>
    [Fact]
    public async Task RemoveKeyCommand_StoredKey_RemovesIt()
    {
        _hasKey = true;
        var viewModel = Create();
        await viewModel.KeyCheck;
        viewModel.RemoveKeyCommand.CanExecute(null).Should().BeTrue();

        await viewModel.RemoveKeyCommand.ExecuteAsync(null);

        viewModel.HasStoredKey.Should().BeFalse();
        viewModel.RemoveKeyCommand.CanExecute(null).Should().BeFalse();
        viewModel.KeyPlaceholder.Should().Be("Enter the API key");
    }

    /// <summary>
    /// Saved AI settings are read back, and the file holds the provider by name.
    /// </summary>
    [Fact]
    public void AppSettings_SetAi_IsReadBack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"swiftlet-ai-{Guid.NewGuid():N}.json");
        try
        {
            var value = new AiConfiguration(
                true, AiProvider.OpenAiCompatible, "http://localhost:11434/v1", "llama", 8_000, "x");
            var store = new AppSettings(path);
            var changes = 0;
            store.AiChanged += (_, _) => changes++;

            store.SetAi(value);
            store.SetCloseToTray(false);

            new AppSettings(path).Ai.Should().Be(value);
            File.ReadAllText(path).Should().Contain("\"OpenAiCompatible\"");
            changes.Should().Be(1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Missing or unusable stored values leave AI switched off without losing the other settings.
    /// </summary>
    /// <param name="stored">The stored member.</param>
    [Theory]
    [InlineData("")]
    [InlineData(",\"ai\":\"text\"")]
    [InlineData(",\"ai\":{\"isEnabled\":true,\"provider\":\"Other\"}")]
    [InlineData(",\"ai\":{\"isEnabled\":true,\"provider\":7,\"endpoint\":\"https://a\",\"model\":\"m\"}")]
    [InlineData(",\"ai\":{\"isEnabled\":true}")]
    public void AppSettings_UnusableAi_UsesTheDefault(string stored)
    {
        var path = Path.Combine(Path.GetTempPath(), $"swiftlet-ai-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"closeToTray\":false" + stored + "}");

            var store = new AppSettings(path);

            store.Ai.Should().Be(AiConfiguration.Default);
            store.CloseToTray.Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Creates the view model in English.
    /// </summary>
    /// <returns>The view model.</returns>
    private AiSettingsViewModel Create()
    {
        return new AiSettingsViewModel(_settings, _dispatcher, _dialogs, TestLocalizer.English());
    }
}
