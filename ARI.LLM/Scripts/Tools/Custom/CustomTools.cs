using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ARI.Common;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

public sealed record ToolDiagnostic(string Severity, string Id, int Line, int Column, string Message);

/// <summary>One script file: what it compiled to, or why it didn't. Name is null when it didn't compile.</summary>
public sealed record CustomToolInfo(string File, string? Name, string? Description, bool Compiled, List<ToolDiagnostic> Diagnostics,
                                    bool Enabled, bool Async, string Group);

/// <summary>
/// Custom tools: C# scripts in the CustomTools folder, each holding one class marked [Tool]. Saving or dropping a
/// file compiles it with Roslyn into its own collectible assembly and registers the tool straight away; editing
/// replaces it, deleting removes it. A script that doesn't compile is reported, never fatal. See Documentation/Server/Custom-Tools.md.
/// </summary>
public static class CustomTools
{
    private sealed class Loaded
    {
        internal required string File, Name, Description, Hash;
        internal required string? Group;
        internal required bool Async, Overrides;
        internal required int TimeoutSeconds;
        internal required ToolAccess Access;
        internal required Type Type;
        internal required object Schema;
        internal required AssemblyLoadContext Alc;
    }

    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<string, Loaded> byFile = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, List<ToolDiagnostic>> failed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex FileRx = new(@"^[A-Za-z0-9_\-]{1,60}\.cs$", RegexOptions.Compiled);
    private static readonly Regex NameRx = new(@"^[a-z][a-z0-9_]{1,40}$", RegexOptions.Compiled);
    private static FileSystemWatcher? watcher;
    private static int generation;

    // Scripts need no using lines for the everyday namespaces.
    private const string GlobalUsings =
        "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; " +
        "global using System.Net.Http; global using System.Text; global using System.Text.Json; global using System.Text.RegularExpressions; " +
        "global using System.Threading; global using System.Threading.Tasks; global using ARI.LLM;";

    // A script can use anything inside ARI itself, internal or not: a copy of a built-in tool needs the same helpers the original
    // uses, and these are the owner's own tools. This declares the attribute the runtime honours and switches the compiler's
    // accessibility check off. (The compiler setting is an internal one, reached by reflection, so a Roslyn update could break it
    // and then the only effect is that copies of built-ins that use internals stop compiling.)
    private const string AccessShim =
        "[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo(\"ARI.LLM\")] [assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo(\"ARI.Common\")] " +
        "namespace System.Runtime.CompilerServices { [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)] " +
        "internal sealed class IgnoresAccessChecksToAttribute : System.Attribute { public IgnoresAccessChecksToAttribute(string assemblyName) { } } }";

    private static CSharpCompilationOptions IgnoreAccessibility(CSharpCompilationOptions options)
    {
        try
        {
            typeof(CSharpCompilationOptions).GetProperty("TopLevelBinderFlags", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(options, (uint)1 << 22);
            // Without this the compiler doesn't even read internal members out of ARI's assemblies.
            typeof(CompilationOptions).GetProperty("MetadataImportOptions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(options, MetadataImportOptions.All);
        }
        catch (Exception e) { Shared.Logger.LogWarning(e, "[CustomTools] Couldn't switch off the accessibility check."); }
        return options;
    }

    public static string Folder => Path.Combine(Paths.PersistentData, "CustomTools");

    // ── What the rest of the server asks ─────────────────────────────────────

    internal static IEnumerable<string> Names => byFile.Values.Select(l => l.Name).ToList();
    internal static bool IsCustom(string name) => Find(name) is not null;
    private static Loaded? Find(string name) => byFile.Values.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    internal static string? DescriptionOf(string name) => Find(name)?.Description;
    internal static string? FileOf(string name) => Find(name)?.File;
    internal static bool OverridesBuiltIn(string name) => Find(name)?.Overrides == true;

    internal static Tool Host(string name, Thread thread) => new CustomToolHost(name, thread);

    // ── Start-up and file watching ───────────────────────────────────────────

    public static void Start()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Folder);
            foreach (string f in System.IO.Directory.GetFiles(Folder, "*.cs")) Compile(Path.GetFileName(f), System.IO.File.ReadAllText(f), save: false);
            ToolGroups.Rebuild();

            watcher = new FileSystemWatcher(Folder, "*.cs") { EnableRaisingEvents = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            watcher.Changed += (_, e) => Debounce(e.Name);
            watcher.Created += (_, e) => Debounce(e.Name);
            watcher.Deleted += (_, e) => Debounce(e.Name);
            watcher.Renamed += (_, e) => { Debounce(e.OldName); Debounce(e.Name); };
            Shared.Logger.LogInformation("[CustomTools] {Count} tool(s) loaded from {Dir}.", byFile.Count, Folder);
        }
        catch (Exception e) { Shared.Logger.LogError(e, "[CustomTools] Couldn't start; custom tools are unavailable."); }
    }

