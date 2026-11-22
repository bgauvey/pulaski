using System.Globalization;

namespace Pulaski.Web.Services.Localization;

/// <summary>
/// Dates and numbers in the reader's region (#128, D-114). A reader who chose no region (and reads English) keeps Pulaski's usual formats
/// (2026-11-22 15:04, 1,234.5): the request's culture is then the invariant one.
/// </summary>
public static class Formats
{
    /// <summary>Whether the reader chose a region (or a language) whose formats the screens follow.</summary>
    public static bool IsRegional => !Equals(CultureInfo.CurrentCulture, CultureInfo.InvariantCulture);
}
