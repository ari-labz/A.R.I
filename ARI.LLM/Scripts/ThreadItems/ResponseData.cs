using System.Text.Json.Serialization;

namespace ARI.LLM;

/// <summary>Telemetry and debug-dump fields for a <see cref="Response"/>, grouped into one object so they
/// stay together and off the main response surface. Token stats feed budget logging and the control panel;
/// the Debug* dumps feed the Deep Thread Inspection (DTI) view. None of this re-enters the model context.</summary>
public sealed class ResponseData
{
    public int CompletionTokens          { get; set; }
    public int OutputTokenLimit          { get; set; }
    public int PromptTokens              { get; set; }
    public int ContextTokenLimit         { get; set; }
    public bool HadImageAttachments      { get; set; }
    public int EstimatedTextPromptTokens { get; set; }
    public int ImageTokenLimit           { get; set; }

    /// <summary>The full JSON body sent to /v1/chat/completions for the final turn. DTI only — never on the normal wire.</summary>
    [JsonIgnore]
    public string? DebugRequestJson  { get; set; }
    /// <summary>The assembled model response text for the final turn. DTI only — never on the normal wire.</summary>
    [JsonIgnore]
    public string? DebugResponseText { get; set; }
}
