using System.Text;

namespace ARI.LLM;

// How Ari writes tools for herself. A tool is a C# script saved into the custom tools folder; it compiles at once and she
// can try it straight away. Owner's app chats only: a script runs with full access to this machine.
[Tool("tool_docs", "How to write a tool of your own: the format, the lifecycle methods, and a worked example. Read it before your first write_tool.", Access = ToolAccess.Read)]
public class ToolDocs : Tool
{
    public override string OnValidate() => IsAdmin ? null : "Only the owner, chatting in the app, can write tools.";

    public override Task<string> Run() => Task.FromResult("""
        A tool is one C# file: one class marked [Tool] that inherits Tool. Save it with write_tool, then try it with test_tool.

        [Tool("weather", "Get the current weather for a city. Use it when asked about weather.")]
        public class Weather : Tool
        {
            [Param("City name, e.g. Paris")]
            public string City;

            [Param("celsius or fahrenheit", Options = "celsius,fahrenheit", Optional = true)]
            public string Units = "celsius";

            public override async Task<string> Run()
            {
                string text = await Http.GetStringAsync($"https://wttr.in/{City}?format=3", Cancelled);
                return text;
            }
        }

        Rules:
        - The [Tool] name is what you call it: lowercase letters, digits and underscores. The description is what you read later, so say when to use it.
        - Each [Param] field is something you fill in when you call it. Everything you send is text; ARI converts it to the field's type (int, double, bool, enum, List<string>...). A parameter is required unless it has Optional = true or a nullable type.
        - Run() returns a string: that is what you read back. Throw or return a clear message when something goes wrong.
        - Optional methods to override: OnValidate() (return a message to refuse the call), OnStart(), OnComplete(result), OnError(exception), OnCancel().
        - Helpers inside the class: Http (an HttpClient), Cancelled (a token), IsAdmin, ConversationId, DataFolder (a folder just for this tool), WorkingFolder, Log("text").
        - A fresh object is made for every call, so use static fields for anything that must persist.
        - [Tool(..., Async = true)] makes it run in the background: you carry on and are handed the result when it finishes.
        - One tool per file. Helper classes and enums in the same file are fine. System, Linq, IO, Net.Http, Text, Text.Json, Regex and Tasks need no using lines.
        - If it doesn't compile, write_tool tells you the line and the error. Fix it and save again; the old version keeps running meanwhile.
        - After it compiles, call it with test_tool before you tell the user it works. It is available to you straight away through request_tools or tool_search.
        """);
}

[Tool("write_tool", "Write a tool of your own, or replace one, and compile it. Read tool_docs first. Returns the compile errors with line numbers if it doesn't build.")]
public class WriteTool : Tool
{
    [Param("The file name, ending in .cs, e.g. Weather.cs. Letters, numbers, - and _ only.")]
    public string File;

    [Param("The whole C# source of the tool.")]
    public string Code;

    public override string OnValidate() => IsAdmin ? null : "Only the owner, chatting in the app, can write tools.";

    public override Task<string> Run()
    {
        CustomToolInfo info = CustomTools.Save(File.Trim(), Code);
        StringBuilder sb = new();
        foreach (ToolDiagnostic d in info.Diagnostics) sb.AppendLine($"{d.Severity} {d.Id} at line {d.Line}, column {d.Column}: {d.Message}");
        bool errors = info.Diagnostics.Any(d => d.Severity == "Error");
        if (!errors && info.Compiled) sb.Insert(0, $"Compiled. The tool '{info.Name}' is live. Try it with test_tool, or load it with request_tools.\n");
        else sb.Insert(0, $"It didn't compile.{(info.Compiled ? " The previous version is still running." : "")}\n");
        return Task.FromResult(sb.ToString().TrimEnd());
    }
}

[Tool("test_tool", "Call one of your own custom tools once with the arguments you give and see exactly what it returns, as you would when using it.")]
public class TestTool : Tool
{
    [Param("The tool's name, as in its [Tool(\"name\", ...)] line.")]
    public string Name;

    [Param("The arguments as JSON, e.g. {\"city\": \"Paris\"}. Use {} for none.", Optional = true)]
    public string Args;

    public override string OnValidate() => IsAdmin ? null : "Only the owner, chatting in the app, can test tools.";

    public override async Task<string> Run() => await CustomTools.TestCall(Name.Trim(), string.IsNullOrWhiteSpace(Args) ? "{}" : Args, Cancelled);
}

[Tool("read_tool", "Read the source of one of your custom tools, to change it. Name the file, as list shows it. Use list_my_tools to see them.", Access = ToolAccess.Read)]
public class ReadTool : Tool
{
    [Param("The file name, e.g. Weather.cs")]
    public string File;

    public override string OnValidate() => IsAdmin ? null : "Only the owner, chatting in the app, can read tool source.";

    public override Task<string> Run() => Task.FromResult(CustomTools.ReadCode(File.Trim()) ?? $"[Error: no custom tool file called {File}. Use list_my_tools.]");
}

[Tool("list_my_tools", "List the custom tools that have been written (by you or the owner): which file, what name, and whether each compiles.", Access = ToolAccess.Read)]
public class ListMyTools : Tool
{
    public override string OnValidate() => IsAdmin ? null : "Only the owner, chatting in the app, can see custom tools.";

    public override Task<string> Run()
    {
        List<CustomToolInfo> all = CustomTools.List();
        if (all.Count == 0) return Task.FromResult("No custom tools yet. Read tool_docs, then write one with write_tool.");
        return Task.FromResult(string.Join("\n", all.Select(t => t.Compiled
            ? $"- {t.File}: {t.Name} — {t.Description}{(t.Enabled ? "" : " (switched off)")}"
            : $"- {t.File}: does not compile ({t.Diagnostics.FirstOrDefault(d => d.Severity == "Error")?.Message})")));
    }
}

[Tool("delete_tool", "Delete one of your custom tools. If it replaced a built-in, the built-in comes back.")]
public class DeleteTool : Tool
{
    [Param("The file name, e.g. Weather.cs")]
    public string File;

    public override string OnValidate() => IsAdmin ? null : "Only the owner, chatting in the app, can delete tools.";

    public override Task<string> Run() => Task.FromResult(CustomTools.Delete(File.Trim()) ? $"Deleted {File}." : $"[Error: no custom tool file called {File}.]");
}
