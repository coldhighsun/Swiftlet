using Swiftlet.Domain.Contacts;

namespace Swiftlet.Desktop.Settings;

/// <summary>
/// The stored values of the order in which contact names are shown.
/// </summary>
public static class ContactNameOrder
{
    /// <summary>
    /// The order follows the script of the name.
    /// </summary>
    public const string Auto = "auto";

    /// <summary>
    /// Family name first.
    /// </summary>
    public const string FamilyFirst = "familyFirst";

    /// <summary>
    /// Given name first.
    /// </summary>
    public const string GivenFirst = "givenFirst";

    /// <summary>
    /// Maps a stored value to a known order.
    /// </summary>
    /// <param name="value">The stored value.</param>
    /// <returns>The value when it is known, otherwise <see cref="Auto"/>.</returns>
    public static string Normalize(string? value)
    {
        return value is FamilyFirst or GivenFirst ? value : Auto;
    }

    /// <summary>
    /// Gets the order a stored value stands for.
    /// </summary>
    /// <param name="value">The stored value.</param>
    /// <returns>The order; <see cref="NameOrder.Auto"/> for an unknown value.</returns>
    public static NameOrder ToNameOrder(string? value)
    {
        return value switch
        {
            FamilyFirst => NameOrder.FamilyFirst,
            GivenFirst => NameOrder.GivenFirst,
            _ => NameOrder.Auto,
        };
    }
}
