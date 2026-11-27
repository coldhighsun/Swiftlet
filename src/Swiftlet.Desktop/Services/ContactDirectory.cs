using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Application.Contacts;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// The addresses of the address book and their names, held in memory so that views can look them up while binding.
/// </summary>
public interface IContactDirectory
{
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
    /// <returns>The name, or <see langword="null"/> when no contact has the address or that contact has no name.</returns>
    string? NameOf(string address);
}

/// <summary>
/// Loads the directory through <see cref="GetContactNamesQuery"/>.
/// </summary>
/// <param name="dispatcher">Runs the query.</param>
/// <param name="logger">The logger; optional.</param>
public sealed class ContactDirectory(IRequestDispatcher dispatcher, ILogger<ContactDirectory>? logger = null) : IContactDirectory
{
    /// <summary>
    /// The logger.
    /// </summary>
    private readonly ILogger _logger = logger ?? NullLogger<ContactDirectory>.Instance;

    /// <summary>
    /// The names by address; replaced as a whole, never changed.
    /// </summary>
    private volatile IReadOnlyDictionary<string, string> _names = new Dictionary<string, string>();

    /// <summary>
    /// Whether a load has succeeded.
    /// </summary>
    private volatile bool _loaded;

    /// <inheritdoc />
    public async Task ReloadAsync()
    {
        try
        {
            // Database work completes synchronously, so it is moved off the UI thread.
            _names = await Task.Run(() => dispatcher.SendAsync(new GetContactNamesQuery()));
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
    public string? NameOf(string address) => _names.TryGetValue(address, out var name) && name.Length > 0 ? name : null;
}
