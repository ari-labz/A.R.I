namespace ARI.Common;

public interface IDiscordModule
{
    Task NotifyOwner(string message);
    Task NotifyOffline();
}

public interface ILLMModule
{
    Task StopAllServersAsync();
    Task RestartAllServersAsync();
    bool AssignAgentServer(string agentName, string serverName);
    bool AssignAgentSlot(string agentName, int? slot);
}

public interface IVoiceModule
{
    bool    IsReady     { get; }
    string? ActiveModel { get; }
    Task<byte[]> Synthesise(string text, CancellationToken ct);
}

public interface IVoiceSynthesisModule
{
    bool IsSetupComplete { get; }
}

public interface IBrainModule { }

public static class Modules
{
    public static IDiscordModule?        Discord        { get; private set; }
    public static ILLMModule?            Llm            { get; private set; }
    public static IVoiceModule?          Voice          { get; private set; }
    public static IVoiceSynthesisModule? VoiceSynthesis { get; private set; }
    public static IBrainModule?          Brain          { get; private set; }

    public static void Register(
        IDiscordModule?        discord        = null,
        ILLMModule?            llm            = null,
        IVoiceModule?          voice          = null,
        IVoiceSynthesisModule? voiceSynthesis = null,
        IBrainModule?          brain          = null)
    {
        if (discord        is not null) Discord        = discord;
        if (llm            is not null) Llm            = llm;
        if (voice          is not null) Voice          = voice;
        if (voiceSynthesis is not null) VoiceSynthesis = voiceSynthesis;
        if (brain          is not null) Brain          = brain;
    }
}
