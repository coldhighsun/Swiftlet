using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Application.Contacts;
using Swiftlet.Desktop.Settings;
using Swiftlet.Domain.Contacts;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// The addresses of the address book and their names, held in memory so that views can look them up while binding.
/// </summary>
public interface IContactDirectory
{
    /// <summary>
    /// Raised when the names read differently although the address book was not loaded again: the user chose another name
    /// order.
    /// </summary>
    event EventHandler? NamesChanged;

    /// <summary>
    /// Reads the address book again; call it after contacts may have changed. A failure is logged and keeps what was
    /// loaded before.
    /// </summary>
    /// <returns>A task that completes when the directory is up to date or the failure is logged.</returns>
    Task ReloadAsync();

    /// <summary>
    /// Reads the address book unless that already succeeded once.
    /// </summary>
    /// <returns>A task that completes when the directory is loaded or the failure is logged.</returns>
    Task EnsureLoadedAsync();

    /// <summary>
    /// Gets a value indicating whether an address belongs to a contact.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns><see langword="true"/> if a contact has the address.</returns>
    bool Contains(string address);

    /// <summary>
    /// Gets the name the address book has for an address.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns>
    /// The name in the order the user chose, or <see langword="null"/> when no contact has the address or that contact has
    /// no name.
    /// </returns>
    string? NameOf(string address);
}

/// <summary>
/// Loads the directory through <see cref="GetContactNamesQuery"/>.
/// </summary>
public sealed class ContactDirectory : IContactDirectory
{
    /// <summary>
    /// Runs the query.
    /// </summary>
    private readonly IRequestDispatcher _dispatcher;

    /// <summary>
    /// The settings saying the name order, if any.
    /// </summary>
    private readonly IAppSettings? _settings;

    /// <summary>
    /// The logger.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// The names by address; replaced as a whole, never changed.
    /// </summary>
    private volatile IReadOnlyDictionary<string, ContactName> _names = new Dictionary<string, ContactName>();

    /// <summary>
    /// Whether a load has succeeded.
    /// </summary>
    private volatile bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContactDirectory"/> class. It lives as long as the settings do.
    /// </summary>
    /// <param name="dispatcher">Runs the query.</param>
    /// <param name="logger">The logger; optional.</param>
    /// <param name="settings">The settings saying the name order; optional.</param>
    public ContactDirectory(
        IRequestDispatcher dispatcher,
        ILogger<ContactDirectory>? logger = null,
        IAppSettings? settings = null)
    {
        _dispatcher = dispatcher;
        _logger = logger ?? NullLogger<ContactDirectory>.Instance;
        _settings = settings;
        if (settings is not null)
        {
            settings.ContactNameOrderChanged += (_, _) => NamesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public event EventHandler? NamesChanged;

    /// <inheritdoc />
    public async Task ReloadAsync()
    {
        try
        {
            // Database work completes synchronously, so it is moved off the UI thread.
            _names = await Task.Run(() => _dispatcher.SendAsync(new GetContactNamesQuery()));
            _loaded = true;
        }
#pragma warning disable CA1031 // Names are decoration: without them mail is still shown with what its headers say.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "The contact names could not be loaded");
        }
    }

    /// <inheritdoc />
    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : ReloadAsync();

    /// <inheritdoc />
    public bool Contains(string address) => _names.ContainsKey(address);

    /// <inheritdoc />
    public string? NameOf(string address)
    {
        if (!_names.TryGetValue(address, out var parts))
        {
            return null;
        }

        var order = _settings is null ? NameOrder.Auto : ContactNameOrder.ToNameOrder(_settings.ContactNameOrder);
        var name = PersonName.Compose(parts.FirstName, parts.LastName, order);
        return name.Length > 0 ? name : null;
    }
}
