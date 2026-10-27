using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using ARI.Common;
using ARI.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>What the pipeline editor and validator need to know about a script: its ports.</summary>
internal sealed record ScriptShape(string Description, string[] Inputs, string[] OptionalInputs, string[] Outputs);

/// <summary>The scripts people have written, compiled while A·R·I runs. Saving a script compiles it; if that works it replaces the
/// running version at once (the next node run uses it), and if not the old version keeps running and the errors are returned.
/// Each script is loaded into its own unloadable context, so replacing or deleting one frees the old copy.</summary>
internal sealed class ScriptRegistry
{
    private sealed record Port(string Name, bool Optional);
    private sealed record Loaded(string Name, string Source, AssemblyLoadContext Context, Type Type, ScriptShape Shape, List<Port> Ports);

    private static readonly Regex NameRx = new(@"^[A-Za-z][A-Za-z0-9_-]{0,39}$", RegexOptions.Compiled);

    // Every script starts with these, so a short one needs no using lines. `#line 1` keeps reported line numbers the script's own.
    private const string DefaultUsings =
        "using System; using System.Collections.Generic; using System.IO; using System.Linq; using System.Net.Http; using System.Text; " +
        "using System.Text.Json; using System.Text.RegularExpressions; using System.Threading; using System.Threading.Tasks; using ARI.Scripting;\n#line 1\n";

    private static readonly Lazy<List<MetadataReference>> References = new(() =>
    {
        List<MetadataReference> refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        refs.Add(MetadataReference.CreateFromFile(typeof(ScriptNode).Assembly.Location));
        return refs;
    });

    private readonly string folder;
    private readonly ConcurrentDictionary<string, Loaded> loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> failed = new(StringComparer.OrdinalIgnoreCase);   // name → last compile error, for scripts on disk that don't compile
    private readonly object writeLock = new();

    internal ScriptRegistry(string folder) => this.folder = folder;

    internal static bool ValidName(string name) => NameRx.IsMatch(name);

