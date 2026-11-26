namespace Swiftlet.Application.Abstractions;

/// <summary>
/// The saved rules that allow loading remote images in mail from a sender or a sender's domain (see
/// <see cref="RemoteImageRule"/>). The rules are kept in memory for instant lookups and persisted in the global store.
/// </summary>
public interface IRemoteImageRules
{
    /// <summary>
    /// Loads the saved rules; must complete before the rules are used.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the rules are loaded.</returns>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether remote images may be loaded for a message from a sender.
    /// </summary>
    /// <param name="fromAddress">The sender address.</param>
    /// <returns><see langword="true"/> if a saved rule matches the sender's address or domain.</returns>
    bool IsAllowed(string? fromAddress);

    /// <summary>
    /// Saves a rule.
    /// </summary>
    /// <param name="rule">The rule, already normalized by <see cref="RemoteImageRule.TryNormalize"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> if the rule was added; <see langword="false"/> if it already existed.</returns>
    Task<bool> AddAsync(string rule, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a rule.
    /// </summary>
    /// <param name="rule">The rule to delete.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> if the rule existed and was deleted.</returns>
    Task<bool> RemoveAsync(string rule, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists saved rules containing a text, in alphabetical order.
    /// </summary>
    /// <param name="filter">The text to search for, or <see langword="null"/> for all rules.</param>
    /// <param name="take">The maximum number of rules to return.</param>
    /// <returns>The matching rules, and the total number of matches.</returns>
    (IReadOnlyList<string> Rules, int TotalMatches) Search(string? filter, int take);
}
