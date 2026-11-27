using System.Globalization;
using AwesomeAssertions;
using Swiftlet.Desktop.Converters;
using Swiftlet.Desktop.ViewModels.Main;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for <see cref="SearchFilterViewModel"/> and the date picker converter.
/// </summary>
public class SearchFilterViewModelTests
{
    /// <summary>
    /// Verifies days are turned into instants of the given time zone, the last day included.
    /// </summary>
    [Fact]
    public void BuildCriteria_Days_UsesTimeZoneMidnights()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(8), "test", "test");
        var filter = new SearchFilterViewModel { DateFrom = new DateOnly(2026, 3, 1), DateTo = new DateOnly(2026, 3, 1) };

        var criteria = filter.BuildCriteria(string.Empty, null, zone);

        criteria.SentFrom.Should().Be(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.FromHours(8)));
        criteria.SentBefore.Should().Be(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.FromHours(8)));
        filter.HasInvalidDateRange.Should().BeFalse();
    }

    /// <summary>
    /// Verifies the very first and last day limit nothing instead of failing.
    /// </summary>
    [Fact]
    public void BuildCriteria_ExtremeDays_LeaveRangeOpen()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(8), "test", "test");
        var filter = new SearchFilterViewModel { DateFrom = DateOnly.MinValue, DateTo = DateOnly.MaxValue };

        var criteria = filter.BuildCriteria(string.Empty, null, zone);

        criteria.IsEmpty.Should().BeTrue();
    }

    /// <summary>
    /// Verifies a filter without any entry builds empty criteria.
    /// </summary>
    [Fact]
    public void BuildCriteria_NothingEntered_IsEmpty()
    {
        var filter = new SearchFilterViewModel { From = "  " };

        var criteria = filter.BuildCriteria("  ", null, TimeZoneInfo.Utc);

        criteria.IsEmpty.Should().BeTrue();
    }

    /// <summary>
    /// Verifies the converter maps days to picker dates and back, including no selection.
    /// </summary>
    [Fact]
    public void DateOnlyConverter_BothDirections_RoundTrips()
    {
        var day = new DateOnly(2026, 3, 1);

        var date = DateOnlyConverter.Instance.Convert(day, typeof(object), null, CultureInfo.InvariantCulture);
        var back = DateOnlyConverter.Instance.ConvertBack(date, typeof(object), null, CultureInfo.InvariantCulture);

        back.Should().Be(day);
        DateOnlyConverter.Instance.Convert(null, typeof(object), null, CultureInfo.InvariantCulture).Should().BeNull();
        DateOnlyConverter.Instance.ConvertBack(null, typeof(object), null, CultureInfo.InvariantCulture).Should().BeNull();
    }
}
