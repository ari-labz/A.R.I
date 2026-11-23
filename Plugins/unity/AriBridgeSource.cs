/// <summary>The editor script unity_install_bridge puts into a Unity project as Assets/Editor/AriBridge.cs. Inside the Unity editor it listens on
/// localhost only and runs each request on Unity's main thread, so Ari can read and change the open scene the way a person would in the Inspector.
/// Every change goes through SerializedObject, so it is on Unity's undo stack and marks the scene as changed.</summary>
public static class AriBridgeSource
{
    public const string Code = """
// Installed by ARI. Lets ARI look at and change the open scene from outside the editor, on this machine only.
// Remove this file to disconnect ARI from this project.
#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class AriBridge
{
    const int Port = __PORT__;
    static HttpListener listener;
    static Thread thread;
    static readonly ConcurrentQueue<Action> work = new ConcurrentQueue<Action>();
    static readonly List<string> log = new List<string>();

    static AriBridge()
    {
        Application.logMessageReceived += (msg, stack, type) =>
        {
            lock (log) { log.Add($"[{type}] {msg}" + (type == LogType.Error || type == LogType.Exception ? "\n" + stack.Trim() : "")); if (log.Count > 300) log.RemoveAt(0); }
        };
        EditorApplication.update += () => { while (work.TryDequeue(out Action a)) a(); };
        AssemblyReloadEvents.beforeAssemblyReload += Stop;
        EditorApplication.quitting += Stop;
        Start();
    }

    static void Start()
    {
        try
        {
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Start();
            thread = new Thread(Listen) { IsBackground = true, Name = "AriBridge" };
            thread.Start();
        }
        catch (Exception e) { Debug.LogWarning($"[AriBridge] Couldn't listen on port {Port}: {e.Message}"); }
    }

    static void Stop() { try { listener?.Stop(); listener?.Close(); } catch { } listener = null; }

    static void Listen()
    {
        while (listener != null && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); } catch { return; }
            string path = ctx.Request.Url.AbsolutePath.Trim('/');
            var q = ctx.Request.QueryString;
            string answer = null;
            var done = new ManualResetEventSlim();
            work.Enqueue(() =>
            {
                try { answer = Handle(path, k => q[k]); }
                catch (Exception e) { answer = "Error: " + e.Message; }
                done.Set();
            });
            if (!done.Wait(30000)) answer = "Error: the editor didn't answer in time (is it busy compiling or showing a dialog?).";
            byte[] bytes = Encoding.UTF8.GetBytes(answer ?? "");
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            try { ctx.Response.OutputStream.Write(bytes, 0, bytes.Length); ctx.Response.Close(); } catch { }
        }
    }

    static string Handle(string action, Func<string, string> arg)
    {
        switch (action)
        {
            case "status":
                return $"Unity {Application.unityVersion}, project {Path.GetFileName(Path.GetDirectoryName(Application.dataPath))}, scene {SceneManager.GetActiveScene().path}, " +
                       (EditorApplication.isPlaying ? "playing" : "editing") + (EditorApplication.isCompiling ? ", compiling" : "");
            case "hierarchy":
            {
                var sb = new StringBuilder();
                int depth = int.TryParse(arg("depth"), out int d) ? d : 4;
                foreach (GameObject go in SceneManager.GetActiveScene().GetRootGameObjects()) Tree(sb, go, 0, depth);
                return sb.Length == 0 ? "The scene is empty." : sb.ToString();
            }
            case "inspect":
            {
                GameObject go = Find(arg("path"));
                var sb = new StringBuilder($"{PathOf(go)}{(go.activeSelf ? "" : " (inactive)")}\n");
                foreach (Component c in go.GetComponents<Component>())
                {
                    if (c == null) { sb.AppendLine("- (missing script)"); continue; }
                    sb.AppendLine($"- {c.GetType().Name}");
                    var so = new SerializedObject(c);
                    SerializedProperty p = so.GetIterator();
                    if (!p.NextVisible(true)) continue;
                    do { if (p.name != "m_Script") sb.AppendLine($"    {p.propertyPath}: {Show(p)}"); } while (p.NextVisible(false));
                }
                return sb.ToString();
            }
            case "set":
            {
                GameObject go = Find(arg("path"));
                Component c = go.GetComponents<Component>().FirstOrDefault(x => x != null && x.GetType().Name.Equals(arg("component"), StringComparison.OrdinalIgnoreCase));
                if (c == null) return $"Error: {PathOf(go)} has no {arg("component")}.";
                var so = new SerializedObject(c);
                SerializedProperty p = so.FindProperty(arg("field"));
                if (p == null) return $"Error: {c.GetType().Name} has no field {arg("field")}. Use inspect to see the field names.";
                Assign(p, arg("value") ?? "");
                so.ApplyModifiedProperties();
                if (!EditorApplication.isPlaying) EditorSceneManager.MarkSceneDirty(go.scene);
                return $"{PathOf(go)} {c.GetType().Name}.{p.propertyPath} = {Show(p)}";
            }
            case "select":
            {
                GameObject go = Find(arg("path"));
                Selection.activeGameObject = go;
                EditorGUIUtility.PingObject(go);
                SceneView.lastActiveSceneView?.FrameSelected();
                return $"Selected {PathOf(go)}.";
            }
            case "play": EditorApplication.isPlaying = true; return "Entering play mode.";
            case "stop": EditorApplication.isPlaying = false; return "Leaving play mode.";
            case "pause": EditorApplication.isPaused = !EditorApplication.isPaused; return EditorApplication.isPaused ? "Paused." : "Resumed.";
            case "save": EditorSceneManager.SaveOpenScenes(); return "Saved the open scenes.";
            case "refresh": AssetDatabase.Refresh(); return "Refreshed the assets; scripts recompile if they changed.";
            case "menu": return EditorApplication.ExecuteMenuItem(arg("item")) ? $"Ran {arg("item")}." : $"Error: there's no menu item {arg("item")}.";
            case "console":
            {
                int n = int.TryParse(arg("lines"), out int l) ? l : 40;
                lock (log) return log.Count == 0 ? "The console is empty." : string.Join("\n", log.Skip(Math.Max(0, log.Count - n)));
            }
            case "clear_console": lock (log) log.Clear(); return "Cleared.";
            case "capture":
            {
                bool scene = arg("view") == "scene";
                Camera cam = scene ? SceneView.lastActiveSceneView?.camera : Camera.main;
                if (cam == null) cam = scene ? Camera.main : SceneView.lastActiveSceneView?.camera;
                if (cam == null) cam = Camera.allCameras.FirstOrDefault();
                if (cam == null) return "Error: there's no camera to capture from.";
                int w = int.TryParse(arg("width"), out int ww) ? ww : 960, h = int.TryParse(arg("height"), out int hh) ? hh : 540;
                var rt = new RenderTexture(w, h, 24);
                RenderTexture before = cam.targetTexture, active = RenderTexture.active;
                cam.targetTexture = rt; cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
                cam.targetTexture = before; RenderTexture.active = active;
                string file = arg("file");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllBytes(file, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex); rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
                return $"Captured the {(scene ? "scene" : "game")} camera {cam.name} to {file}.";
            }
            default: return "Error: unknown action " + action;
        }
    }

    static void Tree(StringBuilder sb, GameObject go, int level, int max)
    {
        string comps = string.Join(", ", go.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name));
        sb.AppendLine($"{new string(' ', level * 2)}- {go.name}{(go.activeSelf ? "" : " (inactive)")}{(comps.Length > 0 ? " [" + comps + "]" : "")}");
        if (level + 1 >= max) { if (go.transform.childCount > 0) sb.AppendLine($"{new string(' ', level * 2 + 2)}… {go.transform.childCount} more"); return; }
        foreach (Transform child in go.transform) Tree(sb, child.gameObject, level + 1, max);
    }

    static GameObject Find(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new Exception("give the object's path, e.g. Player/Camera.");
        string[] parts = path.Trim('/').Split('/');
        GameObject root = SceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(g => g.name == parts[0]);
        Transform t = root?.transform;
        for (int i = 1; t != null && i < parts.Length; i++) t = t.Cast<Transform>().FirstOrDefault(c => c.name == parts[i]);
        return t != null ? t.gameObject : throw new Exception($"no object at {path}. Use hierarchy to see the paths.");
    }

    static string PathOf(GameObject go) => go.transform.parent == null ? go.name : PathOf(go.transform.parent.gameObject) + "/" + go.name;

    static string Show(SerializedProperty p)
    {
        switch (p.propertyType)
        {
            case SerializedPropertyType.Integer: return p.intValue.ToString();
            case SerializedPropertyType.Boolean: return p.boolValue ? "true" : "false";
            case SerializedPropertyType.Float: return p.floatValue.ToString("0.###");
            case SerializedPropertyType.String: return "\"" + p.stringValue + "\"";
            case SerializedPropertyType.Enum: return p.enumValueIndex >= 0 && p.enumValueIndex < p.enumDisplayNames.Length ? p.enumDisplayNames[p.enumValueIndex] : p.intValue.ToString();
            case SerializedPropertyType.Vector2: return p.vector2Value.ToString();
            case SerializedPropertyType.Vector3: return p.vector3Value.ToString();
            case SerializedPropertyType.Vector4: return p.vector4Value.ToString();
            case SerializedPropertyType.Quaternion: return p.quaternionValue.eulerAngles.ToString() + " (euler)";
            case SerializedPropertyType.Color: return "#" + ColorUtility.ToHtmlStringRGBA(p.colorValue);
            case SerializedPropertyType.ObjectReference: return p.objectReferenceValue ? p.objectReferenceValue.name + " (" + p.objectReferenceValue.GetType().Name + ")" : "none";
            case SerializedPropertyType.Generic: return p.isArray ? $"list of {p.arraySize}" : "(group)";
            default: return p.propertyType.ToString();
        }
    }

    static void Assign(SerializedProperty p, string v)
    {
        float[] nums = v.Trim('(', ')').Split(',').Select(s => float.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f).ToArray();
        float N(int i) => i < nums.Length ? nums[i] : 0f;
        switch (p.propertyType)
        {
            case SerializedPropertyType.Integer: p.intValue = int.Parse(v); break;
            case SerializedPropertyType.Boolean: p.boolValue = v.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on"; break;
            case SerializedPropertyType.Float: p.floatValue = N(0); break;
            case SerializedPropertyType.String: p.stringValue = v; break;
            case SerializedPropertyType.Enum:
                int idx = Array.FindIndex(p.enumDisplayNames, n => n.Equals(v, StringComparison.OrdinalIgnoreCase));
                p.enumValueIndex = idx >= 0 ? idx : int.Parse(v); break;
            case SerializedPropertyType.Vector2: p.vector2Value = new Vector2(N(0), N(1)); break;
            case SerializedPropertyType.Vector3: p.vector3Value = new Vector3(N(0), N(1), N(2)); break;
            case SerializedPropertyType.Vector4: p.vector4Value = new Vector4(N(0), N(1), N(2), N(3)); break;
            case SerializedPropertyType.Quaternion: p.quaternionValue = Quaternion.Euler(N(0), N(1), N(2)); break;
            case SerializedPropertyType.Color:
                if (!ColorUtility.TryParseHtmlString(v.StartsWith("#") ? v : "#" + v, out Color col)) throw new Exception("give a colour like #FF8800");
                p.colorValue = col; break;
            default: throw new Exception($"{p.propertyPath} is a {p.propertyType}, which can't be set from text yet.");
        }
    }
}
#endif
""";
}
