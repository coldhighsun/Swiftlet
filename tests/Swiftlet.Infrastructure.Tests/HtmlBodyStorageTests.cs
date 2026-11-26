using System.Text;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Persistence;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="HtmlBodyCodec"/> and the compressed storage of <see cref="MailMessage.BodyHtml"/>.
/// </summary>
public class HtmlBodyStorageTests
{
    /// <summary>
    /// A body repetitive enough to compress well, with non-ASCII text.
    /// </summary>
    private static readonly string LargeHtml = string.Concat(Enumerable.Repeat("<div style=\"color:#333\">会议纪要 meeting notes</div>", 200));

    /// <summary>
    /// Verifies a compressible body gets smaller, carries the marker and comes back unchanged.
    /// </summary>
    [Fact]
    public void Encode_CompressibleBody_ShrinksAndRoundTrips()
    {
        var stored = HtmlBodyCodec.Encode(LargeHtml);

        stored.Take(2).Should().Equal(0xFF, 0x01);
        stored.Length.Should().BeLessThan(Encoding.UTF8.GetByteCount(LargeHtml) / 4);
        HtmlBodyCodec.Decode(stored).Should().Be(LargeHtml);
    }

    /// <summary>
    /// Verifies a body that compression would not shrink is kept as plain UTF-8.
    /// </summary>
    /// <param name="html">The body.</param>
    [Theory]
    [InlineData("")]
    [InlineData("<p>你好</p>")]
    public void Encode_IncompressibleBody_KeepsPlainUtf8(string html)
    {
        var stored = HtmlBodyCodec.Encode(html);

        stored.Should().Equal(Encoding.UTF8.GetBytes(html));
        HtmlBodyCodec.Decode(stored).Should().Be(html);
    }

    /// <summary>
    /// Verifies a saved body is stored compressed and loaded as the original text.
    /// </summary>
    [Fact]
    public async Task Save_LargeBody_StoresCompressedBlobAndLoadsOriginal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        await db.CreateMessageRepository().AddRangeAsync([Message(account.Id, folder.Id, LargeHtml)], ct);
        await using var context = await db.Factory.CreateAsync(account.Id, ct);

        var type = await context.Database.SqlQueryRaw<string>("SELECT typeof(BodyHtml) AS Value FROM Messages").SingleAsync(ct);
        var length = await context.Database.SqlQueryRaw<int>("SELECT length(BodyHtml) AS Value FROM Messages").SingleAsync(ct);
        var loaded = await context.Messages.AsNoTracking().SingleAsync(ct);

        type.Should().Be("blob");
        length.Should().BeLessThan(LargeHtml.Length / 4);
        loaded.BodyHtml.Should().Be(LargeHtml);
    }

    /// <summary>
    /// Verifies a message without HTML stays without it.
    /// </summary>
    [Fact]
    public async Task Save_NoBody_StaysNull()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        await db.CreateMessageRepository().AddRangeAsync([Message(account.Id, folder.Id, null)], ct);
        await using var context = await db.Factory.CreateAsync(account.Id, ct);

        var loaded = await context.Messages.AsNoTracking().SingleAsync(ct);

        loaded.BodyHtml.Should().BeNull();
    }

    /// <summary>
    /// Verifies a row written as plain text by an earlier version is still readable.
    /// </summary>
    [Fact]
    public async Task Load_PlainTextRow_ReadsText()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        await db.CreateMessageRepository().AddRangeAsync([Message(account.Id, folder.Id, "x")], ct);
        await using var context = await db.Factory.CreateAsync(account.Id, ct);
        await context.Database.ExecuteSqlAsync($"UPDATE Messages SET BodyHtml = {LargeHtml}", ct);

        var type = await context.Database.SqlQueryRaw<string>("SELECT typeof(BodyHtml) AS Value FROM Messages").SingleAsync(ct);
        var loaded = await context.Messages.AsNoTracking().SingleAsync(ct);

        type.Should().Be("text");
        loaded.BodyHtml.Should().Be(LargeHtml);
    }

    /// <summary>
    /// Creates a message with the given HTML body.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="folderId">The folder.</param>
    /// <param name="html">The HTML body.</param>
    /// <returns>The message.</returns>
    private static MailMessage Message(Guid accountId, Guid folderId, string? html)
    {
        return MailMessage.Create(accountId, folderId, 1, "s", "A", "a@example.com", "r", DateTimeOffset.UnixEpoch, "body", html);
    }
}