    private static readonly ConcurrentDictionary<string, Timer> pending = new();
    private static void Debounce(string? file)
    {
        if (file is null || !FileRx.IsMatch(file)) return;
        pending[file] = new Timer(_ =>
        {
            pending.TryRemove(file, out Timer? t); t?.Dispose();
            try
            {
                string path = Path.Combine(Folder, file);
                if (System.IO.File.Exists(path)) Compile(file, System.IO.File.ReadAllText(path), save: false);
                else Remove(file);
                ToolGroups.Rebuild();
            }
            catch (Exception e) { Shared.Logger.LogWarning(e, "[CustomTools] Couldn't reload {File}.", file); }
        }, null, 400, Timeout.Infinite);
    }

    // ── What the control panel does ──────────────────────────────────────────

    public static List<CustomToolInfo> List()
    {
        System.IO.Directory.CreateDirectory(Folder);
        return System.IO.Directory.GetFiles(Folder, "*.cs").Select(Path.GetFileName).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f => Info(f!)).ToList();
    }

    public static string? ReadCode(string file) => FileRx.IsMatch(file) && System.IO.File.Exists(Path.Combine(Folder, file)) ? System.IO.File.ReadAllText(Path.Combine(Folder, file)) : null;

    /// <summary>Writes the script, compiles it and, if it compiled, makes the tool live. Returns the result either way.</summary>
    public static CustomToolInfo Save(string file, string code)
    {
        if (!FileRx.IsMatch(file)) throw new ArgumentException("A file name uses letters, numbers, - and _, and ends in .cs.");
        System.IO.Directory.CreateDirectory(Folder);
        System.IO.File.WriteAllText(Path.Combine(Folder, file), code);
        Compile(file, code, save: true);
        ToolGroups.Rebuild();
        return Info(file);
    }

    public static bool Delete(string file)
    {
        if (!FileRx.IsMatch(file)) return false;
        string path = Path.Combine(Folder, file);
        if (!System.IO.File.Exists(path)) return false;
        System.IO.File.Delete(path);
        Remove(file);
        ToolGroups.Rebuild();
        return true;
    }

    /// <summary>Calls a custom tool by name with JSON arguments, outside any conversation. For trying one from the control panel.</summary>
    public static async Task<string> TestCall(string name, string argsJson, CancellationToken ct = default)
    {
        if (Find(name) is not { } l) return $"[Error: there is no custom tool called {name}.]";
        Tool instance;
        try { instance = (Tool)Activator.CreateInstance(l.Type)!; }
        catch (Exception e) { return $"[Error: couldn't start {name}: {e.Message}]"; }
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(l.TimeoutSeconds));
        return (await instance.RunLifecycle(argsJson, cts.Token)).Text;
    }

    /// <summary>A starting point for editing a built-in tool: a custom tool with the same name, description and
    /// parameters that replaces the built-in once saved, with the original source kept below as a comment. Built-ins use
    /// ARI's internals, which scripts can't see, so the copy starts as a stub and Run is yours to write.</summary>
    public static (string File, string Code)? Fork(string name)
    {
        if (ToolRegistry.Detail(name) is not { } d) return null;
        static string Pascal(string snake) => string.Concat(snake.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        static string Lit(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        string cls = Pascal(name), file = cls + ".cs";
        for (int i = 2; System.IO.File.Exists(Path.Combine(Folder, file)); i++) file = $"{cls}{i}.cs";

        if (RealCopy(name, file) is { } real) return real;

        StringBuilder sb = new();
        sb.AppendLine($"// A copy of the built-in \"{name}\" tool. Saving it replaces the built-in until you delete this file.");
        sb.AppendLine("// Built-in tools use ARI's internals, which a script can't see, so this starts as a stub: write Run() yourself.");
        sb.AppendLine("// The original source is at the bottom, for reference.");
        sb.AppendLine($"[Tool(\"{name}\", \"{Lit(d.Description)}\", Overrides = true)]");
        sb.AppendLine($"public class {cls} : Tool");
        sb.AppendLine("{");
        foreach (ToolParam p in d.Parameters)
        {
            sb.AppendLine($"    [Param(\"{Lit(p.Description)}\"{(p.Required ? "" : ", Optional = true")})]");
            sb.AppendLine($"    public string {Pascal(p.Name)};");
            sb.AppendLine();
        }
        sb.AppendLine("    public override async Task<string> Run()");
        sb.AppendLine("    {");
        sb.AppendLine("        return \"TODO: this copy doesn't do anything yet.\";");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        if (ToolSources.For(name) is { } src)
        {
            sb.AppendLine();
            sb.AppendLine($"/* ── original source: {src.File} ──");
            sb.AppendLine(src.Code.Replace("*/", "* /"));
            sb.AppendLine("*/");
        }
        return (file, sb.ToString());
    }

    /// <summary>A built-in written in the [Tool]/[Param] style is already a custom tool in all but name, so its own class, with
    /// Overrides = true added, is a working copy. Other built-ins take constructor arguments and can't be copied this way.</summary>
    private static (string File, string Code)? RealCopy(string name, string file)
    {
        if (ToolSources.For(name) is not { } src) return null;
        int attr = src.Code.IndexOf($"[Tool(\"{name}\"", StringComparison.Ordinal);
        if (attr < 0) return null;
        int cls = src.Code.IndexOf("class ", attr, StringComparison.Ordinal), open = cls < 0 ? -1 : src.Code.IndexOf('{', cls);
        if (open < 0) return null;
        int depth = 0, end = -1;
        for (int i = open; i < src.Code.Length; i++)
        {
            if (src.Code[i] == '{') depth++;
            else if (src.Code[i] == '}' && --depth == 0) { end = i; break; }
        }
        if (end < 0) return null;

        string attribute = src.Code[attr..cls];
        int close = attribute.LastIndexOf(")]", StringComparison.Ordinal);
        if (close < 0) return null;
        attribute = attribute[..close] + (attribute[..close].Contains("Overrides") ? "" : ", Overrides = true") + attribute[close..];

        string usings = string.Join("\n", src.Code.Split('\n').Where(l => l.StartsWith("using ") && l.TrimEnd().EndsWith(";")).Select(l => l.TrimEnd()));
        StringBuilder sb = new();
        sb.AppendLine($"// A copy of the built-in \"{name}\" tool (from {src.File}). Saving it replaces the built-in until you delete this file,");
        sb.AppendLine("// and it works exactly as the original does, so edit it and Save. A script can use ARI's own internals.");
        if (usings.Length > 0) sb.AppendLine(usings);
        sb.AppendLine();
        sb.Append(attribute).Append(src.Code[cls..(end + 1)]).AppendLine();
        return (file, sb.ToString());
    }

    public static IReadOnlyDictionary<string, string> Templates => CustomToolTemplates.All;

    private static CustomToolInfo Info(string file)
    {
        byFile.TryGetValue(file, out Loaded? l);
        failed.TryGetValue(file, out List<ToolDiagnostic>? d);
        return new CustomToolInfo(file, l?.Name, l?.Description, l is not null, d ?? new(),
            l is null || ToolRegistry.IsEnabled(l.Name), l is not null && ToolRegistry.IsAsync(l.Name),
            l is null ? "" : ToolRegistry.GroupOverride(l.Name) ?? ToolGroups.BaseGroupOf(l.Name) ?? "");
    }

    // ── Compile and load ─────────────────────────────────────────────────────

    private static void Remove(string file)
    {
        failed.TryRemove(file, out _);
        if (byFile.TryRemove(file, out Loaded? old)) { ToolRegistry.ForgetDescription(old.Name); Unload(old); }
    }

    private static void Unload(Loaded l) { try { l.Alc.Unload(); } catch { } }

    private static void Compile(string file, string code, bool save)
    {
        lock (Gate)
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
            if (!save && byFile.TryGetValue(file, out Loaded? same) && same.Hash == hash) return;   // the watcher noticing our own save

            (Loaded? loaded, List<ToolDiagnostic> diags) = Build(file, code, hash);
            if (loaded is null)
            {
                failed[file] = diags;   // keep running the last good version, if there was one
                Shared.Logger.LogWarning("[CustomTools] {File} didn't compile: {First}", file, diags.FirstOrDefault()?.Message ?? "unknown");
                return;
            }

            failed.TryRemove(file, out _);
            if (byFile.TryGetValue(file, out Loaded? old)) { ToolRegistry.ForgetDescription(old.Name); Unload(old); }
            byFile[file] = loaded;
            ToolRegistry.ForgetDescription(loaded.Name);
            ToolRegistry.SeedIfNew(loaded.Name, loaded.Group, loaded.Async);
            Shared.Logger.LogInformation("[CustomTools] {File} loaded as {Tool}.", file, loaded.Name);
        }
    }

    private static (Loaded?, List<ToolDiagnostic>) Build(string file, string code, string hash)
    {
        List<ToolDiagnostic> diags = new();
        string assemblyName = $"CustomTool_{Path.GetFileNameWithoutExtension(file)}_{Interlocked.Increment(ref generation)}";
        CSharpParseOptions parse = new(LanguageVersion.Latest);
        CSharpCompilation compilation = CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText(code, parse, file), CSharpSyntaxTree.ParseText(GlobalUsings, parse, "usings.cs"), CSharpSyntaxTree.ParseText(AccessShim, parse, "access.cs")],
            References(),
            IgnoreAccessibility(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Disable)));

        using MemoryStream ms = new();
        var result = compilation.Emit(ms);
        foreach (Diagnostic d in result.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning))
        {
            LinePosition at = d.Location.GetLineSpan().StartLinePosition;
            string where = d.Location.SourceTree?.FilePath is { } path && path != file ? $"[{path}] " : "";   // a problem outside the script itself, e.g. a missing reference
            diags.Add(new ToolDiagnostic(d.Severity.ToString(), d.Id, d.Location.SourceTree is null ? 1 : at.Line + 1, at.Character + 1, where + d.GetMessage()));
        }
        if (!result.Success)
        {
            if (!diags.Any(d => d.Severity == "Error")) diags.Add(new ToolDiagnostic("Error", "CT0001", 1, 1, "The script didn't compile."));
            return (null, diags);
        }

        AssemblyLoadContext alc = new(assemblyName, isCollectible: true);
        ms.Position = 0;
        Assembly asm = alc.LoadFromStream(ms);

        List<Type> tools = asm.GetTypes().Where(t => !t.IsAbstract && typeof(Tool).IsAssignableFrom(t) && t.GetCustomAttribute<ToolAttribute>() is not null).ToList();
        string? problem = tools.Count switch
        {
            0 => "No class is marked [Tool(\"name\", \"description\")] and inherits Tool.",
            > 1 => "A file holds one tool. Found " + string.Join(", ", tools.Select(t => t.Name)) + " all marked [Tool]; move the others to their own files, or remove [Tool] from the helpers.",
            _ => null
        };
        ToolAttribute? attr = tools.Count == 1 ? tools[0].GetCustomAttribute<ToolAttribute>() : null;
        if (problem is null && !NameRx.IsMatch(attr!.Name)) problem = $"The tool name \"{attr.Name}\" should be lowercase letters, digits and underscores, starting with a letter (like \"weather\").";
        if (problem is null && ToolFactories.IsBuiltIn(attr!.Name) && !attr.Overrides) problem = $"\"{attr.Name}\" is the name of a built-in tool. Pick another, or add Overrides = true to replace it.";
        if (problem is null && byFile.Values.Any(l => l.Name.Equals(attr!.Name, StringComparison.OrdinalIgnoreCase) && !l.File.Equals(file, StringComparison.OrdinalIgnoreCase)))
            problem = $"Another script already defines a tool called \"{attr!.Name}\".";
        if (problem is null && tools[0].GetConstructor(Type.EmptyTypes) is null) problem = $"{tools[0].Name} needs a public constructor with no parameters.";
        if (problem is not null)
        {
            diags.Add(new ToolDiagnostic("Error", "CT0002", 1, 1, problem));
            try { alc.Unload(); } catch { }
            return (null, diags);
        }

        Type type = tools[0];
        ToolAccess access = ToolAccess.ReadWrite;
        return (new Loaded
        {
            File = file, Name = attr!.Name, Description = attr.Description, Hash = hash, Group = string.IsNullOrWhiteSpace(attr.Group) ? null : attr.Group,
            Async = attr.Async, Overrides = attr.Overrides, TimeoutSeconds = Math.Clamp(attr.TimeoutSeconds, 1, 3600),
            Access = access, Type = type, Alc = alc,
            Schema = ToolParams.Schema(attr.Name, attr.Description, ToolParams.Describe(type)),
        }, diags);
    }

    private static List<MetadataReference>? references;
    private static List<MetadataReference> References()
    {
        if (references is not null) return references;
        string tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "";
        List<string> paths = tpa.Split(Path.PathSeparator).Where(p =>
        {
            string n = Path.GetFileName(p);
            return n.StartsWith("System.", StringComparison.Ordinal) || n is "netstandard.dll" or "mscorlib.dll" or "Microsoft.CSharp.dll";
        }).ToList();
        paths.Add(typeof(Tool).Assembly.Location);     // ARI.LLM: Tool, [Tool], [Param]
        paths.Add(typeof(Shared).Assembly.Location);   // ARI.Common: Shared.Logger, Paths
        return references = paths.Where(p => p.Length > 0).Distinct().Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
    }

    // ── The tool Ari holds ───────────────────────────────────────────────────

    /// <summary>What gets registered on a thread. It holds only the tool's name: each call looks up the current
    /// compiled version, so an edit takes effect even in a conversation that loaded the tool earlier, and makes
    /// a fresh instance, so calls never share fields.</summary>
    private sealed class CustomToolHost(string name, Thread thread) : Tool
    {
        internal override string Name => name;
        internal override object Schema => Find(name)?.Schema ?? throw new InvalidOperationException($"{name} was removed.");
        internal override ToolAccess Access => Find(name)?.Access ?? ToolAccess.ReadWrite;

        internal override Task<ToolResult> Execute(string argsJson)
        {
            if (Find(name) is not { } l) return Task.FromResult<ToolResult>($"[Error: the {name} tool has been removed.]");
            // The instance to run comes from the current compiled version, so an edit takes effect at once.
            Tool prototype;
            try { prototype = (Tool)Activator.CreateInstance(l.Type)!; }
            catch (Exception e) { return Task.FromResult<ToolResult>($"[Error: couldn't start {name}: {e.Message}]"); }
            prototype.CallThread = thread;
            return prototype.RunGuarded(argsJson, l.TimeoutSeconds, name);
        }
    }
}
