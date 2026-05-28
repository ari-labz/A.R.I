namespace ARI.LLM;

public class LlmRequestFailedException : Exception
{
    public LlmRequestFailedException(string message) : base(message) { }
}

public class ModelNotFoundException : Exception
{
    public ModelNotFoundException(string message) : base(message) { }
}
