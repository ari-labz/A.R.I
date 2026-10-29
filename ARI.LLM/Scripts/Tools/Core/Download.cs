namespace ARI.LLM;

[Tool("download_file", "Download a file from the web into your working folder: a PDF, spreadsheet, image, data file, archive. Give the address and, if you want, the name to save it as. " +
                       "It refuses files over 200 MB and never overwrites a file that was there before the conversation. Then read it with read_file (or the reader for its type), or hand it over with deliver_file. " +
                       "To just read a web page use fetch_page.")]
public class DownloadFile : Tool
{
    [Param("The full address, starting with http:// or https://")]
    public string Url;

    [Param("The name to save it as in your working folder. Defaults to the name in the address.", Optional = true)]
    public string Path;

    public override string OnValidate()
    {
        if (!IsAdmin) return "Only the owner, chatting in the app, can download files.";
        return Uri.TryCreate(Url, UriKind.Absolute, out Uri u) && u.Scheme is "http" or "https" ? null : "The address must start with http:// or https://.";
    }

    public override async Task<string> Run()
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(Cancelled);
        cts.CancelAfter(TimeSpan.FromMinutes(10));
        using HttpResponseMessage res = await Http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!res.IsSuccessStatusCode) return $"[Error: the server answered HTTP {(int)res.StatusCode} {res.ReasonPhrase}.]";
        if (res.Content.Headers.ContentLength > 200L * 1024 * 1024) return "[Error: that file is over 200 MB, which is more than I'll download.]";

        string name = string.IsNullOrWhiteSpace(Path) ? (res.Content.Headers.ContentDisposition?.FileNameStar ?? res.Content.Headers.ContentDisposition?.FileName ?? System.IO.Path.GetFileName(new Uri(Url).LocalPath)).Trim('"') : Path;
        if (string.IsNullOrWhiteSpace(name)) name = "download";
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(WorkingFolder, System.IO.Path.GetFileName(name)));
        if (File.Exists(full) && CallThread is not null && !CallThread.CreatedFiles.Contains(full))
            return $"[Blocked: {System.IO.Path.GetFileName(full)} was here before this conversation, so it isn't overwritten. Choose another name.]";

        long total = 0;
        await using (FileStream fs = File.Create(full))
        await using (Stream body = await res.Content.ReadAsStreamAsync(cts.Token))
        {
            byte[] buf = new byte[81920]; int n;
            while ((n = await body.ReadAsync(buf, cts.Token)) > 0)
            {
                total += n;
                if (total > 200L * 1024 * 1024) { fs.Close(); File.Delete(full); return "[Error: that file is over 200 MB, which is more than I'll download.]"; }
                await fs.WriteAsync(buf.AsMemory(0, n), cts.Token);
            }
        }
        SaveRules.Created(CallThread, full);
        return $"Saved {full} ({total:N0} bytes, {res.Content.Headers.ContentType?.MediaType ?? "unknown type"}).";
    }
}
