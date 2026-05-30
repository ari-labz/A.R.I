using ARI.Brain;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

public class LlmService : IDisposable
{
    private readonly Dialogue? dialogue;
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

        Context? context = null;
        if (enabled.TryGetValue("Context", out ModelConfig? contextConfig))
        {
            context = new Context(contextConfig);
            Common.Logger.LogInformation("Context tracker is active.");
        }

        if (brainConfigPath is not null &&
            enabled.TryGetValue("Engram", out ModelConfig? engramConfig) &&
            dialogue is not null)
        {
            BrainService brain = new BrainService(brainConfigPath, loggerFactory);
            engram = new Engram(engramConfig, dialogue, brain, context, config.EngramSweepIntervalMinutes, config.EngramFetchDepth);
            Common.Logger.LogInformation("Engram is active. Brain connected.");
        }
    }

    public Task<string> Prompt(string threadKey, string prompt, string? contextNote = null)
    {
        if (dialogue is null)
            throw new ModelNotFoundException("Dialogue model is not loaded or is not enabled.");
        return dialogue.SendPrompt(threadKey, prompt, contextNote);
    }

    public Task<int> PurgeNotes() => engram?.PurgeNotes() ?? Task.FromResult(0);

    public void Dispose() => engram?.Dispose();
}
