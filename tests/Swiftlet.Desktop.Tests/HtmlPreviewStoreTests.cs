using AwesomeAssertions;
using Swiftlet.Desktop.Html;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for <see cref="HtmlPreviewStore"/>.
/// </summary>
public sealed class HtmlPreviewStoreTests : IDisposable
{
    /// <summary>
    /// The directory standing in for the system temporary directory.
    /// </summary>
    private readonly string _root = Directory.CreateTempSubdirectory("swiftlet-store-test-").FullName;

    /// <summary>
    /// Verifies the file carries the restrictive policy and sits in a dedicated subdirectory.
    /// </summary>
    [Fact]
    public void Write_Html_CreatesProtectedDocumentInPreviewDirectory()
    {
        var store = new HtmlPreviewStore(_root);

        var path = store.Write("<p>Hi</p>");

        var directory = Path.GetDirectoryName(path)!;
        Path.GetDirectoryName(directory).Should().Be(_root);
        Path.GetFileName(directory).Should().StartWith("swiftlet-preview-");
        Path.GetExtension(path).Should().Be(".html");
        File.ReadAllText(path).Should().Be(HtmlPreviewFile.Build("<p>Hi</p>"));
    }

    /// <summary>
    /// Verifies several previews of one run share the directory without overwriting each other.
    /// </summary>
    [Fact]
    public void Write_Twice_UsesOneDirectoryAndTwoFiles()
    {
        var store = new HtmlPreviewStore(_root);

        var first = store.Write("<p>1</p>");
        var second = store.Write("<p>2</p>");

        second.Should().NotBe(first);
        Path.GetDirectoryName(second).Should().Be(Path.GetDirectoryName(first));
        File.Exists(first).Should().BeTrue();
    }

    /// <summary>
    /// Verifies the previews of this run are removed.
    /// </summary>
    [Fact]
    public void Clear_AfterWrite_DeletesPreviewDirectory()
    {
        var store = new HtmlPreviewStore(_root);
        var path = store.Write("<p>Hi</p>");

        store.Clear();

        Directory.Exists(Path.GetDirectoryName(path)).Should().BeFalse();
    }

    /// <summary>
    /// Verifies writing works again after the previews were cleared.
    /// </summary>
    [Fact]
    public void Write_AfterClear_CreatesDirectoryAgain()
    {
        var store = new HtmlPreviewStore(_root);
        store.Write("<p>1</p>");
        store.Clear();

        var path = store.Write("<p>2</p>");

        File.Exists(path).Should().BeTrue();
    }

    /// <summary>
    /// Verifies writing works when the directory was removed from outside, for example by a second instance starting.
    /// </summary>
    [Fact]
    public void Write_DirectoryRemovedExternally_CreatesItAgain()
    {
        var store = new HtmlPreviewStore(_root);
        store.Write("<p>1</p>");
        new HtmlPreviewStore(_root).RemoveLeftovers();

        var path = store.Write("<p>2</p>");

        File.Exists(path).Should().BeTrue();
    }

    /// <summary>
    /// Verifies clearing without any preview does nothing.
    /// </summary>
    [Fact]
    public void Clear_NothingWritten_LeavesRootUntouched()
    {
        var store = new HtmlPreviewStore(_root);

        store.Clear();

        Directory.EnumerateFileSystemEntries(_root).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies preview directories left by an earlier run and files written by earlier versions are removed.
    /// </summary>
    [Fact]
    public void RemoveLeftovers_EarlierPreviews_DeletesThem()
    {
        var oldPath = new HtmlPreviewStore(_root).Write("<p>old</p>");
        var legacy = Path.Combine(_root, $"swiftlet-{Guid.NewGuid():N}.html");
        File.WriteAllText(legacy, "legacy");

        new HtmlPreviewStore(_root).RemoveLeftovers();

        Directory.Exists(Path.GetDirectoryName(oldPath)).Should().BeFalse();
        File.Exists(legacy).Should().BeFalse();
    }

    /// <summary>
    /// Verifies files the application did not write are kept.
    /// </summary>
    /// <param name="name">The name of a file that only resembles a preview.</param>
    [Theory]
    [InlineData("swiftlet-notes.html")]
    [InlineData("swiftlet-0123456789abcdef0123456789abcdef.txt")]
    [InlineData("swiftlet-0123456789abcdef0123456789abcdef-copy.html")]
    [InlineData("report.html")]
    public void RemoveLeftovers_UnrelatedFile_KeepsIt(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "keep");

        new HtmlPreviewStore(_root).RemoveLeftovers();

        File.Exists(path).Should().BeTrue();
    }

    /// <summary>
    /// Verifies a leftover that cannot be deleted does not stop the others from being removed.
    /// </summary>
    [Fact]
    public void RemoveLeftovers_LockedFile_ContinuesWithTheRest()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to delete an open file.");
        var locked = Path.Combine(_root, "swiftlet-00000000000000000000000000000000.html");
        var free = Path.Combine(_root, "swiftlet-ffffffffffffffffffffffffffffffff.html");
        File.WriteAllText(free, "free");
        using var stream = new FileStream(locked, FileMode.Create, FileAccess.Write, FileShare.None);

        new HtmlPreviewStore(_root).RemoveLeftovers();

        File.Exists(free).Should().BeFalse();
        File.Exists(locked).Should().BeTrue();
    }

    /// <summary>
    /// Verifies a missing temporary directory does not stop startup.
    /// </summary>
    [Fact]
    public void RemoveLeftovers_RootMissing_DoesNothing()
    {
        var store = new HtmlPreviewStore(Path.Combine(_root, "missing"));

        var act = store.RemoveLeftovers;

        act.Should().NotThrow();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }
}
