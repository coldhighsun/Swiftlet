namespace Swiftlet.Domain.Labels;

/// <summary>
/// Persistence operations for labels.
/// </summary>
public interface ILabelRepository
{
    /// <summary>
    /// Adds a label and saves it.
    /// </summary>
    /// <param name="label">The label to add.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the label is saved.</returns>
    Task AddAsync(Label label, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a label by identifier.
    /// </summary>
    /// <param name="id">The label identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The label, or <see langword="null"/> if not found.</returns>
    Task<Label?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all labels ordered by name.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The labels.</returns>
    Task<IReadOnlyList<Label>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether a label name is already used, ignoring case and surrounding whitespace.
    /// </summary>
    /// <param name="name">The name to check.</param>
    /// <param name="exceptId">A label to exclude from the check, if any.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> if another label has the name.</returns>
    Task<bool> NameExistsAsync(string name, Guid? exceptId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a label.
    /// </summary>
    /// <param name="id">The label identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the label is removed.</returns>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves pending changes.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when changes are saved.</returns>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
