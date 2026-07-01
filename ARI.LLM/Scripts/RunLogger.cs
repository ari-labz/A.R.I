using ARI.Common;
using Microsoft.Extensions.Logging;
using System.Text;

namespace ARI.LLM;

/// <summary>
/// Writes a complete, human-readable transcript of a single Engram or Memory run to Logs.
/// One markdown file per run capturing everything the model saw and every reasoning step, tool
/// call, tool result and decision it made — the same data the Debug Thread view renders.
///
/// Purpose: accumulate hundreds of runs over time so they can be handed to Claude for analysis
/// of how to improve these agents. Best-effort — any failure here is swallowed so logging can
/// never disrupt a sweep or a recall.
/// </summary>
internal static class RunLogger
{
    private static readonly object WriteGate = new();

    /// <summary>
    /// Writes one log file for a run. <paramref name="threads"/> is the ordered set of internal
    /// threads the run used (each is rendered turn-by-turn); <paramref name="meta"/> is a list of
    /// summary facts about the non-LLM work (inputs, scoring, fetches, outcome).
    /// </summary>
    internal static void Write(
        string agent,
        string runLabel,
        IEnumerable<(string Title, Thread Thread)> threads,
        IEnumerable<string>? meta = null)
    {
        try
        {
            string dir = LogPaths.Dir("Logs");

            DateTime now  = DateTime.Now;
            string   file = Path.Combine(dir, $"{now:yyyyMMdd-HHmmss-fff}_{agent}_{Sanitize(runLabel)}.md");

            StringBuilder sb = new();
            sb.AppendLine($"# {agent} run — {runLabel}");
            sb.AppendLine($"_{now:yyyy-MM-dd HH:mm:ss}_");
            sb.AppendLine();

            if (meta is not null)
            {
                List<string> lines = meta.ToList();
                if (lines.Count > 0)
                {
                    sb.AppendLine("## Run summary");
                    foreach (string line in lines) sb.AppendLine($"- {line}");
                    sb.AppendLine();
                }
            }

            foreach ((string title, Thread thread) in threads)
                RenderThread(sb, title, thread);

            lock (WriteGate)
                File.WriteAllText(file, sb.ToString());
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[RunLogger] failed to write log for {Agent}: {Error}", agent, ex.Message);
        }
    }

    private static void RenderThread(StringBuilder sb, string title, Thread thread)
    {
        sb.AppendLine($"## {title}");
        sb.AppendLine($"`{thread.Key}`");
        sb.AppendLine();

        // The final accumulated request holds the full context the model saw on its last turn
        // (system prompt + every prior message); dumping just that one avoids repeating the
        // growing payload on every turn while still recording exactly what reached the model.
        int lastResponse = -1;
        for (int i = 0; i < thread.History.Count; i++)
            if (thread.History[i] is Response) lastResponse = i;

        int turn = 0;
        for (int i = 0; i < thread.History.Count; i++)
        {
            switch (thread.History[i])
            {
                case Prompt u:
                    sb.AppendLine($"**{u.AuthorName}:** {u.Text}");
                    sb.AppendLine();
                    break;
                case Response r:
                    RenderResponse(sb, ++turn, r, dumpRequest: i == lastResponse);
                    break;
            }
        }
    }

    private static void RenderResponse(StringBuilder sb, int turn, Response r, bool dumpRequest)
    {
        sb.AppendLine($"### Turn {turn} — {r.ThinkingSeconds:F1}s · {r.Data.PromptTokens} prompt / {r.Data.CompletionTokens} completion tokens");
        sb.AppendLine();

        if (r.Trace is { Count: > 0 })
        {
            foreach (TraceStep step in r.Trace) RenderStep(sb, step);
        }
        else
        {
            // No structured trace — fall back to raw reasoning + output.
            if (!string.IsNullOrWhiteSpace(r.Reasoning))
            {
                sb.AppendLine("**Reasoning (chain of thought):**");
                sb.AppendLine();
                sb.AppendLine(Quote(r.Reasoning));
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(r.ContentText))
            {
                sb.AppendLine("**Output:**");
                sb.AppendLine();
                sb.AppendLine(r.ContentText);
                sb.AppendLine();
            }
        }

        if (dumpRequest && !string.IsNullOrWhiteSpace(r.Data.DebugRequestJson))
        {
            sb.AppendLine("<details><summary>Full request the model received (system prompt + all messages)</summary>");
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine(r.Data.DebugRequestJson!.Trim());
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }
    }

    private static void RenderStep(StringBuilder sb, TraceStep step)
    {
        switch (step.Kind)
        {
            case "prompt":
                sb.AppendLine("**Prompt the model received:**");
                sb.AppendLine();
                sb.AppendLine(Quote(step.Text));
                sb.AppendLine();
                break;
            case "reasoning":
                sb.AppendLine("**Reasoning (chain of thought):**");
                sb.AppendLine();
                sb.AppendLine(Quote(step.Text));
                sb.AppendLine();
                break;
            case "tool_call":
                sb.AppendLine($"**→ Tool call:** `{step.Name}`");
                if (!string.IsNullOrWhiteSpace(step.Args))
                {
                    sb.AppendLine("```json");
                    sb.AppendLine(step.Args!.Trim());
                    sb.AppendLine("```");
                }
                sb.AppendLine();
                break;
            case "tool_result":
                sb.AppendLine($"**← Tool result** `{step.Name}`**:**");
                sb.AppendLine();
                sb.AppendLine(Quote(step.Text));
                sb.AppendLine();
                break;
            case "text":
                sb.AppendLine("**Output:**");
                sb.AppendLine();
                sb.AppendLine(step.Text);
                sb.AppendLine();
                break;
        }
    }

    /// <summary>Prefixes every line of a block with a markdown quote marker.</summary>
    private static string Quote(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "> _(empty)_";
        return string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(l => $"> {l}"));
    }

    /// <summary>Truncates long single-line values for the run summary header.</summary>
    internal static string Trunc(string? text, int max = 300)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static string Sanitize(string name)
    {
        StringBuilder sb = new(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c is ':' or ' ' ? '_' : c);
        string s = sb.ToString();
        return s.Length <= 60 ? s : s[..60];
    }
}
