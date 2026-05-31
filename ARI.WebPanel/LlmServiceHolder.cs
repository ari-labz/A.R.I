using ARI.LLM;

namespace ARI.WebPanel;

public class LlmServiceHolder
{
    private LlmService? _service;

    public LlmService? Service => _service;
    public bool IsReady => _service is not null;

    public void Set(LlmService service) => _service = service;
}
