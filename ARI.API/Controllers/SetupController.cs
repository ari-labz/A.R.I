using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using ARI.API.Auth;
using ARI.API.Data;
using Microsoft.Extensions.Logging;
using ARI.API.Setup;
using ARI.Common;
using ARI.LLM;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>First-run wizard backend. Only /setup/status and /setup/account are anonymous; everything else
/// needs the admin session that /setup/account hands back.</summary>
[Route("setup")]
[ApiController]
public class SetupController(UserStore users, AuthService auth, PersistentData persistentData, ILoggerFactory loggerFactory) : ControllerBase
{
    private readonly ILogger log = loggerFactory.CreateLogger("ARI.Setup");

    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        needsAccount     = users.CountUsers() == 0,
        setupComplete    = SetupState.SetupComplete,
        tutorialComplete = SetupState.TutorialComplete,
    });

    public record AccountRequest(string Token, string Username, string Password);

    [HttpPost("account")]
    public IActionResult CreateAccount([FromBody] AccountRequest req)
    {
        if (users.CountUsers() > 0) return Conflict(new { error = "An account already exists." });

        string ip = ClientAddress.Of(HttpContext);
        if (!SetupState.TokenMatches(req.Token))
        {
            int attempts = users.RecordFailedAttempt(ip);
            if (attempts >= 5) return StatusCode(403, new { error = "Too many wrong tokens. This IP has been locked out." });
            return Unauthorized(new { error = "That Setup Token is not right. It is printed in the ARI log (docker logs)." });
        }

        string name = (req.Username ?? "").Trim();
        if (AccountRules.CheckUsername(name) is { } usernameProblem) return BadRequest(new { error = usernameProblem });
        if (AccountRules.CheckPassword(req.Password) is { } passwordProblem) return BadRequest(new { error = passwordProblem });

        User user = users.CreateUser(name, AuthService.HashPassword(req.Password!), "Admin", name);
        users.SetPassword(user.Id, user.PasswordHash, mustChange: false);
        user = users.GetById(user.Id)!;
        users.ClearFailedAttempts(ip);
        SetupState.ClearToken();
        SetupState.SetMaster(user.Id);   // the account made here is the Master, for good
        log.LogInformation("[Setup] Admin account '{User}' created.", name);

        string sessionId = Guid.NewGuid().ToString();
        (string token, UserSession session) = auth.IssueToken(user, sessionId, "Browser", false);
        users.CreateSession(session);
        AuthCookie.Set(Response, token, false, ClientAddress.IsHttps(HttpContext));
        return Ok(new { token, role = user.Role, displayName = user.DisplayName, mustChangePassword = false });
    }

    // ── Everything below needs the admin session ─────────────────────────────────

    private bool IsAdmin()
    {
        string? id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(id, out int uid) && users.GetById(uid)?.Role == "Admin";
    }

    [HttpGet("hardware")]
    public IActionResult Hardware() => !IsAdmin() ? StatusCode(403) : Ok(HardwareInfo.Detect());

    public record InspectRequest(string Url);

    [HttpPost("inspect")]
    public async Task<IActionResult> Inspect([FromBody] InspectRequest req, CancellationToken ct)
    {
        if (!IsAdmin()) return StatusCode(403);
        if (!ModelInspector.TryParse(req.Url ?? "", out string repo, out string? file))
            return BadRequest(new { error = "That doesn't look like a Hugging Face link. Paste a model page or file link, e.g. https://huggingface.co/unsloth/Qwen3.5-9B-GGUF" });

        RepoListing listing = await ModelInspector.ListAsync(repo, file, ct);
        if (listing.Error is not null) return BadRequest(new { error = listing.Error });

        HardwareReport hw = HardwareInfo.Detect();
        double kvPerTok   = ModelInspector.KvBytesPerToken(listing.Arch, listing.Files[listing.Files.Count / 2].SizeBytes);

        var files = listing.Files.Select(f =>
        {
            double perTok = listing.Arch is not null ? kvPerTok : ModelInspector.KvBytesPerToken(null, f.SizeBytes);
            int maxCtx = ModelInspector.MaxContext(hw.UsableBytes, f.SizeBytes, perTok);
            string fit = maxCtx >= 16384 ? "fits" : maxCtx > 0 ? "tight" : "no";
            return new
            {
                f.Path, f.Name, f.SizeBytes, f.Quant, f.DownloadUrl,
                fit, maxContext = maxCtx,
                needBytesAt8k = (long)(f.SizeBytes + perTok * 8192 + ModelInspector.Overhead),
            };
        }).ToList();

        return Ok(new { repo, kvExact = listing.Arch is not null, hardware = hw, files });
    }

    public record ModelRequest(string Repo, string File, string DownloadUrl, long SizeBytes, int ContextSize);

    [HttpPost("model")]
    public IActionResult ChooseModel([FromBody] ModelRequest req)
    {
        if (!IsAdmin()) return StatusCode(403);
        if (SetupDownload.Running) return Conflict(new { error = "A download is already running." });
        if (!req.DownloadUrl.StartsWith("https://huggingface.co/", StringComparison.Ordinal) || !req.File.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Only Hugging Face .gguf links are accepted." });

        string rel = $"{req.Repo.Split('/').Last()}/{Path.GetFileName(req.File)}";
        string modelName = Path.GetFileNameWithoutExtension(req.File);
        SetupDownload.Start(req.DownloadUrl, Path.Combine(Paths.Models, rel), req.SizeBytes, log, onDone: () =>
        {
            Model model = persistentData.GetModel(modelName) ?? persistentData.AddModel(new Model
            {
                Name = modelName, Path = rel, DownloadLink = req.DownloadUrl,
            });

            Server server = persistentData.GetServerByName("Main Server") ?? persistentData.AddServer(new Server
            {
                Name = "Main Server", Port = 8081, ContextSize = req.ContextSize,
                KvCacheQuantK = 8, KvCacheQuantV = 8, CurrentModelName = model.Name, BootStartup = true,
                Slots = [new NamedSlot { Name = "Default", ContextLimit = req.ContextSize }],
            });
            (Modules.Llm as LLMModule)?.AddServer(server);

            foreach (AgentDefinition a in persistentData.GetAgents())
                persistentData.MergeAgent(a.Name, new JsonObject { ["serverName"] = server.Name });
            SetupState.Set(setup: true);
            log.LogInformation("[Setup] Model {Model} downloaded; server '{Server}' created.", model.Name, server.Name);
        });
        return Ok();
    }

    [HttpGet("progress")]
    public IActionResult Progress() => !IsAdmin() ? StatusCode(403) : Ok(SetupDownload.Snapshot());

    [HttpPost("skip-model")]
    public IActionResult SkipModel() { if (!IsAdmin()) return StatusCode(403); SetupState.Set(setup: true); return Ok(); }

    [HttpPost("tutorial-complete")]
    public IActionResult TutorialDone() { if (!IsAdmin()) return StatusCode(403); SetupState.Set(tutorial: true); return Ok(); }

    /// <summary>Lets an admin run the wizard's tutorial again (keeps their account and models).</summary>
    [HttpPost("restart-tutorial")]
    public IActionResult RestartTutorial() { if (!IsAdmin()) return StatusCode(403); SetupState.Set(tutorial: false); return Ok(); }
}

