using System.Reflection;
using ARI.API.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ARI.API.Controllers;

/// <summary>
/// A machine-readable description of every route (OpenAPI 3), built from the routes themselves so it can't drift from the
/// code: address, verb, what goes in the address and query, the shape of the body, and who may call it.
/// </summary>
[Route("api/v1/openapi.json")]
[ApiController]
public class OpenApiController(IApiDescriptionGroupCollectionProvider routes) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        SortedDictionary<string, SortedDictionary<string, object>> paths = new();

        foreach (ApiDescription route in routes.ApiDescriptionGroups.Items.SelectMany(g => g.Items))
        {
            if (route.HttpMethod is null || route.RelativePath is null) continue;
            string path = "/" + route.RelativePath.Split('?')[0].TrimStart('/');
            // Route constraints ({id:int}) are not part of the OpenAPI address.
            path = System.Text.RegularExpressions.Regex.Replace(path, @"\{([^}:]+)(:[^}]*)?\}", "{$1}");

            List<object> parameters = [];
            object? body = null;
            foreach (ApiParameterDescription p in route.ParameterDescriptions)
            {
                if (p.Source == BindingSource.Path || p.Source == BindingSource.Query)
                    parameters.Add(new
                    {
                        name     = p.Name,
                        @in      = p.Source == BindingSource.Path ? "path" : "query",
                        required = p.Source == BindingSource.Path || p.IsRequired,
                        schema   = SchemaOf(p.Type),
                    });
                else if (p.Source == BindingSource.Body)
                    body = new { required = true, content = new Dictionary<string, object> { ["application/json"] = new { schema = SchemaOf(p.Type) } } };
                else if (p.Source == BindingSource.FormFile || p.Source == BindingSource.Form)
                    body = new { required = true, content = new Dictionary<string, object> { ["multipart/form-data"] = new { schema = new { type = "object", properties = new { file = new { type = "string", format = "binary" } } } } } };
            }

            string method = route.HttpMethod.ToLowerInvariant();
            bool admin = ("/" + route.RelativePath).StartsWith(AuthMiddleware.AdminPrefix, StringComparison.OrdinalIgnoreCase);
            string action = (route.ActionDescriptor as ControllerActionDescriptor)?.ActionName ?? method;

            Dictionary<string, object> operation = new()
            {
                ["operationId"] = $"{method}_{path.Trim('/').Replace('/', '_').Replace("{", "").Replace("}", "")}",
                ["summary"]     = action,
                ["tags"]        = new[] { TagOf(path) },
                ["parameters"]  = parameters,
                ["x-adminOnly"] = admin,
                ["responses"]   = new Dictionary<string, object>
                {
                    ["200"]     = new { description = "Success: the thing itself, or { \"ok\": true } when there is nothing to return." },
                    ["default"] = new { description = "Failure: { \"error\": \"what went wrong\" } with a status of 400 or more." },
                },
            };
            if (body is not null) operation["requestBody"] = body;

            if (!paths.TryGetValue(path, out SortedDictionary<string, object>? verbs)) paths[path] = verbs = new();
            verbs[method] = operation;
        }

        // The two WebSockets are not routes MVC knows about.
        AddSocket(paths, "/api/v1/client", "ARI Desktop connects here so ARI can use its files and tools. Query: threadKey.");
        AddSocket(paths, "/api/v1/listener/stream", "The microphone stream. Query: threadKey, source.");

        return Ok(new
        {
            openapi = "3.0.3",
            info    = new { title = "ARI API", version = "v1", description = "Everything under /api/v1/admin is for admins only. Everything else needs a signed-in person; each route that holds someone's own data checks it is theirs." },
            servers = new[] { new { url = "/" } },
            components = new { securitySchemes = new { bearer = new { type = "http", scheme = "bearer" } } },
            security = new[] { new Dictionary<string, string[]> { ["bearer"] = [] } },
            paths,
        });
    }

    private static void AddSocket(SortedDictionary<string, SortedDictionary<string, object>> paths, string path, string description)
        => paths[path] = new SortedDictionary<string, object>
        {
            ["get"] = new Dictionary<string, object>
            {
                ["operationId"] = "websocket_" + path.Trim('/').Replace('/', '_'),
                ["summary"]     = description,
                ["tags"]        = new[] { "sockets" },
                ["x-adminOnly"] = false,
                ["responses"]   = new Dictionary<string, object> { ["101"] = new { description = "Switching to a WebSocket." } },
            },
        };

    private static string TagOf(string path)
    {
        string[] parts = path.Trim('/').Split('/');
        // /api/v1/admin/<area>/... groups by area; /api/v1/<area>/... by area.
        int i = parts.Length > 3 && parts[2] == "admin" ? 3 : 2;
        return parts.Length > i ? (i == 3 ? "admin/" : "") + parts[i] : "api";
    }

    private static object SchemaOf(Type? type, int depth = 0)
    {
        if (type is null) return new { type = "object" };
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type == typeof(Guid) || type == typeof(char)) return new { type = "string" };
        if (type == typeof(bool)) return new { type = "boolean" };
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)) return new { type = "integer" };
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return new { type = "number" };
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return new { type = "string", format = "date-time" };
        if (type.IsEnum) return new { type = "string", @enum = Enum.GetNames(type) };
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string))
        {
            Type? item = type.IsArray ? type.GetElementType() : type.GetGenericArguments().FirstOrDefault();
            return new { type = "array", items = depth > 3 ? new { type = "object" } : SchemaOf(item, depth + 1) };
        }
        if (type.FullName?.StartsWith("System.Text.Json.Nodes") == true) return new { type = "object", additionalProperties = true };
        if (depth > 3) return new { type = "object" };

        Dictionary<string, object> props = new();
        foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.GetIndexParameters().Length == 0)
                props[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = SchemaOf(p.PropertyType, depth + 1);
        return new { type = "object", properties = props };
    }
}