    /// <summary>Copies the scripts ARI ships (the Memory steps) into the scripts folder if they're missing. They are ordinary scripts
    /// from then on: edit them freely, ARI never overwrites a copy that exists.</summary>
    internal void SeedShipped()
    {
        Assembly assembly = typeof(ScriptRegistry).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.Contains(".SystemScripts.") && n.EndsWith(".cs")))
        {
            string name = resource[..^3][(resource.LastIndexOf(".SystemScripts.") + ".SystemScripts.".Length)..];
            string file = Path.Combine(folder, name + ".cs");
            if (File.Exists(file) || !ValidName(name)) continue;
            using Stream stream = assembly.GetManifestResourceStream(resource)!;
            using StreamReader reader = new(stream);
            Directory.CreateDirectory(folder);
            File.WriteAllText(file, reader.ReadToEnd());
        }
    }

    /// <summary>Compiles every script in the folder. One that doesn't compile is recorded and skipped.</summary>
    internal void LoadAll()
    {
        if (!Directory.Exists(folder)) return;
        foreach (string file in Directory.EnumerateFiles(folder, "*.cs"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (!ValidName(name)) continue;
            string? error = Compile(name, File.ReadAllText(file), out Loaded? script);
            if (script is not null) { loaded[name] = script; failed.TryRemove(name, out _); }
            else { failed[name] = error ?? "Did not compile."; Shared.Logger.LogWarning("[Scripts] '{Name}' did not compile: {Error}", name, error); }
        }
        Shared.Logger.LogInformation("[Scripts] {Count} script(s) loaded.", loaded.Count);
    }

    /// <summary>Compiles <paramref name="source"/> and, if it works, saves it and swaps it in. Returns null on success, otherwise the
    /// compiler's errors (and nothing is changed).</summary>
    internal string? Save(string name, string source)
    {
        if (!ValidName(name)) return "Use letters, numbers, - or _ for the name (up to 40 characters, starting with a letter).";
        string? error = Compile(name, source, out Loaded? script);
        if (script is null) return error ?? "Did not compile.";
        lock (writeLock)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, name + ".cs"), source);
            if (loaded.TryGetValue(name, out Loaded? old)) Unload(old);
            loaded[name] = script;
            failed.TryRemove(name, out _);
        }
        Shared.Logger.LogInformation("[Scripts] '{Name}' compiled and loaded.", name);
        return null;
    }

    internal bool Delete(string name)
    {
        lock (writeLock)
        {
            bool had = loaded.TryRemove(name, out Loaded? old) | failed.TryRemove(name, out _);
            if (old is not null) Unload(old);
            string file = Path.Combine(folder, name + ".cs");
            if (ValidName(name) && File.Exists(file)) { File.Delete(file); had = true; }
            return had;
        }
    }

    internal string? Source(string name)
    {
        if (loaded.TryGetValue(name, out Loaded? s)) return s.Source;
        string file = Path.Combine(folder, name + ".cs");
        return ValidName(name) && File.Exists(file) ? File.ReadAllText(file) : null;
    }

    internal ScriptShape? Shape(string name) => loaded.TryGetValue(name, out Loaded? s) ? s.Shape : null;

    /// <summary>Every script on disk: its ports if it compiles, or the error if it doesn't.</summary>
    internal IReadOnlyList<(string Name, ScriptShape? Shape, string? Error)> List() =>
        loaded.Keys.Select(n => (n, (ScriptShape?)loaded[n].Shape, (string?)null))
            .Concat(failed.Select(f => (f.Key, (ScriptShape?)null, (string?)f.Value)))
            .OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Runs a script once with these inputs (by port name) and returns its outputs. A fresh instance each time, so nothing
    /// leaks from one run to the next.</summary>
    internal async Task<Dictionary<string, string?>> Run(string name, IReadOnlyDictionary<string, string?> inputs, ScriptContext ctx)
    {
        if (!loaded.TryGetValue(name, out Loaded? script)) throw new InvalidOperationException($"Script '{name}' isn't loaded (does it have errors?).");
        ScriptNode node = (ScriptNode)Activator.CreateInstance(script.Type)!;
        foreach (PropertyInfo prop in script.Type.GetProperties().Where(p => p.GetCustomAttribute<InputAttribute>() is not null))
            prop.SetValue(node, inputs.TryGetValue(prop.Name, out string? v) ? v : null);

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ctx.Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(node.TimeoutSeconds, 1, 600)));
        ScriptContext bounded = new(ctx.ThreadKey, ctx.Username, limit.Token, m => Shared.Logger.LogInformation("[Script:{Name}] {Message}", name, m), ctx.AskDelegate, ctx.MemoryDelegate);
        Task work = Task.Run(() => node.Run(bounded), limit.Token);
        Task finished = await Task.WhenAny(work, Task.Delay(Timeout.Infinite, limit.Token).ContinueWith(_ => { }));
        if (finished != work)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"Script '{name}' took longer than {node.TimeoutSeconds}s and was stopped waiting for.");
        }
        await work;   // surfaces the script's own exception

        Dictionary<string, string?> outputs = new(StringComparer.OrdinalIgnoreCase);
        foreach (PropertyInfo prop in script.Type.GetProperties().Where(p => p.GetCustomAttribute<OutputAttribute>() is not null))
            outputs[prop.Name] = (string?)prop.GetValue(node);
        return outputs;
    }

    // ── compiling ────────────────────────────────────────────────────────────────

    private static string? Compile(string name, string source, out Loaded? script)
    {
        script = null;
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Script_" + name + "_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(DefaultUsings + source)],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Disable));

        using MemoryStream ms = new();
        var result = compilation.Emit(ms);
        if (!result.Success)
            return string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(Describe));

        ms.Position = 0;
        AssemblyLoadContext context = new ScriptContextLoader();
        Assembly assembly = context.LoadFromStream(ms);

        Type[] nodes = assembly.GetTypes().Where(t => t.IsPublic && !t.IsAbstract && t.IsSubclassOf(typeof(ScriptNode))).ToArray();
        string? problem = nodes.Length == 0 ? "The script needs one public class that inherits from ScriptNode."
            : nodes.Length > 1 ? "The script has more than one ScriptNode class; keep one per file." : null;
        if (problem is null)
        {
            List<Port> ports = new();
            foreach (PropertyInfo prop in nodes[0].GetProperties())
            {
                InputAttribute? input = prop.GetCustomAttribute<InputAttribute>();
                OutputAttribute? output = prop.GetCustomAttribute<OutputAttribute>();
                if (input is null && output is null) continue;
                if (prop.PropertyType != typeof(string) || !prop.CanRead || !prop.CanWrite) { problem = $"{prop.Name}: ports must be public string properties with get and set."; break; }
                if (input is not null && output is not null) { problem = $"{prop.Name} can't be both an input and an output."; break; }
                ports.Add(new Port(prop.Name, input?.Optional ?? false));
            }
            if (problem is null && ports.Count(p => nodes[0].GetProperty(p.Name)!.GetCustomAttribute<OutputAttribute>() is not null) == 0) problem = "The script needs at least one [Output] property.";
            if (problem is null)
            {
                ScriptNode probe = (ScriptNode)Activator.CreateInstance(nodes[0])!;
                string[] ins = ports.Where(p => nodes[0].GetProperty(p.Name)!.GetCustomAttribute<InputAttribute>() is not null).Select(p => p.Name).ToArray();
                string[] outs = ports.Where(p => nodes[0].GetProperty(p.Name)!.GetCustomAttribute<OutputAttribute>() is not null).Select(p => p.Name).ToArray();
                string[] optional = ports.Where(p => p.Optional).Select(p => p.Name).ToArray();
                script = new Loaded(name, source, context, nodes[0], new ScriptShape(probe.Description, ins, optional, outs), ports);
                return null;
            }
        }
        Unload(context);
        return problem;
    }

    private static string Describe(Diagnostic d)
    {
        FileLinePositionSpan span = d.Location.GetLineSpan();
        return $"line {span.StartLinePosition.Line + 1}, col {span.StartLinePosition.Character + 1}: {d.GetMessage()} ({d.Id})";
    }

    private static void Unload(Loaded old) => Unload(old.Context);
    private static void Unload(AssemblyLoadContext context) { try { context.Unload(); } catch { /* the old copy is freed when nothing references it */ } }

    private sealed class ScriptContextLoader() : AssemblyLoadContext(isCollectible: true);
}
