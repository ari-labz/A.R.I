using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ARI.API;

/// <summary>
/// A "name" (a model, a stage, a saved setup, an engine) is one plain file-name-like word, never a path. This is the one
/// check for that, and a filter that applies it to every part of an address and to the query values that name things,
/// so no controller has to remember to.
/// </summary>
public static class SafeNames
{
    public static bool IsSafe(string? name)
        => !string.IsNullOrWhiteSpace(name) && name != "." && name != ".."
           && name.IndexOfAny(['/', '\\', '\0']) < 0 && !name.Contains("..");

    private static readonly string[] NamingQueryKeys = ["name", "modelName", "engine", "stageId", "stem", "file", "fileName"];

    public sealed class Filter : IActionFilter
    {
        public void OnActionExecuting(ActionExecutingContext context)
        {
            bool bad = context.RouteData.Values.Any(v => v.Value is string s && !IsSafe(s))
                    || NamingQueryKeys.Any(k => context.HttpContext.Request.Query.TryGetValue(k, out var values) && values.Any(v => !IsSafe(v)));
            if (bad) context.Result = new BadRequestObjectResult(new { error = "That name is not allowed." });
        }

        public void OnActionExecuted(ActionExecutedContext context) { }
    }
}
