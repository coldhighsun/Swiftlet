using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Swiftlet.Application.Abstractions;

/// <summary>
/// Parsing and matching of the rules that allow loading remote images in mail from a sender or a sender's domain.
/// A rule containing <c>@</c> is an address rule (exact match); any other rule is a domain rule that matches the
/// domain and its subdomains.
/// </summary>
public static partial class RemoteImageRule
{
    /// <summary>
    /// Normalizes and validates a rule typed by the user or derived from a message.
    /// </summary>
    /// <param name="input">The raw text, such as <c>News@Shop.com</c>, <c>@shop.com</c> or <c>*.shop.com</c>.</param>
    /// <param name="rule">The normalized rule when the input is valid.</param>
    /// <returns><see langword="true"/> if the input is a valid address or domain.</returns>
    public static bool TryNormalize(string? input, [NotNullWhen(true)] out string? rule)
    {
        rule = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim().ToLowerInvariant();
        if (text.StartsWith("*.", StringComparison.Ordinal))
        {
            text = text[2..];
        }
        else if (text.StartsWith('@'))
        {
            text = text[1..];
        }

        var valid = text.Contains('@', StringComparison.Ordinal) ? AddressShape().IsMatch(text) : DomainShape().IsMatch(text);
        if (!valid)
        {
            return false;
        }

        rule = text;
        return true;
    }

    /// <summary>
    /// Lists the rules that would allow a message from a sender: the address itself, then the sender's domain and
    /// its parent domains (without the top-level domain).
    /// </summary>
    /// <param name="fromAddress">The sender address, or <see langword="null"/>.</param>
    /// <returns>The candidate rules, most specific first; empty when the address is not a usable sender.</returns>
    public static IReadOnlyList<string> Candidates(string? fromAddress)
    {
        if (!TryNormalize(fromAddress, out var address) || !address.Contains('@', StringComparison.Ordinal))
        {
            return [];
        }

        var domain = address[(address.LastIndexOf('@') + 1)..];
        var candidates = new List<string> { address };
        while (domain.Contains('.', StringComparison.Ordinal))
        {
            candidates.Add(domain);
            domain = domain[(domain.IndexOf('.', StringComparison.Ordinal) + 1)..];
        }

        return candidates;
    }

    /// <summary>
    /// Matches a plausible e-mail address.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex(@"^[^@\s]+@([\p{L}\p{N}\-]+\.)+[\p{L}\p{N}\-]+$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex AddressShape();

    /// <summary>
    /// Matches a domain name with at least one dot.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex(@"^([\p{L}\p{N}\-]+\.)+[\p{L}\p{N}\-]+$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DomainShape();
}
