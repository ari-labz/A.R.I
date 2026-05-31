using ARI.Brain;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

public class LlmService : IDisposable
{
    private readonly Dialogue? dialogue;
    private readonly Context? context;
    private readonly Recall? recall;
    private readonly Engram? engram;

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
                recall = new Recall(recallConfig, brain, recallConfig.RecursiveBrainSearchDepth, recallConfig.CacheSize);
                Common.Logger.LogInformation("Recall is active. Depth: {Depth}, Cache: {Cache}.",
                    recallConfig.RecursiveBrainSearchDepth, recallConfig.CacheSize > 0 ? recallConfig.CacheSize : 0);
            }

            if (enabled.TryGetValue("Engram", out ModelConfig? engramConfig))
            {
                engram = new Engram(engramConfig, dialogue, brain, context, engramConfig.SweepIntervalMinutes, engramConfig.RecursiveBrainSearchDepth);
                Common.Logger.LogInformation("Engram is active. Brain connected.");
            }
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

    public Task<int> PurgeNotes() => engram?.PurgeNotes() ?? Task.FromResult(0);

    public void Dispose() => engram?.Dispose();
}
