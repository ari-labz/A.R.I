using ARI.Common;
using Microsoft.AspNetCore.Http;

namespace ARI.API;

/// <summary>Custom logos uploaded from the control panel. Each slot replaces one of the built-in images:
/// the server answers requests for that image's URL with the uploaded file instead, so every page, the
/// login screen and the service worker pick it up without any UI change.</summary>
public static class BrandingStore
{
    public sealed record Slot(string Key, string Label, string DefaultPath, string Used);

    public static readonly Slot[] Slots =
    [
        new("logo-light",  "Main logo",                "/images/logo-black.png",  "Login screen and the chat page (shown small at the top of a conversation)"),
        new("logo-header", "Header logo",              "/images/logo-header.png", "Control panel header; uses the main logo until you upload one"),
        new("logo-dark",   "Logo for the dark sidebar", "/images/logo-white.png",  "Sidebar in the chat and in the control panel; uses the main logo, turned white, until you upload one"),
        new("code-logo",  "Code mode label",            "/images/BlackCode.png",  "The \"</Code>\" word shown next to the logo at the top of a Code mode conversation"),
    ];

    public const long MaxBytes = 2 * 1024 * 1024;

    private static readonly string Dir = Path.Combine(Paths.PersistentData, "Branding");
    private static readonly object Lock = new();

    private static readonly Dictionary<string, string> ContentTypes = new()
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".webp"] = "image/webp", [".gif"] = "image/gif", [".svg"] = "image/svg+xml",
    };

    public static Slot? Find(string key) => Slots.FirstOrDefault(s => s.Key == key);

    public static Slot? FindByPath(string path) =>
        Slots.FirstOrDefault(s => string.Equals(s.DefaultPath, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>The uploaded file for a slot, or null when the built-in image is in use.</summary>
    public static (string Path, string ContentType)? Custom(string key)
    {
        lock (Lock)
        {
            if (!Directory.Exists(Dir)) return null;
            foreach ((string ext, string type) in ContentTypes)
            {
                string file = System.IO.Path.Combine(Dir, key + ext);
                if (File.Exists(file)) return (file, type);
            }
            return null;
        }
    }

    /// <summary>Returns an error message, or null when saved. The type comes from the file's contents, not its name.</summary>
    public static string? Save(string key, Stream data)
    {
        using MemoryStream buffer = new();
        data.CopyTo(buffer);
        if (buffer.Length == 0) return "The file is empty.";
        if (buffer.Length > MaxBytes) return "The file is larger than 2 MB.";
        byte[] b = buffer.ToArray();
        string? ext = Detect(b);
        if (ext is null) return "Use a PNG, JPEG, WebP, GIF or SVG image.";
        lock (Lock)
        {
            Directory.CreateDirectory(Dir);
            Clear(key);
            File.WriteAllBytes(System.IO.Path.Combine(Dir, key + ext), b);
        }
        return null;
    }

    public static void Reset(string key) { lock (Lock) Clear(key); }

    /// <summary>What a slot actually shows: its own upload, or (for the sidebar and header logos) the
    /// light-background upload when that is the only one — so a single uploaded logo rebrands the sidebar too.
    /// The sidebar copy is turned white by the UI (Derived), since it sits on a dark background.</summary>
    public static (string Path, string ContentType, bool Derived)? Resolve(string key)
    {
        if (Custom(key) is { } own) return (own.Path, own.ContentType, false);
        if (key is "logo-dark" or "logo-header" && Custom("logo-light") is { } light) return (light.Path, light.ContentType, true);
        return null;
    }

    public static DateTime? Version(string key) => Resolve(key) is { } r ? File.GetLastWriteTimeUtc(r.Path) : null;

    /// <summary>True when the sidebar logo is the light logo and needs turning white.</summary>
    public static bool SidebarDerived => Resolve("logo-dark") is { Derived: true };

    private static void Clear(string key)
    {
        if (!Directory.Exists(Dir)) return;
        foreach (string ext in ContentTypes.Keys)
            File.Delete(System.IO.Path.Combine(Dir, key + ext));
    }

    private static string? Detect(byte[] b)
    {
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
        if (b.Length > 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8') return ".gif";
        if (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return ".webp";
        string head = System.Text.Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 2048));
        if (head.Contains("<svg", StringComparison.OrdinalIgnoreCase)) return ".svg";
        return null;
    }

    /// <summary>Serves the uploaded file for the built-in logo URLs, ahead of the static files.</summary>
    public static async Task Middleware(HttpContext ctx, Func<Task> next)
    {
        if ((HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
            && FindByPath(ctx.Request.Path.Value ?? "") is { } slot)
        {
            if (Resolve(slot.Key) is { } custom)
            {
                ctx.Response.ContentType = custom.ContentType;
                ctx.Response.Headers.CacheControl = "no-cache";
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                // An uploaded SVG opened directly must not be able to run script on this origin.
                ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; img-src data:";
                await ctx.Response.SendFileAsync(custom.Path);
                return;
            }
            // The header logo has no image of its own by default: it is the standard logo.
            if (slot.Key == "logo-header") ctx.Request.Path = "/images/logo-black.png";
        }
        await next();
    }
}
