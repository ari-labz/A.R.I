using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ARI.LLM;

internal partial class Coder : Agent
{
    [JsonPropertyName("shortTermMemoryLimit")] public int? ShortTermMemoryLimit { get; init; }

    internal override int  MemoryLimit      => ShortTermMemoryLimit ?? 0;
    internal override bool SuppressPromptLog => true;

    public Coder() { }

    // ── Per-thread code context ──────────────────────────────────────────────

    private sealed class CodeThreadState
    {
        public string? CodingConventions { get; set; }
        public string? ProjectRules      { get; set; }
        public string? ProjectMap        { get; set; }
    }

    private readonly ConcurrentDictionary<string, CodeThreadState> threadStates = new();

    private CodeThreadState GetOrCreateState(string threadKey)
        => threadStates.GetOrAdd(threadKey, _ => new CodeThreadState());

    internal void SetThreadContext(string threadKey, string? map, string? conventions, string? rules)
    {
        CodeThreadState s = GetOrCreateState(threadKey);
        s.ProjectMap        = map;
        s.CodingConventions = conventions;
        s.ProjectRules      = rules;
    }

    // ── Context building ─────────────────────────────────────────────────────

    // Always-on working principles for the code pipeline. Lives in the static (cached) system context so it
    // costs nothing per step. WHY: the model's biggest failure mode is context bloat — slurping whole files
    // and re-reading — which makes long tasks crawl and can stall the stream. Steer it toward minimal,
    // targeted context use without ever banning a read it genuinely needs.
    private const string WorkingPrinciples =
        "## Working efficiently\n" +
        "Keep your context lean and your actions decisive — this is the main driver of both speed AND answer quality.\n" +
        "- **Think in proportion to the task.** A one-line change needs one sentence of planning; even a multi-file " +
        "change needs a short plan, not paragraphs. Decide your approach ONCE, then carry it out. Do not re-derive the " +
        "plan, re-justify a decision you already made, or mentally re-walk code you have already read — trust your " +
        "earlier reasoning and the tool results. Over-thinking is the single biggest thing that makes you slow.\n" +
        "- To find code, use search_files (regex, returns file:line) or preview_file (outline) FIRST. " +
        "Then read_file ONLY the line range you need (start_line/end_line). Read a whole file only when it is small or you truly need all of it.\n" +
        "- **Read each file once.** Once a file (or range) is in your context it does NOT change until you edit it — " +
        "re-reading returns nothing new and only wastes time. Never \"re-read from the beginning to get clean content\": " +
        "you already have it. To edit a spot you have seen, just edit it; don't re-read first.\n" +
        "- Edit only the lines that must change. Batch all edits to one file into a single edit_file 'edits' array. " +
        "**After an edit, edit_file shows you the changed lines — that IS your confirmation it landed. Do NOT re-read the file to look at your own edit.**\n" +
        "- **Verify by building ONCE at the end** (run the build), not by re-reading or re-searching to eyeball the code. " +
        "The compiler catches what the eye misses; re-reading to \"double-check\" is wasted effort.\n" +
        "- If an edit produces broken code (brace mismatch, duplicate definition, corrupted string) and you cannot " +
        "fix it cleanly with one small follow-up edit, use revert_file to restore the file to its pre-edit state, " +
        "then re-read and plan the edit again. Reverting and starting over is always better than incrementally " +
        "patching a corrupt file — iterative repair of a bad edit is how small mistakes become total rewrites.\n" +
        "- Be concise: spend the minimum tokens needed to finish. Don't restate your plan every step or add filler. " +
        "Use as much room as a large task genuinely needs, but no more.";

    private string BuildStaticContext(CodeThreadState s)
    {
        StringBuilder sb = new();
        void Block(string title, string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            sb.Append("\n\n").Append(title).Append('\n').Append(body.Trim());
        }
        sb.Append("\n\n").Append(WorkingPrinciples);
        Block("## Coding conventions", s.CodingConventions);
        Block("## Project rules",      s.ProjectRules);
        Block("## Project map",        s.ProjectMap);
        return sb.ToString();
    }

    // ── Virtual hook overrides ───────────────────────────────────────────────

    internal override string BuildPersistentContext(Thread thread) => BuildStaticContext(GetOrCreateState(thread.Key));

}
