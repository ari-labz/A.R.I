namespace ARI.LLM;

public class LlmService
{
    private readonly Dictionary<string, Model> models;
    private readonly List<string> ollamaModelnames;

    public LlmService(string modelsConfigPath)
    {
        AriModelsConfig config = AriModelsConfig.LoadFrom(modelsConfigPath);

        models = new Dictionary<string, Model>();
        ollamaModelnames = new List<string>();

        foreach (ModelConfig modelConfig in config.Models.Where(m => m.Enabled))
        {
            models[modelConfig.Name] = new Model(modelConfig);
            ollamaModelnames.Add(modelConfig.Model);
        }
    }
    
    public IReadOnlyCollection<string> OllamaModelNames => ollamaModelnames.AsReadOnly();


    public Task<string> Prompt(string threadKey, string prompt, string? contextNote = null)
    {
        return PromptModel("Dialogue", threadKey, prompt, contextNote);
    }

    private Task<string> PromptModel(string modelName, string threadKey, string prompt, string? contextNote = null)
    {
        if (!models.TryGetValue(modelName, out Model? model))
            throw new ModelNotFoundException($"Model '{modelName}' is not loaded or is not enabled.");

        return model.SendPrompt(threadKey, prompt, contextNote);
    }
}
