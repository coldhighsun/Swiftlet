using System.Text.Json;
using Swiftlet.Domain.Labels;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Ai;

/// <summary>
/// Reads the classification a model answered with. Models often wrap the JSON in other text, and what they name is not
/// trusted: only existing labels and the known priorities come out.
/// </summary>
public static class ClassificationParser
{
    /// <summary>
    /// Reads an answer.
    /// </summary>
    /// <param name="answer">The text of the model.</param>
    /// <param name="labels">The existing labels.</param>
    /// <returns>The suggested label identifiers and priority; both empty when the answer cannot be read.</returns>
    public static (IReadOnlyList<Guid> LabelIds, MessagePriority? Priority) Parse(string answer, IReadOnlyList<Label> labels)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(labels);
        var end = answer.LastIndexOf('}');

        // Each opening brace is tried, because text before the JSON may contain braces too.
        for (var start = answer.IndexOf('{'); start >= 0 && start < end; start = answer.IndexOf('{', start + 1))
        {
            try
            {
                using var document = JsonDocument.Parse(answer.AsMemory(start, end - start + 1));
                return Read(document.RootElement, labels);
            }
            catch (JsonException)
            {
                // Not the start of the JSON; try the next brace.
            }
        }

        return ([], null);
    }

    /// <summary>
    /// Takes the known values out of the JSON object.
    /// </summary>
    /// <param name="root">The object.</param>
    /// <param name="labels">The existing labels.</param>
    /// <returns>The suggestion.</returns>
    private static (IReadOnlyList<Guid> LabelIds, MessagePriority? Priority) Read(JsonElement root, IReadOnlyList<Label> labels)
    {
        List<Guid> ids = [];
        if (root.TryGetProperty("labels", out var names) && names.ValueKind == JsonValueKind.Array)
        {
            var wanted = names.EnumerateArray()
                .Where(n => n.ValueKind == JsonValueKind.String)
                .Select(n => n.GetString()!.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            ids = [.. labels.Where(l => wanted.Contains(l.Name)).Select(l => l.Id)];
        }

        MessagePriority? priority = null;
        if (root.TryGetProperty("priority", out var value) && value.ValueKind == JsonValueKind.String)
        {
            priority = value.GetString()!.Trim().ToUpperInvariant() switch
            {
                "HIGH" => MessagePriority.High,
                "NORMAL" => MessagePriority.Normal,
                "LOW" => MessagePriority.Low,
                _ => null,
            };
        }

        return (ids, priority);
    }
}
