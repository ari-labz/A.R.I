import { test, status, has, equal, assert } from "../lib.mjs";
const { master, guest } = globalThis.ari;

test("an event and a reminder are both entries: made at /entries, read by id, and each says what kind it is", async () => {
  const ev = status(await master.post("/admin/calendar/entries", {
    kind: "event", title: "Dentist", start: "2031-03-04T09:00:00", end: "2031-03-04T10:00:00", isWholeDay: false, notes: "bring forms",
  }), 200).json;
  const rem = status(await master.post("/admin/calendar/entries", {
    kind: "reminder", title: "Water plants", triggerTime: "2031-03-05T08:00:00", prompt: "Remind me to water the plants",
  }), 200).json;
  const gotEvent = status(await master.get(`/admin/calendar/entries/${ev.id}`), 200).json;
  equal(gotEvent.kind, "event"); equal(gotEvent.title, "Dentist"); equal(gotEvent.notes, "bring forms");
  const gotReminder = status(await master.get(`/admin/calendar/entries/${rem.id}`), 200).json;
  equal(gotReminder.kind, "reminder"); equal(gotReminder.prompt, "Remind me to water the plants");

  const listed = status(await master.get("/admin/calendar?from=2031-03-01T00:00:00&to=2031-03-31T00:00:00"), 200).json;
  assert(listed.events.some(e => e.id === ev.id), "event listed");
  assert(listed.reminders.some(r => r.id === rem.id), "reminder listed");

  status(await master.del(`/admin/calendar/entries/${ev.id}`), 200);
  status(await master.del(`/admin/calendar/entries/${rem.id}`), 200);
  status(await master.get(`/admin/calendar/entries/${ev.id}`), 404);
});

test("changing an entry keeps whatever is not sent, and a repeat can be added then removed", async () => {
  const ev = status(await master.post("/admin/calendar/entries", {
    kind: "event", title: "Gym", start: "2031-04-01T18:00:00", end: "2031-04-01T19:00:00", notes: "legs",
  }), 200).json;
  status(await master.patch(`/admin/calendar/entries/${ev.id}`, { title: "Gym (legs)" }), 200);
  let got = status(await master.get(`/admin/calendar/entries/${ev.id}`), 200).json;
  equal(got.title, "Gym (legs)"); equal(got.notes, "legs"); equal(got.start.slice(0, 16), "2031-04-01T18:00");
  status(await master.patch(`/admin/calendar/entries/${ev.id}`, { recurrence: { frequency: "weekly", interval: 1, daysOfWeek: ["Monday"] } }), 200);
  got = status(await master.get(`/admin/calendar/entries/${ev.id}`), 200).json;
  equal(got.recurrence.frequency, "Weekly");
  status(await master.patch(`/admin/calendar/entries/${ev.id}`, { recurrence: { frequency: "none" } }), 200);
  got = status(await master.get(`/admin/calendar/entries/${ev.id}`), 200).json;
  equal(got.recurrence.frequency.toLowerCase(), "none");
  status(await master.del(`/admin/calendar/entries/${ev.id}`), 200);
});

test("an entry needs its kind and the right details", async () => {
  equal((await master.post("/admin/calendar/entries", { title: "No kind" })).status, 400, "no kind");
  equal((await master.post("/admin/calendar/entries", { kind: "event", title: "No times" })).status, 400, "event without times");
  equal((await master.post("/admin/calendar/entries", { kind: "reminder", title: "No prompt", triggerTime: "2031-01-01T00:00:00" })).status, 400, "reminder without prompt");
  equal((await master.post("/admin/calendar/entries", { kind: "event", title: " ", start: "2031-01-01T00:00:00", end: "2031-01-01T01:00:00" })).status, 400, "blank title");
});

test("the old event, reminder and entry routes are gone, and a guest is kept out", async () => {
  status(await master.get("/admin/calendar/event/1"), 404);
  status(await master.get("/admin/calendar/reminder/1"), 404);
  status(await master.del("/admin/calendar/entry/1"), 404);
  status(await guest.get("/admin/calendar"), 403);
  status(await guest.post("/admin/calendar/entries", { kind: "event" }), 403);
});
