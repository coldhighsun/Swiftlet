using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Swiftlet.Infrastructure.Persistence;

/// <summary>
/// Stores <see cref="DateTimeOffset"/> values as Unix milliseconds so SQLite can sort and compare them.
/// </summary>
public sealed class UnixMillisecondsConverter()
    : ValueConverter<DateTimeOffset, long>(
        value => value.ToUnixTimeMilliseconds(),
        value => DateTimeOffset.FromUnixTimeMilliseconds(value));
