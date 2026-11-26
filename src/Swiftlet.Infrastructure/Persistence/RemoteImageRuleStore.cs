using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;

namespace Swiftlet.Infrastructure.Persistence;

/// <summary>
/// Keeps the remote image rules in the global store and a sorted in-memory copy for instant lookups.
/// </summary>
/// <param name="scopes">Creates the scopes that provide the global database context (this service is a singleton).</param>
/// <param name="clock">The clock used for the time a rule was added.</param>
/// <param name="logger">The logger; optional.</param>
internal sealed class RemoteImageRuleStore(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<RemoteImageRuleStore>? logger = null) : IRemoteImageRules, IDisposable
{
    /// <summary>
    /// Serializes changes so the database and the in-memory copy are updated as one step.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// The current rules; replaced as a whole on every change, so readers never need a lock.
    /// </summary>
    private volatile ImmutableSortedSet<string> _rules = ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<GlobalDbContext>();
            var saved = await context.RemoteImageRules.AsNoTracking().Select(r => r.Rule).ToListAsync(cancellationToken);
            _rules = ImmutableSortedSet.CreateRange(StringComparer.Ordinal, saved);
            logger?.LogInformation("Loaded {Count} remote image rules", saved.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public bool IsAllowed(string? fromAddress)
    {
        var rules = _rules;
        return RemoteImageRule.Candidates(fromAddress).Any(rules.Contains);
    }

    /// <inheritdoc />
    public async Task<bool> AddAsync(string rule, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(rule);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_rules.Contains(normalized))
            {
                return false;
            }

            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<GlobalDbContext>();
            context.RemoteImageRules.Add(new RemoteImageRuleEntry { Rule = normalized, AddedAt = clock.GetUtcNow() });
            await context.SaveChangesAsync(cancellationToken);

            // The in-memory copy changes only after the row is saved, so a failed write leaves both unchanged.
            _rules = _rules.Add(normalized);
            logger?.LogInformation("Remote image rule added: {Rule}", normalized);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string rule, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(rule);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_rules.Contains(normalized))
            {
                return false;
            }

            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<GlobalDbContext>();
            await context.RemoteImageRules.Where(r => r.Rule == normalized).ExecuteDeleteAsync(cancellationToken);
            _rules = _rules.Remove(normalized);
            logger?.LogInformation("Remote image rule removed: {Rule}", normalized);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public (IReadOnlyList<string> Rules, int TotalMatches) Search(string? filter, int take)
    {
        var text = filter?.Trim();
        var matches = string.IsNullOrEmpty(text)
            ? _rules
            : _rules.Where(r => r.Contains(text, StringComparison.OrdinalIgnoreCase));
        var list = matches.ToList();
        return (list.Take(take).ToList(), list.Count);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <summary>
    /// Normalizes a rule, rejecting values that are not a valid address or domain.
    /// </summary>
    /// <param name="rule">The rule as given by the caller.</param>
    /// <returns>The normalized rule.</returns>
    private static string Normalize(string rule)
    {
        return RemoteImageRule.TryNormalize(rule, out var normalized)
            ? normalized
            : throw new ArgumentException($"'{rule}' is not a valid sender address or domain.", nameof(rule));
    }
}
