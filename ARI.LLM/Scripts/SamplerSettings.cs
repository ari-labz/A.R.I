namespace ARI.LLM;

/// <summary>Per-turn sampler override returned by <see cref="Agent.ResolveSampler"/>, or set statically
/// via <see cref="Agent.SamplerSettings"/>. Null members fall through to the bound server baseline.</summary>
public sealed class SamplerSettings
{
    public double?   Temperature         { get; init; }
    public double?   TopP                { get; init; }
    public int?      TopK                { get; init; }
    public double?   MinP                { get; init; }
    public double?   RepeatPenalty       { get; init; }
    public double?   PresencePenalty     { get; init; }
    public double?   FrequencyPenalty    { get; init; }
    public double?   TopNSigma           { get; init; }
    public double?   TypicalP            { get; init; }
    public double?   XtcProbability      { get; init; }
    public double?   XtcThreshold        { get; init; }
    public double?   DynatempRange       { get; init; }
    public double?   DynatempExp         { get; init; }
    public int?      RepeatLastN         { get; init; }
    public double?   DryMultiplier       { get; init; }
    public double?   DryBase             { get; init; }
    public int?      DryAllowedLength    { get; init; }
    public int?      DryPenaltyLastN     { get; init; }
    public string[]? DrySequenceBreakers { get; init; }
    public int?      Mirostat            { get; init; }
    public double?   MirostatLr          { get; init; }
    public double?   MirostatEnt         { get; init; }
    public long?     Seed                { get; init; }
}