/// <summary>One background model download at a time, with progress the wizard polls.</summary>
public static class SetupDownload
{
    private static readonly object gate = new();
    private static readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static string phase = "idle", message = "";
    private static long done, total;

    public static bool Running => phase == "downloading";

    public static object Snapshot() { lock (gate) return new { phase, message, bytesDone = done, bytesTotal = total }; }

    public static void Start(string url, string dest, long expected, ILogger log, Action onDone)
    {
        lock (gate) { phase = "downloading"; message = ""; done = 0; total = expected; }
        _ = Task.Run(async () =>
        {
            string part = dest + ".part";
            try
            {
                if (File.Exists(dest)) { lock (gate) { done = total = new FileInfo(dest).Length; } }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    using HttpResponseMessage r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    r.EnsureSuccessStatusCode();
                    if (r.Content.Headers.ContentLength is long len) lock (gate) total = len;
                    await using Stream src = await r.Content.ReadAsStreamAsync();
                    await using (FileStream dst = File.Create(part))
                    {
                        byte[] buf = new byte[1 << 20];
                        int n;
                        while ((n = await src.ReadAsync(buf)) > 0)
                        {
                            await dst.WriteAsync(buf.AsMemory(0, n));
                            lock (gate) done += n;
                        }
                    }
                    File.Move(part, dest, overwrite: true);
                }
                onDone();
                lock (gate) phase = "done";
            }
            catch (Exception ex)
            {
                log.LogError(ex, "[Setup] Model download failed.");
                try { File.Delete(part); } catch { }
                lock (gate) { phase = "error"; message = ex.Message; }
            }
        });
    }
}
