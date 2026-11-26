namespace Swiftlet.Application.Abstractions;

/// <summary>
/// An image embedded in a message body and referenced from its HTML through a <c>cid:</c> URL.
/// </summary>
/// <param name="ContentType">The image MIME type, for example <c>image/png</c>.</param>
/// <param name="Content">The decoded image bytes.</param>
public sealed record InlineImage(string ContentType, ReadOnlyMemory<byte> Content);
