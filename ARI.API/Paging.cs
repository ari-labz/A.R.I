using Microsoft.AspNetCore.Mvc;

namespace ARI.API;

/// <summary>
/// Optional paging for any list: add <c>?limit=50&amp;offset=100</c> to take a slice. With neither, you get the whole list,
/// so existing callers are unaffected. The total size always comes back in the <c>X-Total-Count</c> header.
/// </summary>
public static class Paging
{
    public static List<T> Page<T>(this ControllerBase controller, IReadOnlyList<T> all)
    {
        controller.Response.Headers["X-Total-Count"] = all.Count.ToString();
        controller.Request.Query.TryGetValue("limit", out Microsoft.Extensions.Primitives.StringValues limitText);
        controller.Request.Query.TryGetValue("offset", out Microsoft.Extensions.Primitives.StringValues offsetText);
        int offset = int.TryParse(offsetText, out int o) && o > 0 ? o : 0;
        IEnumerable<T> slice = all.Skip(offset);
        if (int.TryParse(limitText, out int limit) && limit > 0) slice = slice.Take(limit);
        return slice.ToList();
    }
}
