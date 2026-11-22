using System.Globalization;
using System.Runtime.CompilerServices;

namespace Pulaski.Web.Tests;

/// <summary>
/// The screens' formats for a reader who chose no region are Pulaski's usual ones, the invariant culture's (#128); the tests read them so,
/// whatever the machine's own culture.
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void Invariant()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }
}
