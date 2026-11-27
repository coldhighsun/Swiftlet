namespace Swiftlet.Application.Abstractions;

/// <summary>
/// Keeps the most recently built attachment previews in memory, so hovering an attachment again costs no download.
/// </summary>
public sealed class AttachmentPreviewCache
{
    /// <summary>
    /// The most previews kept.
    /// </summary>
    private const int MaxEntries = 20;

    /// <summary>
    /// The most image bytes and text characters kept.
    /// </summary>
    private const long MaxBytes = 20 * 1024 * 1024;

    /// <summary>
    /// Guards the list and the total.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// The entries, least recently used first.
    /// </summary>
    private readonly LinkedList<(Guid Id, AttachmentPreview Preview, long Size)> _entries = [];

    /// <summary>
    /// The total size of the entries.
    /// </summary>
    private long _totalSize;

    /// <summary>
    /// Gets the number of cached previews.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Looks up a preview and marks it as recently used.
    /// </summary>
    /// <param name="attachmentId">The attachment identifier.</param>
    /// <param name="preview">The cached preview.</param>
    /// <returns><see langword="true"/> when the preview was cached.</returns>
    public bool TryGet(Guid attachmentId, out AttachmentPreview preview)
    {
        lock (_gate)
        {
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                if (node.Value.Id == attachmentId)
                {
                    _entries.Remove(node);
                    _entries.AddLast(node);
                    preview = node.Value.Preview;
                    return true;
                }
            }
        }

        preview = AttachmentPreview.Unsupported;
        return false;
    }

    /// <summary>
    /// Adds a preview, dropping the least recently used ones when the limits are exceeded.
    /// </summary>
    /// <param name="attachmentId">The attachment identifier.</param>
    /// <param name="preview">The preview.</param>
    public void Add(Guid attachmentId, AttachmentPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var size = (preview.Image?.LongLength ?? 0) + ((preview.Text?.Length ?? 0) * 2L) + 64;
        if (size > MaxBytes)
        {
            return;
        }

        lock (_gate)
        {
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                if (node.Value.Id == attachmentId)
                {
                    _totalSize -= node.Value.Size;
                    _entries.Remove(node);
                    break;
                }
            }

            _entries.AddLast((attachmentId, preview, size));
            _totalSize += size;
            while (_entries.Count > MaxEntries || _totalSize > MaxBytes)
            {
                _totalSize -= _entries.First!.Value.Size;
                _entries.RemoveFirst();
            }
        }
    }
}
