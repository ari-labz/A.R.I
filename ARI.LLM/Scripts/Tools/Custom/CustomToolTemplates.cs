namespace ARI.LLM;

/// <summary>Starting points for a new custom tool, offered by the control panel's New tool button.</summary>
internal static class CustomToolTemplates
{
    internal static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        ["Hello world"] = """
            [Tool("hello", "Greet someone by name. A simple example of a custom tool.")]
            public class Hello : Tool
            {
                [Param("Who to greet, e.g. Sam")]
                public string Name;

                // The only method you must write. What it returns is what Ari reads.
                public override async Task<string> Run()
                {
                    return $"Hello, {Name}!";
                }
            }
            """,

        ["Web request"] = """
            [Tool("fetch_json", "Download a JSON web address and return its text. Use it for simple public APIs.")]
            public class FetchJson : Tool
            {
                [Param("The full address, starting with https://")]
                public string Url;

                [Param("How many characters to keep", Optional = true)]
                public int MaxChars = 4000;

                public override string OnValidate()
                {
                    // Return a message to refuse the call; Ari reads it and can correct herself.
                    return Url.StartsWith("https://") ? null : "Only https:// addresses are allowed.";
                }

                public override async Task<string> Run()
                {
                    string body = await Http.GetStringAsync(Url, Cancelled);
                    return body.Length <= MaxChars ? body : body[..MaxChars] + "…";
                }

                public override string OnError(Exception error)
                {
                    return $"Couldn't fetch that address: {error.Message}";
                }
            }
            """,

        ["Background job"] = """
            // Async = true: Ari calls it, carries on talking, and is handed the result when it finishes.
            [Tool("slow_count", "Count to a number slowly, to show a background tool.", Async = true)]
            public class SlowCount : Tool
            {
                [Param("How high to count, e.g. 10")]
                public int To;

                [Param("Seconds to wait between numbers", Optional = true)]
                public double Seconds = 1;

                public override async Task<string> Run()
                {
                    for (int i = 1; i <= To; i++)
                        await Task.Delay(TimeSpan.FromSeconds(Seconds));
                    return $"Counted to {To}.";
                }
            }
            """,
    };
}
