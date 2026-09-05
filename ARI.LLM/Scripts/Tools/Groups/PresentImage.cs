using ARI.Common;
using System.Text.Json;
using System;

namespace ARI.LLM;

/// <summary>Reveals an image already sitting in the thread's scratchpad to the user — typically one
/// generate_image just saved and the model has looked at via the vision path. Nothing reaches the user
/// until this runs. Lets the model look before it leaps — retry with a new prompt instead of showing a
/// bad result, or give up instead of presenting nothing.</summary>
internal sealed class PresentImage : Tool
{
    private readonly Thread _thread;
    private string? _shownFilename;
    private string? _shownThreadKey;

    internal PresentImage(Thread thread) => _thread = thread;

    internal override string     Name   => "present_image";
    internal override ToolAccess Access => ToolAccess.Write;

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "present_image",
            description = "Show an image from the scratchpad to the user — typically one generate_image just saved. " +
                          "Only call this after you've actually looked at the image and are satisfied with it.",
            parameters = new
            {
                type       = "object",
                properties = new
                {
                    filename = new
                    {
                        type        = "string",
                        description = "The scratchpad filename to show (e.g. the one generate_image just gave you)."
                    }
                },
                required = new[] { "filename" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string args)
    {
        using JsonDocument doc  = JsonDocument.Parse(string.IsNullOrWhiteSpace(args) ? "{}" : args);
        JsonElement        root = doc.RootElement;
        string filename = root.TryGetProperty("filename", out JsonElement fnEl) ? fnEl.GetString() ?? "" : "";
        filename = Path.GetFileName(filename.Trim());

        if (string.IsNullOrWhiteSpace(filename))
            return Task.FromResult<ToolResult>("A filename is required.");

        string dir  = Paths.ScratchpadDir(_thread.Key);
        string path = Path.Combine(dir, filename);
        if (!File.Exists(path))
            return Task.FromResult<ToolResult>($"No file named '{filename}' found in the scratchpad.");

        _shownFilename  = filename;
        _shownThreadKey = _thread.Key;

        string url = $"/threads/{Uri.EscapeDataString(_thread.Key)}/scratchpad/{Uri.EscapeDataString(filename)}";
        _thread.RaiseScratchpadFileReady(url);

        return Task.FromResult<ToolResult>($"'{filename}' has been shown to the user.");
    }

    internal override Func<string, string>? DisplayAfter => _ =>
        _shownFilename is not null && _shownThreadKey is not null
            ? $"\n<!--ari-image:{_shownThreadKey}:{_shownFilename}-->"
            : "";
}
