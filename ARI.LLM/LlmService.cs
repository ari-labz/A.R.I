namespace ARI.LLM;

public class LlmService
{
    private readonly Dictionary<string, ModelClient> loadedModels;
    private readonly List<string> ollamaModelStrings;

    public LlmService(string modelsConfigPath)
    {
        AriModelsConfig config = AriModelsConfig.LoadFrom(modelsConfigPath);

        loadedModels = new Dictionary<string, ModelClient>();
        ollamaModelStrings = new List<string>();

        foreach (ModelConfig modelConfig in config.Models.Where(m => m.Enabled))
        {
            loadedModels[modelConfig.Name] = new ModelClient(modelConfig.Endpoint, modelConfig.Model, modelConfig.SystemPrompt, modelConfig.HistoryLimit);
            ollamaModelStrings.Add(modelConfig.Model);
        }
    }

    // The Ollama model strings (e.g. "qwen2.5:14b"), not the logical names (e.g. "Dialogue")
    public IReadOnlyCollection<string> OllamaModelStrings => ollamaModelStrings.AsReadOnly();

    public Task<string> PromptModel(string modelName, string prompt)
    {
        if (!loadedModels.TryGetValue(modelName, out ModelClient? client))
            throw new ModelNotFoundException($"Model '{modelName}' is not loaded or is not enabled.");

        return client.SendPrompt(prompt);
    }
}
