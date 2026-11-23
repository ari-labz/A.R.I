using System.Text.Json;
using ARI.Common;

namespace ARI.Calendar;

/// <summary>
/// The secretary part of the calendar. Once a day, from nine in the morning, Ari looks at the all-day events coming up in the next week
/// (birthdays, anniversaries, trips, deadlines) that have no reminder of their own and, if there are any she hasn't mentioned yet, sends
/// one message about what's worth preparing now: a card for a birthday, packing for a trip. Each occurrence is mentioned once. Settings and
/// what has been mentioned live in LookAhead.json; switch it off there or in the control panel's Calendar tab.
/// </summary>
internal sealed class LookAhead(string persistentDataDir)
{
    public sealed class State
    {
        public bool    Enabled   { get; set; } = true;
        public int     Hour      { get; set; } = 9;
        public int     Days      { get; set; } = 7;
        public string? LastDay   { get; set; }
        public List<string> Mentioned { get; set; } = new();   // "eventId@start", trimmed to the last 200
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private string FilePath => Path.Combine(persistentDataDir, "LookAhead.json");

    public State Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save(State s)
    {
        if (s.Mentioned.Count > 200) s.Mentioned.RemoveRange(0, s.Mentioned.Count - 200);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(s, Json));
    }

    /// <summary>Called every minute by the reminder loop; does nothing until the day's look has come due.</summary>
    public async Task Check(Func<DateTime, DateTime, IReadOnlyList<CalendarEventInfo>> eventsIn, CancellationToken ct)
    {
        if (Modules.Llm is not { } llm) return;
        State s = Load();
        DateTime now = DateTime.Now;
        string today = now.ToString("yyyy-MM-dd");
        if (!s.Enabled || s.LastDay == today || now.Hour < s.Hour || !llm.IsIdle) return;

        s.LastDay = today;
        List<CalendarEventInfo> coming = eventsIn(now.Date.AddDays(1), now.Date.AddDays(s.Days + 1))
            .Where(e => e.IsWholeDay && e.RemindBeforeMinutes is null && !s.Mentioned.Contains(Key(e)))
            .OrderBy(e => e.Start).ToList();
        if (coming.Count == 0) { Save(s); return; }

        s.Mentioned.AddRange(coming.Select(Key));
        Save(s);

        string list = string.Join("\n", coming.Select(e => $"- {e.Title}: {e.Start:dddd d MMMM} ({(int)(e.Start.Date - now.Date).TotalDays} days){(string.IsNullOrWhiteSpace(e.Notes) ? "" : $" — {e.Notes}")}"));
        string prompt = "You're looking over the owner's week like a good secretary. These are coming up and nothing has been said about them yet:\n" + list +
                        "\n\nSend one short message: for each one that needs something done beforehand (a card or a present for a birthday, booking, packing, " +
                        "paperwork for a deadline), say what and by when. Skip anything that needs no preparation. Offer to set a reminder. Don't list what needs nothing.";
        await llm.FireReminderAsync(prompt, list, "Coming up", ct);
    }

    private static string Key(CalendarEventInfo e) => $"{e.Id}@{e.Start:yyyy-MM-dd}";
}
