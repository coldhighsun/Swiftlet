namespace Swiftlet.Domain.Contacts;

/// <summary>
/// The order in which the two parts of a name are shown.
/// </summary>
public enum NameOrder
{
    /// <summary>
    /// Family name first for Chinese, Japanese and Korean names, otherwise given name first.
    /// </summary>
    Auto,

    /// <summary>
    /// Family name first for every name.
    /// </summary>
    FamilyFirst,

    /// <summary>
    /// Given name first for every name.
    /// </summary>
    GivenFirst,
}

/// <summary>
/// Joins a given and a family name into a display name, and splits a display name back.
/// </summary>
public static class PersonName
{
    /// <summary>
    /// Chinese family names of two characters, in simplified and traditional writing.
    /// </summary>
    private static readonly HashSet<string> CompoundFamilyNames = new(StringComparer.Ordinal)
    {
        "欧阳", "歐陽", "太史", "端木", "上官", "司马", "司馬", "东方", "東方", "独孤", "獨孤", "南宫", "南宮", "夏侯", "诸葛", "諸葛",
        "尉迟", "尉遲", "皇甫", "公孙", "公孫", "慕容", "长孙", "長孫", "宇文", "司徒", "轩辕", "軒轅", "西门", "西門", "令狐",
    };

    /// <summary>
    /// Joins the names in the asked order. Chinese, Japanese and Korean names are joined without a space; by default they
    /// put the family name first and all others the given name.
    /// </summary>
    /// <param name="firstName">The given name.</param>
    /// <param name="lastName">The family name.</param>
    /// <param name="order">Which part comes first.</param>
    /// <returns>The display name; empty when both parts are.</returns>
    public static string Compose(string firstName, string lastName, NameOrder order = NameOrder.Auto)
    {
        ArgumentNullException.ThrowIfNull(firstName);
        ArgumentNullException.ThrowIfNull(lastName);

        if (firstName.Length == 0 || lastName.Length == 0)
        {
            return string.Concat(firstName, lastName);
        }

        var eastAsian = IsEastAsian(firstName) || IsEastAsian(lastName);
        var familyFirst = order == NameOrder.FamilyFirst || (order == NameOrder.Auto && eastAsian);
        var (first, second) = familyFirst ? (lastName, firstName) : (firstName, lastName);
        return eastAsian ? first + second : $"{first} {second}";
    }

    /// <summary>
    /// Guesses the parts of a display name that comes as one text: "family, given", a Chinese name of two to four
    /// characters, or words of which the last one is the family name. A text that fits none of these is kept whole as
    /// the given name.
    /// </summary>
    /// <param name="displayName">The display name.</param>
    /// <returns>The given and family name; either may be empty.</returns>
    public static (string FirstName, string LastName) Split(string? displayName)
    {
        var name = (displayName ?? string.Empty).Trim();

        var comma = name.IndexOfAny([',', '，']);
        if (comma >= 0)
        {
            return (name[(comma + 1)..].Trim(), name[..comma].Trim());
        }

        var space = name.LastIndexOf(' ');
        if (IsEastAsian(name))
        {
            // "张 三" and "山田 太郎" put the family name first.
            if (space >= 0)
            {
                var first = name.IndexOf(' ', StringComparison.Ordinal);
                return (name[(first + 1)..].Trim(), name[..first]);
            }

            if (name.Length is < 2 or > 4 || !name.All(IsHan))
            {
                return (name, string.Empty);
            }

            var familyLength = name.Length > 2 && CompoundFamilyNames.Contains(name[..2]) ? 2 : 1;
            return (name[familyLength..], name[..familyLength]);
        }

        return space < 0 ? (name, string.Empty) : (name[..space].TrimEnd(), name[(space + 1)..]);
    }

    /// <summary>
    /// Determines whether a name contains Chinese, Japanese or Korean characters.
    /// </summary>
    /// <param name="value">The name.</param>
    /// <returns><see langword="true"/> if it does.</returns>
    private static bool IsEastAsian(string value)
    {
        return value.Any(c => IsHan(c) || c is (>= '぀' and <= 'ヿ') or (>= '가' and <= '힯'));
    }

    /// <summary>
    /// Determines whether a character is a Chinese character of the basic planes.
    /// </summary>
    /// <param name="c">The character.</param>
    /// <returns><see langword="true"/> if it is.</returns>
    private static bool IsHan(char c) => c is >= '㐀' and <= '鿿';
}
