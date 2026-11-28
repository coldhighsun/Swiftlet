using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Swiftlet.Domain.Contacts;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Infrastructure.Persistence.Repositories;

/// <summary>
/// Builds the database condition of <see cref="MessageSearchCriteria.Participants"/>.
/// </summary>
internal static class ParticipantFilter
{
    /// <summary>
    /// The characters that separate alternative entries, the Chinese comma and semicolon included.
    /// </summary>
    private static readonly char[] EntrySeparators = [',', ';', '，', '；'];

    /// <summary>
    /// Builds the condition: any entry matches, and an entry matches when each of its terms does.
    /// </summary>
    /// <param name="participants">The entered text.</param>
    /// <returns>The condition; it matches nothing when there is no term.</returns>
    public static Expression<Func<MailMessage, bool>> Create(string participants)
    {
        var entries = participants
            .Split(EntrySeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(MessageRepository.SplitTerms)
            .Where(terms => terms.Length > 0)
            .Select(terms => terms.Select(Term).Aggregate((left, right) => Combine(left, right, Expression.AndAlso)))
            .ToList();
        return entries.Count == 0
            ? m => false
            : entries.Aggregate((left, right) => Combine(left, right, Expression.OrElse));
    }

    /// <summary>
    /// Builds the condition of one term.
    /// </summary>
    /// <param name="term">The term.</param>
    /// <returns>The condition.</returns>
    private static Expression<Func<MailMessage, bool>> Term(string term)
    {
        if (Contact.IsValidEmail(term))
        {
            // The recipients are stored as "a@x.com, b@y.com"; the added separators let only whole addresses match.
            var sender = MessageRepository.EscapeLike(term);
            var recipient = $"%, {sender}, %";
            return m => EF.Functions.Like(m.FromAddress, sender, "\\")
                || EF.Functions.Like(", " + m.Recipients + ", ", recipient, "\\");
        }

        var pattern = MessageRepository.ToLikePattern(term);
        return m => EF.Functions.Like(m.FromName, pattern, "\\")
            || EF.Functions.Like(m.FromAddress, pattern, "\\")
            || EF.Functions.Like(m.Recipients, pattern, "\\");
    }

    /// <summary>
    /// Joins two conditions on the same message.
    /// </summary>
    /// <param name="left">The first condition.</param>
    /// <param name="right">The second condition.</param>
    /// <param name="join">Creates the logical operation.</param>
    /// <returns>The joined condition.</returns>
    private static Expression<Func<MailMessage, bool>> Combine(
        Expression<Func<MailMessage, bool>> left,
        Expression<Func<MailMessage, bool>> right,
        Func<Expression, Expression, BinaryExpression> join)
    {
        var body = ReplacingExpressionVisitor.Replace(right.Parameters[0], left.Parameters[0], right.Body);
        return Expression.Lambda<Func<MailMessage, bool>>(join(left.Body, body), left.Parameters);
    }
}
