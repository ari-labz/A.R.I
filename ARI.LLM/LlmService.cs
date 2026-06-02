using ARI.Brain;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

public class LlmService : IDisposable
{
    private readonly Dialogue?  dialogue;
    private readonly Context?   context;
    private readonly Recall?    recall;
    private readonly Engram?    engram;
    private readonly Refactor?  refactor;
    private readonly CommandService commands;

    public LlmService(string modelsConfigPath, string? brainConfigPath = null, ILoggerFactory? loggerFactory = null)
    {
        if (loggerFactory is not null)
            Common.InitialiseLogger(loggerFactory);

        AriModelsConfig config = AriModelsConfig.LoadFrom(modelsConfigPath);

        Dictionary<string, ModelConfig> enabled = config.Models
            .Where(m => m.Enabled)
            .ToDictionary(m => m.Name);

        if (enabled.TryGetValue("Dialogue", out ModelConfig? dialogueConfig))
            dialogue = new Dialogue(dialogueConfig);

        if (enabled.TryGetValue("Context", out ModelConfig? contextConfig))
        {
            int memoryLimit = enabled.TryGetValue("Dialogue", out ModelConfig? dlgCfg) ? dlgCfg.ShortTermMemoryLimit : 25;
            context = new Context(contextConfig, memoryLimit);
            Common.Logger.LogInformation("Context tracker is active.");
        }

        if (brainConfigPath is not null && dialogue is not null)
        {
            BrainService brain = new BrainService(brainConfigPath, loggerFactory);

            if (enabled.TryGetValue("Recall", out ModelConfig? recallConfig) && recallConfig.RecursiveBrainSearchDepth > 0)
            {
                recall = new Recall(recallConfig, brain, recallConfig.RecursiveBrainSearchDepth, brain.BrainPublicUrl);
                Common.Logger.LogInformation("Recall is active. Depth: {Depth}, Cache: {Cache}.",
                    recallConfig.RecursiveBrainSearchDepth, recallConfig.CacheSize > 0 ? recallConfig.CacheSize : 0);
            }

            if (enabled.TryGetValue("Engram", out ModelConfig? engramConfig))
            {
                engram = new Engram(engramConfig, dialogue, brain, context, engramConfig.SweepIntervalMinutes, engramConfig.RecursiveBrainSearchDepth);
                Common.Logger.LogInformation("Engram is active. Brain connected.");
            }

            if (enabled.TryGetValue("Refactor", out ModelConfig? refactorConfig))
            {
                refactor = new Refactor(refactorConfig, brain);
                Common.Logger.LogInformation("Refactor is active.");
            }

            commands = new CommandService(engram, refactor, brain.PurgeAllNotes, brain.BackupAsync);
        }
        else
        {
            commands = new CommandService(engram);
        }
    }

    public IReadOnlyCollection<string> GetActiveThreadKeys()
        => dialogue?.ThreadKeys ?? Array.Empty<string>();

    public IReadOnlyList<ChatMessage> GetThreadHistory(string threadKey)
        => dialogue?.GetThreadHistory(threadKey) ?? Array.Empty<ChatMessage>();

    public IReadOnlyList<ChatMessage> GetThreadDisplayHistory(string threadKey)
        => dialogue?.GetThreadDisplayHistory(threadKey) ?? Array.Empty<ChatMessage>();

    public DateTime GetThreadLastMessageAt(string threadKey)
        => dialogue?.GetThreadLastMessageAt(threadKey) ?? DateTime.MinValue;

    // Returns metadata for all internal model threads (Engram, Recall, Context).
    // Context threads that share a key with a Dialogue thread are excluded to avoid duplication.
    public IReadOnlyList<InternalThreadInfo> GetInternalThreads()
    {
        var result = new List<InternalThreadInfo>();
        HashSet<string> dialogueKeys = new(dialogue?.ThreadKeys ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        void Add(Model? model, string modelName)
        {
            if (model is null) return;
            foreach (string key in model.ThreadKeys)
            {
                if (dialogueKeys.Contains(key)) continue;
                result.Add(new InternalThreadInfo(key, modelName,
                    model.GetThreadLastMessageAt(key),
                    model.GetThreadHistory(key).Count));
            }
        }

        Add(engram,    "Engram");
        Add(refactor,  "Refactor");
        Add(recall,    "Recall");
        Add(context,   "Context");
        return result;
    }

    // Returns the raw message history (including system messages) for an internal thread.
    public IReadOnlyList<ChatMessage> GetInternalThreadHistory(string threadKey)
    {
        if (engram?.ThreadKeys.Contains(threadKey)    == true) return engram.GetThreadHistory(threadKey);
        if (refactor?.ThreadKeys.Contains(threadKey)  == true) return refactor.GetThreadHistory(threadKey);
        if (recall?.ThreadKeys.Contains(threadKey)    == true) return recall.GetThreadHistory(threadKey);
        if (context?.ThreadKeys.Contains(threadKey)   == true) return context.GetThreadHistory(threadKey);
        return Array.Empty<ChatMessage>();
    }

    public record InternalThreadInfo(string Key, string ModelName, DateTime LastMessageAt, int MessageCount);

    public async Task<string> Prompt(string threadKey, string prompt, string? contextNote = null)
    {
        if (dialogue is null)
            throw new ModelNotFoundException("Dialogue model is not loaded or is not enabled.");

        IReadOnlyList<ChatMessage> history = dialogue.GetThreadHistory(threadKey);
        string? contextSummary = context?.GetContext(threadKey);

        string? recallBlock = null;
        if (recall is not null)
            recallBlock = await recall.FetchContextAsync(history, prompt);

        return await dialogue.SendPrompt(threadKey, prompt, contextNote, recallBlock, contextSummary);
    }

    /// <summary>
    /// Passes a slash command to the CommandService for processing.
    /// Returns a human-readable result, or null if the input is not a recognised command.
    /// </summary>
    public Task<string?> HandleCommandAsync(string input) => commands.HandleAsync(input);

    public void Dispose() => engram?.Dispose();
}
