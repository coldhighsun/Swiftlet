using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Swiftlet.Infrastructure.Persistence.Configurations;

/// <summary>
/// Stores an HTML body compressed, see <see cref="HtmlBodyCodec"/>.
/// </summary>
internal sealed class CompressedHtmlConverter() : ValueConverter<string, byte[]>(
    html => HtmlBodyCodec.Encode(html),
    stored => HtmlBodyCodec.Decode(stored));
