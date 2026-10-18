namespace Pulaski.Web.Services.Security;

/// <summary>
/// A page only users with one of these permissions may see (D-039). Without one the page answers "not found", so its
/// feature isn't even named. A change permission brings its views, so holding it is enough.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequiresViewAttribute(params string[] anyOf) : Attribute
{
    public IReadOnlyList<string> AnyOf { get; } = anyOf;
}
