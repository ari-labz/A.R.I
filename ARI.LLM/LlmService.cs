namespace ARI.LLM;

public class LlmService
{
    private readonly Dictionary<string, ModelClient> loadedModels;

    public LlmService(string modelsConfigPath)
    {
        AriModelsConfig config = AriModelsConfig.LoadFrom(modelsConfigPath);

        loadedModels = new Dictionary<string, ModelClient>();

        foreach (ModelConfig modelConfig in config.Models.Where(m => m.Enabled))
            loadedModels[modelConfig.Name] = new ModelClient(modelConfig.Endpoint, modelConfig.Model, modelConfig.SystemPrompt);
    }

    public Task<string> PromptModel(string modelName, string prompt, string? context = null)
    {
        if (!loadedModels.TryGetValue(modelName, out ModelClient? client))
            throw new ModelNotFoundException($"Model '{modelName}' is not loaded or is not enabled.");

        return client.SendPrompt(prompt, context);
    }
}
