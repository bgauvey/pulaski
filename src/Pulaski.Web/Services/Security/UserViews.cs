namespace Pulaski.Web.Services.Security;

/// <summary>A page the user starred (#131): where it is and what it was called.</summary>
public sealed record FavoritePage(string Href, string Title);

/// <summary>Favourites, home page and saved filters as the screens use them (#131). Pure.</summary>
public static class UserViews
{
    public const string HomeKey = "home";
    public const string FavoritesKey = "favorites";
    public const int MaxFavorites = 20;

    /// <summary>The key a list page keeps its filters under: <c>filters:inventory/lots</c>.</summary>
    public static string FiltersKey(string page) => $"filters:{page}";

    /// <summary>A page's place without its query or fragment, as favourites and the home page keep it: <c>inventory/lots</c>.</summary>
    public static string PageOf(string relativePath) => relativePath.Split('?', '#')[0].Trim('/');

    /// <summary>The page's title as the browser shows it, without the app's name: "Lots · Inventory".</summary>
    public static string TitleOf(string documentTitle, string fallback)
    {
        var title = documentTitle.EndsWith(" · Pulaski", StringComparison.Ordinal) ? documentTitle[..^" · Pulaski".Length] : documentTitle;
        return string.IsNullOrWhiteSpace(title) || title == "Pulaski" ? fallback : title.Trim();
    }

    /// <summary>Adds or removes <paramref name="page"/>; newest last, at most <see cref="MaxFavorites"/>.</summary>
    public static IReadOnlyList<FavoritePage> Toggle(IReadOnlyList<FavoritePage>? favorites, FavoritePage page)
    {
        var list = (favorites ?? []).ToList();
        if (list.RemoveAll(f => f.Href == page.Href) > 0)
        {
            return list;
        }

        list.Add(page);
        return list.Count > MaxFavorites ? list[^MaxFavorites..] : list;
    }

    public static bool IsFavorite(IReadOnlyList<FavoritePage>? favorites, string href) => favorites?.Any(f => f.Href == href) == true;
}
