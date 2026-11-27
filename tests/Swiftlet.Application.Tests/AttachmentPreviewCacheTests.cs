using AwesomeAssertions;
using Swiftlet.Application.Abstractions;

namespace Swiftlet.Application.Tests;

/// <summary>
/// Tests for <see cref="AttachmentPreviewCache"/>.
/// </summary>
public class AttachmentPreviewCacheTests
{
    /// <summary>
    /// Creates a small text preview.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The preview.</returns>
    private static AttachmentPreview Text(string text)
    {
        return new AttachmentPreview(AttachmentPreviewKind.Text, Text: text);
    }

    /// <summary>
    /// Verifies an added preview is found again.
    /// </summary>
    [Fact]
    public void TryGet_AfterAdd_ReturnsPreview()
    {
        var cache = new AttachmentPreviewCache();
        var id = Guid.NewGuid();
        cache.Add(id, Text("hello"));

        var found = cache.TryGet(id, out var preview);

        found.Should().BeTrue();
        preview.Text.Should().Be("hello");
    }

    /// <summary>
    /// Verifies an unknown attachment is not found.
    /// </summary>
    [Fact]
    public void TryGet_Unknown_ReturnsFalse()
    {
        var cache = new AttachmentPreviewCache();

        cache.TryGet(Guid.NewGuid(), out _).Should().BeFalse();
    }

    /// <summary>
    /// Verifies the least recently used preview is dropped first when there are too many entries.
    /// </summary>
    [Fact]
    public void Add_MoreThanLimit_DropsLeastRecentlyUsed()
    {
        var cache = new AttachmentPreviewCache();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        cache.Add(first, Text("1"));
        cache.Add(second, Text("2"));
        cache.TryGet(first, out _);

        for (var i = 0; i < 19; i++)
        {
            cache.Add(Guid.NewGuid(), Text("x"));
        }

        cache.Count.Should().Be(20);
        cache.TryGet(first, out _).Should().BeTrue();
        cache.TryGet(second, out _).Should().BeFalse();
    }

    /// <summary>
    /// Verifies the total size is bounded.
    /// </summary>
    [Fact]
    public void Add_LargeImages_StaysWithinByteLimit()
    {
        var cache = new AttachmentPreviewCache();
        for (var i = 0; i < 5; i++)
        {
            cache.Add(Guid.NewGuid(), new AttachmentPreview(AttachmentPreviewKind.Image, Image: new byte[8 * 1024 * 1024]));
        }

        cache.Count.Should().Be(2);
    }

    /// <summary>
    /// Verifies adding the same attachment again replaces its preview.
    /// </summary>
    [Fact]
    public void Add_SameId_ReplacesPreview()
    {
        var cache = new AttachmentPreviewCache();
        var id = Guid.NewGuid();
        cache.Add(id, Text("old"));

        cache.Add(id, Text("new"));

        cache.Count.Should().Be(1);
        cache.TryGet(id, out var preview);
        preview.Text.Should().Be("new");
    }
}
