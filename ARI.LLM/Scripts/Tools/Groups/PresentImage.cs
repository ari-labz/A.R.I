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
    private readonly Thread boundThread;
    private string? shownFilename;
    private string? shownThreadKey;

    internal PresentImage(Thread thread) => boundThread = thread;

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

    private const long DiscordMaxFileBytes = 10 * 1024 * 1024;

    internal override async Task<ToolResult> Execute(string args)
    {
        using JsonDocument doc  = JsonDocument.Parse(string.IsNullOrWhiteSpace(args) ? "{}" : args);
        JsonElement        root = doc.RootElement;
        string filename = root.TryGetProperty("filename", out JsonElement fnEl) ? fnEl.GetString() ?? "" : "";
        filename = Path.GetFileName(filename.Trim());

        if (string.IsNullOrWhiteSpace(filename))
            return "A filename is required.";

        string dir  = Paths.ScratchpadDir(boundThread.Key);
        string path = Path.Combine(dir, filename);
        if (!File.Exists(path))
            return $"No file named '{filename}' found in the scratchpad.";

        // On Discord there's no app to raise an image event to — send the image as a message attachment instead.
        if (boundThread.Medium == ThreadMedium.Discord)
        {
            if (Modules.Discord is null || boundThread.DiscordChannelId == 0)
                return "Couldn't send the image — this Discord conversation has no channel to send it to.";
            if (new FileInfo(path).Length > DiscordMaxFileBytes)
                return $"'{filename}' is too large to send on Discord (over 10 MB). Tell the user, or generate a smaller image.";

            string? failure = await Modules.Discord.SendFileAsync(boundThread.DiscordChannelId, path);
            return failure is null
                ? $"'{filename}' has been sent to the user as a Discord attachment."
                : $"Couldn't send the image on Discord: {failure}";
        }

        shownFilename  = filename;
        shownThreadKey = boundThread.Key;

        string url = $"/threads/{Uri.EscapeDataString(boundThread.Key)}/scratchpad/{Uri.EscapeDataString(filename)}";
        boundThread.RaiseScratchpadFileReady(url);

        return $"'{filename}' has been shown to the user.";
    }

    internal override Func<string, string>? DisplayAfter => _ =>
        shownFilename is not null && shownThreadKey is not null
            ? $"\n<!--ari-image:{shownThreadKey}:{shownFilename}-->"
            : "";
}
