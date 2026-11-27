namespace Pulaski.Web.Services.Security;

/// <summary>A page the user starred (#131): where it is and what it was called.</summary>
public sealed record FavoritePage(string Href, string Title);

/// <summary>Favorites, home page and saved filters as the screens use them (#131). Pure.</summary>
public static class UserViews
{
    public const string HomeKey = "home";
    public const string FavoritesKey = "favorites";
    public const int MaxFavorites = 20;

    /// <summary>The key a list page keeps its filters under: <c>filters:inventory/lots</c>.</summary>
    public static string FiltersKey(string page) => $"filters:{page}";

    /// <summary>A page's place without its query or fragment, as favorites and the home page keep it: <c>inventory/lots</c>.</summary>
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

/// <summary>A list page's remembered filters (#131): read from the user's profile, and kept when they search or change one.</summary>
public static class SavedFilters
{
    public static T? Load<T>(CurrentUser user, string page) => user.Setting<T>(UserViews.FiltersKey(page));

    /// <summary>Keeps the filters (null forgets them); a failure to save only means they aren't remembered this time.</summary>
    public static async Task SaveAsync<T>(CurrentUser user, PulaskiApiClient api, string page, T? filters)
    {
        try
        {
            await user.SetSettingAsync(api, UserViews.FiltersKey(page), filters);
        }
        catch (ApiException)
        {
        }
    }
}
