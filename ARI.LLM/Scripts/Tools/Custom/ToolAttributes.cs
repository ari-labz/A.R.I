namespace ARI.LLM;

/// <summary>Marks a class as a custom tool and says how Ari sees it. One class per script file carries this.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ToolAttribute(string name, string description) : Attribute
{
    /// <summary>What Ari calls it: lowercase letters, digits and underscores, e.g. "weather".</summary>
    public string Name { get; } = name;

    /// <summary>What it is for. This is what Ari reads when deciding whether to use it, so say when to reach for it.</summary>
    public string Description { get; } = description;

    /// <summary>The group it starts in. You can move it in the control panel afterwards.</summary>
    public string? Group { get; set; }

    /// <summary>Run in the background: Ari carries on and is handed the result when it finishes. You can change it in the control panel.</summary>
    public bool Async { get; set; }

    /// <summary>Read means it never changes anything. Most tools leave this alone.</summary>
    public ToolAccess Access { get; set; } = ToolAccess.ReadWrite;

    /// <summary>Replace the built-in tool of the same name. A forked copy of a built-in sets this; delete the file to get the original back.</summary>
    public bool Overrides { get; set; }

    /// <summary>Stop a call that takes longer than this. Ignored for async tools.</summary>
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>Marks a field or property as something Ari fills in when she calls the tool. Ari writes every
/// parameter as text; ARI converts it to the member's type (int, bool, enum, a list…) before <c>Run</c> is called.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class ParamAttribute(string description) : Attribute
{
    /// <summary>What to put here. Give an example where the format matters.</summary>
    public string Description { get; } = description;

    /// <summary>Ari may leave it out. Without this, a missing value is an error she's told about. A nullable member (int?) is optional too.</summary>
    public bool Optional { get; set; }

    /// <summary>The only values allowed, separated by commas: "celsius,fahrenheit". An enum member needs no list.</summary>
    public string? Options { get; set; }
}

/// <summary>A problem with how Ari called a tool. The message goes back to her so she can correct the call.</summary>
public sealed class ToolArgException(string message) : Exception(message);
