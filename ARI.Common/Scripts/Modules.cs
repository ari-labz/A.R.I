namespace ARI.Common;

public interface IDiscordModule
{
    Task NotifyOwner(string message);
    Task NotifyOffline();
}

public interface ILLMModule { }
public interface IVoiceModule { }
public interface IBrainModule { }

public static class Modules
{
    public static IDiscordModule? Discord { get; private set; }
    public static ILLMModule?     Llm     { get; private set; }
    public static IVoiceModule?   Voice   { get; private set; }
    public static IBrainModule?   Brain   { get; private set; }

    public static void Register(
        IDiscordModule? discord = null,
        ILLMModule?     llm     = null,
        IVoiceModule?   voice   = null,
        IBrainModule?   brain   = null)
    {
        if (discord is not null) Discord = discord;
        if (llm     is not null) Llm     = llm;
        if (voice   is not null) Voice   = voice;
        if (brain   is not null) Brain   = brain;
    }
}
