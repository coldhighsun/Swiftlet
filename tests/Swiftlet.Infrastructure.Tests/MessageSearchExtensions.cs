using Swiftlet.Domain.Messages;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Shortens searches that only use the search text.
/// </summary>
internal static class MessageSearchExtensions
{
    /// <summary>
    /// Searches by text alone.
    /// </summary>
    /// <param name="messages">The repository.</param>
    /// <param name="text">The search text.</param>
    /// <param name="accountId">The account, or <see langword="null"/> for all.</param>
    /// <param name="skip">The number of results to skip.</param>
    /// <param name="take">The maximum number of results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matching messages.</returns>
    public static Task<IReadOnlyList<MailMessage>> SearchAsync(
        this IMessageRepository messages,
        string text,
        Guid? accountId,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        return messages.SearchAsync(new MessageSearchCriteria { Text = text, AccountId = accountId }, skip, take, cancellationToken);
    }
}
