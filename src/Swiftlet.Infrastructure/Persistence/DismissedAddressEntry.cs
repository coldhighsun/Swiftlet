namespace Swiftlet.Infrastructure.Persistence;

/// <summary>
/// A row naming an address the user removed from the collected addresses.
/// </summary>
internal sealed class DismissedAddressEntry
{
    /// <summary>
    /// Gets or sets the address, in lower case.
    /// </summary>
    public string Address { get; set; } = string.Empty;
}
