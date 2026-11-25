using Mediator;
using Swiftlet.Domain.Labels;

namespace Swiftlet.Application.Labels;

/// <summary>
/// Gets all labels ordered by name.
/// </summary>
public sealed record GetLabelsQuery : IQuery<IReadOnlyList<Label>>;

/// <summary>
/// Handles <see cref="GetLabelsQuery"/>.
/// </summary>
/// <param name="labels">The label repository.</param>
public sealed class GetLabelsQueryHandler(ILabelRepository labels) : IQueryHandler<GetLabelsQuery, IReadOnlyList<Label>>
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<Label>> Handle(GetLabelsQuery query, CancellationToken cancellationToken)
    {
        return await labels.GetAllAsync(cancellationToken);
    }
}
