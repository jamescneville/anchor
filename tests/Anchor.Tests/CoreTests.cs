using Anchor.Core;
using Anchor.Core.AI;
using Anchor.Core.Connectors;
using Xunit;

namespace Anchor.Tests;

public class AlertSchedulerTests
{
    static readonly int[] Offsets = [15, 5, 1, 0];
    static readonly DateTimeOffset T0 = new(2026, 10, 19, 14, 0, 0, TimeSpan.Zero);

    static CalendarEvent Meeting(int startsInMin, int lenMin = 30, string id = "m1") =>
        new(id, "Test", "Design review", T0.AddMinutes(startsInMin), T0.AddMinutes(startsInMin + lenMin));

    [Fact]
    public void Nothing_fires_before_the_first_offset()
    {
        Assert.Empty(AlertScheduler.Evaluate([Meeting(20)], T0, new HashSet<string>(), Offsets));
    }

    [Theory]
    [InlineData(15, 15)]
    [InlineData(10, 15)]
    [InlineData(5, 5)]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    [InlineData(-3, 0)] // already started, still in progress
    public void Fires_the_closest_due_offset(int startsInMin, int expected)
    {
        var due = Assert.Single(AlertScheduler.Evaluate([Meeting(startsInMin)], T0, new HashSet<string>(), Offsets));
        Assert.Equal(expected, due.OffsetMinutes);
    }

    [Fact]
    public void Waking_from_sleep_fires_one_alert_and_marks_the_missed_ones()
    {
        var m = Meeting(2); // laptop woke 2 minutes before: 15 and 5 were missed
        var due = Assert.Single(AlertScheduler.Evaluate([m], T0, new HashSet<string>(), Offsets));
        Assert.Equal(5, due.OffsetMinutes);
        Assert.Contains(AlertScheduler.Key(m, 15), due.KeysToMark);
        Assert.Contains(AlertScheduler.Key(m, 5), due.KeysToMark);
    }

    [Fact]
    public void Acknowledged_offsets_do_not_fire_again_but_the_next_one_does()
    {
        var m = Meeting(4);
        var fired = new HashSet<string> { AlertScheduler.Key(m, 15), AlertScheduler.Key(m, 5) };
        Assert.Empty(AlertScheduler.Evaluate([m], T0, fired, Offsets));
        var due = Assert.Single(AlertScheduler.Evaluate([m], T0.AddMinutes(3.5), fired, Offsets));
        Assert.Equal(1, due.OffsetMinutes);
    }

    [Fact]
    public void Ignores_finished_cancelled_and_all_day_events()
    {
        var finished = Meeting(-60);
        var cancelled = Meeting(1, id: "c") with { Cancelled = true };
        var allDay = Meeting(1, id: "a") with { IsAllDay = true };
        Assert.Empty(AlertScheduler.Evaluate([finished, cancelled, allDay], T0, new HashSet<string>(), Offsets));
    }

    [Fact]
    public void A_rescheduled_meeting_alerts_again()
    {
        var original = Meeting(10);
        var fired = new HashSet<string> { AlertScheduler.Key(original, 15) };
        var moved = original with { Start = original.Start.AddMinutes(30), End = original.End.AddMinutes(30) };
        Assert.Empty(AlertScheduler.Evaluate([moved], T0, fired, Offsets)); // now 40 min out: quiet
        var later = AlertScheduler.Evaluate([moved], T0.AddMinutes(30), fired, Offsets);
        Assert.Equal(15, Assert.Single(later).OffsetMinutes); // key includes start time, so it fires afresh
    }

    [Theory]
    [InlineData(30, "starts in 30 min")]
    [InlineData(1340, "starts in 22h 20m")]
    [InlineData(0, "is starting NOW")]
    [InlineData(-5, "started 5 min ago")]
    public void Describe_reads_naturally(int startsInMin, string expected) =>
        Assert.Equal(expected, AlertScheduler.Describe(Meeting(startsInMin), T0));

    [Fact]
    public void Dedupe_merges_the_same_meeting_from_two_calendars_and_keeps_the_join_link()
    {
        var a = Meeting(30, id: "g") with { Source = "Google" };
        var b = Meeting(30, id: "o") with { Source = "Outlook", JoinUrl = "https://teams.microsoft.com/l/x" };
        var merged = Assert.Single(AssistantEngine.Dedupe([a, b]));
        Assert.NotNull(merged.JoinUrl);
    }
}

public class IcsParserTests
{
    const string Ics = """
        BEGIN:VCALENDAR
        BEGIN:VEVENT
        UID:one
        SUMMARY:Standup
        DTSTART:20261019T140000Z
        DTEND:20261019T141500Z
        RRULE:FREQ=WEEKLY;BYDAY=MO,WE
        LOCATION:https://zoom.us/j/123
        END:VEVENT
        BEGIN:VEVENT
        UID:two
        SUMMARY:Dentist
        DTSTART:20261020T160000Z
        DTEND:20261020T170000Z
        STATUS:CANCELLED
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void Expands_weekly_rules_and_extracts_join_links()
    {
        var from = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        var events = IcsParser.Parse(Ics.Replace("        ", ""), "ICS/test", from, from.AddDays(10));
        var standups = events.Where(e => e.Title == "Standup").OrderBy(e => e.Start).ToList();
        Assert.Equal(4, standups.Count); // Mon 19, Wed 21, Mon 26, Wed 28
        Assert.All(standups, s => Assert.Equal("https://zoom.us/j/123", s.JoinUrl));
        Assert.True(events.Single(e => e.Title == "Dentist").Cancelled);
    }
}

public class ClaudeParsingTests
{
    [Fact]
    public void Parses_json_even_when_wrapped_in_prose()
    {
        var text = "Here you go:\n[{\"id\":\"a\",\"action\":true,\"title\":\"Reply to Sam\",\"due\":\"2026-10-20T17:00:00Z\",\"confidence\":0.9},{\"id\":\"b\",\"action\":false,\"title\":\"\",\"due\":null,\"confidence\":0.2}]";
        var r = ClaudeService.ParseClassified(text);
        Assert.Equal(2, r.Count);
        Assert.True(r[0].Action);
        Assert.NotNull(r[0].Due);
        Assert.False(r[1].Action);
    }

    [Fact] public void Garbage_yields_nothing_instead_of_throwing() => Assert.Empty(ClaudeService.ParseClassified("sorry, I can't"));
}

public class BudgetTests
{
    static (BudgetTracker, StateStore) Make(AiSettings? s = null)
    {
        var store = new StateStore(Path.Combine(Path.GetTempPath(), $"anchor-test-{Guid.NewGuid():N}.json"));
        return (new BudgetTracker(store, s ?? new AiSettings()), store);
    }

    [Fact]
    public void Records_cost_from_token_usage()
    {
        var (b, _) = Make();
        var cost = b.Record("claude-sonnet-5-5", 1_000_000, 100_000); // $2.00 + $1.00
        Assert.Equal(3.00, cost, 3);
        Assert.Equal(3.00, b.SpentToday, 3);
    }

    [Fact]
    public void Daily_soft_cap_blocks_background_but_not_user_requests()
    {
        var (b, _) = Make(new AiSettings { DailySoftCapUsd = 0.5 });
        b.Record("claude-sonnet-5-5", 500_000, 0); // $1.00
        Assert.False(b.Allow(background: true));
        Assert.True(b.Allow(background: false));
    }

    [Fact]
    public void Monthly_cutoff_blocks_everything()
    {
        var (b, _) = Make(new AiSettings { CutoffUsd = 1 });
        b.Record("claude-sonnet-5-5", 1_000_000, 0);
        Assert.False(b.Allow(background: false));
    }
}

public class HeuristicTests
{
    static InboxItem Item(string from, string subject, string snippet) => new("x", "Gmail", false, from, subject, snippet, DateTimeOffset.Now);

    [Fact] public void Flags_requests() => Assert.True(AssistantEngine.LooksActionable(Item("sam@x.com", "Quick question", "Can you send the deck by EOD?")));
    [Fact] public void Skips_noreply() => Assert.False(AssistantEngine.LooksActionable(Item("noreply@shop.com", "Your receipt", "please keep this")));
    [Fact] public void Skips_newsletters() => Assert.False(AssistantEngine.LooksActionable(Item("news@x.com", "Weekly digest", "click to unsubscribe")));
}

public class InPersonAndSettingsTests
{
    static CalendarEvent Ev(string? loc, string? join = null) =>
        new("e", "Test", "Lunch", DateTimeOffset.Now.AddHours(1), DateTimeOffset.Now.AddHours(2), join, loc);

    [Fact] public void Physical_location_without_link_is_in_person() => Assert.True(AssistantEngine.IsInPerson(Ev("Cafe Roma, 12 High St")));
    [Fact] public void Video_meetings_are_not_in_person() => Assert.False(AssistantEngine.IsInPerson(Ev("https://zoom.us/j/1")));
    [Fact] public void Meetings_with_a_join_link_are_not_in_person() => Assert.False(AssistantEngine.IsInPerson(Ev("Room 4", "https://teams.microsoft.com/x")));
    [Fact] public void No_location_is_not_in_person() => Assert.False(AssistantEngine.IsInPerson(Ev(null)));

    [Fact]
    public void Settings_round_trip_through_json()
    {
        var path = Path.Combine(Path.GetTempPath(), $"anchor-settings-{Guid.NewGuid():N}.json");
        var s = new AnchorSettings { AlertIntensity = "Maximum", Hotkey = "Ctrl+Shift+W" };
        s.IcsFeeds.Add(new IcsFeed { Name = "Work", Url = "https://example.com/a.ics" });
        s.Save(path);
        var back = AnchorSettings.Load(path);
        Assert.Equal("Maximum", back.AlertIntensity);
        Assert.Equal("Ctrl+Shift+W", back.Hotkey);
        Assert.Equal("Work", Assert.Single(back.IcsFeeds).Name);
    }
}

public class FmtTests
{
    [Fact]
    public void Formats_12_and_24_hour_clocks()
    {
        var t = new DateTime(2026, 10, 19, 14, 5, 0);
        try
        {
            Fmt.Use24Hour = false; Assert.Equal("2:05 PM", Fmt.Time(t)); Assert.Equal("Mon 2:05 PM", Fmt.DayTime(t));
            Fmt.Use24Hour = true; Assert.Equal("14:05", Fmt.Time(t)); Assert.Equal("Mon 14:05", Fmt.DayTime(t));
            Assert.Equal("", Fmt.Time((DateTime?)null));
        }
        finally { Fmt.Use24Hour = false; }
    }
}

public class AiPrivacyTests
{
    static readonly IReadOnlySet<string> NoIcs = new HashSet<string>();
    static CalendarEvent Ev(string source, string title) =>
        new("e", source, title, DateTimeOffset.Now.AddHours(1), DateTimeOffset.Now.AddHours(2), "https://teams.microsoft.com/x", "Room 4");

    [Fact]
    public void Work_meetings_keep_times_but_lose_titles()
    {
        var r = Assert.Single(AiPrivacy.Events([Ev("Outlook", "Acme layoffs prep")], allowWork: false, NoIcs));
        Assert.Equal("Busy (work meeting)", r.Title);
        Assert.Null(r.JoinUrl);
        Assert.Null(r.Location);
    }

    [Fact]
    public void Personal_google_events_pass_through() =>
        Assert.Equal("Dentist", Assert.Single(AiPrivacy.Events([Ev("Google/Home", "Dentist")], allowWork: false, NoIcs)).Title);

    [Fact]
    public void Work_ics_feeds_are_redacted_but_personal_ones_are_not()
    {
        var work = new HashSet<string> { "ICS/Work" };
        var r = AiPrivacy.Events([Ev("ICS/Work", "Secret"), Ev("ICS/Family", "Soccer")], false, work);
        Assert.Equal(["Busy (work meeting)", "Soccer"], r.Select(e => e.Title));
    }

    [Fact]
    public void Allowing_work_content_disables_redaction() =>
        Assert.Equal("Acme layoffs prep", Assert.Single(AiPrivacy.Events([Ev("Outlook", "Acme layoffs prep")], allowWork: true, NoIcs)).Title);

    [Fact]
    public void Work_followups_collapse_into_a_count()
    {
        var tasks = new[]
        {
            new SuggestedTask { Id = "1", Title = "Send Q3 deck to VP", Source = "Outlook" },
            new SuggestedTask { Id = "2", Title = "Reply about budget", Source = "Teams" },
            new SuggestedTask { Id = "3", Title = "Call plumber", Source = "Gmail" },
        };
        var r = AiPrivacy.Tasks(tasks, false, NoIcs);
        Assert.Equal(2, r.Count);
        Assert.Contains(r, t => t.Title == "Call plumber");
        Assert.Contains(r, t => t.Title == "2 work follow-ups (details withheld)");
        Assert.DoesNotContain(r, t => t.Title.Contains("VP"));
    }
}

public class BridgeTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parses_tasks_and_keeps_only_http_links()
    {
        const string json = """
            [
              {"id":"a1","title":"Reply to Sam about budget","source":"Outlook","from":"Sam","link":"https://outlook.office.com/x","due":"2026-10-20T17:00:00Z","confidence":0.9},
              {"id":"a2","title":"Evil link","source":"Gmail","link":"file:///C:/Windows/System32/calc.exe"},
              {"title":"No id given","source":"Slack"},
              {"id":"a4","title":""},
              {"id":"a5","title":"Custom protocol","link":"claude://cowork/new?q=x"}
            ]
            """;
        var t = Bridge.ParseTasks(json, Now);
        Assert.Equal(4, t.Count);
        Assert.Equal("claude:a1", t[0].Id);
        Assert.Equal("https://outlook.office.com/x", t[0].Link);
        Assert.Equal(0.9, t[0].Confidence);
        Assert.Null(t[1].Link);                     // file: rejected
        Assert.Equal(12, t[2].Id.Length - "claude:".Length); // generated stable id
        Assert.Null(t[3].Link);                     // custom protocol rejected
    }

    [Fact]
    public void Accepts_a_wrapped_object_and_survives_garbage()
    {
        Assert.Single(Bridge.ParseTasks("""{"tasks":[{"id":"x","title":"One"}]}""", Now));
        Assert.Empty(Bridge.ParseTasks("not json at all", Now));
        Assert.Empty(Bridge.ParseTasks("""{"foo":1}""", Now));
    }

    [Fact]
    public void Generated_ids_are_stable_so_reruns_do_not_duplicate()
    {
        var a = Bridge.ParseTasks("""[{"title":"Same","source":"Gmail"}]""", Now)[0].Id;
        var b = Bridge.ParseTasks("""[{"title":"Same","source":"Gmail"}]""", Now.AddHours(3))[0].Id;
        Assert.Equal(a, b);
    }

    [Fact]
    public void Clamps_confidence_and_title_length()
    {
        var t = Assert.Single(Bridge.ParseTasks("[{\"id\":\"x\",\"title\":\"" + new string('a', 500) + "\",\"confidence\":7}]", Now));
        Assert.Equal(1.0, t.Confidence);
        Assert.Equal(140, t.Title.Length);
    }
}

public class ClaudeCliTests
{
    [Fact]
    public void Returns_the_result_text_from_the_json_envelope() =>
        Assert.Equal("{\"plan\":\"x\"}", ClaudeCliBackend.ParseResult("{\"is_error\":false,\"result\":\"{\\\"plan\\\":\\\"x\\\"}\"}", "", 0));

    [Fact]
    public void Token_refresh_collisions_are_reported_as_transient() =>
        Assert.Throws<TransientCliException>(() => ClaudeCliBackend.ParseResult(
            "{\"is_error\":true,\"result\":\"Failed to refresh OAuth token: another Claude Code process is refreshing it\"}", "", 1));

    [Fact]
    public void Signed_out_gets_a_friendly_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ClaudeCliBackend.ParseResult("{\"is_error\":true,\"result\":\"Not logged in - please run /login\"}", "", 1));
        Assert.Contains("sign in", ex.Message);
    }

    [Fact]
    public void Non_json_output_surfaces_stderr()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ClaudeCliBackend.ParseResult("", "boom: bad flag", 2));
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public void Finds_the_newest_version_in_the_nested_layout()
    {
        var root = Path.Combine(Path.GetTempPath(), "anchor-cli-" + Guid.NewGuid().ToString("N"));
        foreach (var (v, h) in new[] { ("2.1.295", "aaa"), ("2.1.299", "bbb"), ("2.1.9", "ccc") })
        {
            Directory.CreateDirectory(Path.Combine(root, v, h));
            File.WriteAllText(Path.Combine(root, v, h, "claude.exe"), "x");
        }
        try { Assert.Equal(Path.Combine(root, "2.1.299", "bbb", "claude.exe"), ClaudeCliBackend.FindUnder(root)); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Locate_prefers_an_explicit_existing_path()
    {
        var f = Path.GetTempFileName();
        try { Assert.Equal(f, ClaudeCliBackend.Locate(f)); }
        finally { File.Delete(f); }
    }
}

public class SettingsValidatorTests
{
    static AnchorSettings Ms(string id, string tenant = "organizations") =>
        new() { Microsoft = { Enabled = true, ClientId = id, Tenant = tenant } };

    [Fact] public void Email_in_the_client_id_field_is_rejected() => Assert.Contains(SettingsValidator.Check(Ms("james.neville@autodesk.com")), p => p.StartsWith("Microsoft: the Application"));
    [Fact] public void A_real_guid_passes() => Assert.Empty(SettingsValidator.Check(Ms("0a1b2c3d-4e5f-6789-abcd-ef0123456789")));
    [Fact] public void A_tenant_guid_passes() => Assert.Empty(SettingsValidator.Check(Ms("0a1b2c3d-4e5f-6789-abcd-ef0123456789", "11111111-2222-3333-4444-555555555555")));
    [Fact] public void A_bad_tenant_is_rejected() => Assert.Contains(SettingsValidator.Check(Ms("0a1b2c3d-4e5f-6789-abcd-ef0123456789", "autodesk.com")), p => p.Contains("Tenant must"));
    [Fact] public void Disabled_connectors_are_not_validated() => Assert.Empty(SettingsValidator.Check(new AnchorSettings { Microsoft = { ClientId = "garbage" } }));

    [Fact]
    public void Google_values_are_checked()
    {
        var bad = new AnchorSettings { Google = { Enabled = true, ClientId = "slimjim", ClientSecret = "hunter2" } };
        Assert.Equal(2, SettingsValidator.Check(bad).Count);
        var good = new AnchorSettings { Google = { Enabled = true, ClientId = "123456789-abc123def.apps.googleusercontent.com", ClientSecret = "GOCSPX-abc" } };
        Assert.Empty(SettingsValidator.Check(good));
    }
}

public class BridgeEventTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 0, 0, TimeSpan.FromHours(-4));

    [Fact]
    public void Parses_events_with_offsets_and_drops_out_of_range_and_blank_ones()
    {
        const string json = """
            [
              {"id":"a","title":"Standup","start":"2026-10-19T13:00:00-04:00","end":"2026-10-19T13:45:00-04:00","location":"Microsoft Teams Meeting","joinUrl":"https://teams.microsoft.com/l/x"},
              {"id":"b","title":"Dentist","start":"2026-10-20T08:00:00-04:00","end":"2026-10-20T09:00:00-04:00","location":"12 High St"},
              {"id":"c","title":"Too far","start":"2026-12-01T08:00:00-04:00"},
              {"id":"d","title":"","start":"2026-10-19T10:00:00-04:00"},
              {"id":"e","title":"Bad link","start":"2026-10-19T15:00:00-04:00","joinUrl":"file:///c:/x"}
            ]
            """;
        var ev = Bridge.ParseEvents(json, Now);
        Assert.Equal(["Standup", "Dentist", "Bad link"], ev.Select(e => e.Title));
        Assert.All(ev, e => Assert.Equal(Bridge.EventSource, e.Source));
        Assert.Null(ev[0].Location);                          // "Microsoft Teams Meeting" is not a place
        Assert.Equal("12 High St", ev[1].Location);
        Assert.Equal(TimeSpan.FromMinutes(45), ev[0].End - ev[0].Start);
        Assert.Null(ev[2].JoinUrl);
        Assert.Equal(TimeSpan.FromMinutes(30), ev[2].End - ev[2].Start); // default length
    }

    [Fact]
    public void Garbage_yields_nothing() => Assert.Empty(Bridge.ParseEvents("nope", Now));

    [Fact]
    public void Teams_locations_are_not_in_person()
    {
        var e = new CalendarEvent("x", "S", "Sync", Now, Now.AddHours(1), null, "Microsoft Teams Meeting");
        Assert.False(AssistantEngine.IsInPerson(e));
        Assert.True(AssistantEngine.IsInPerson(e with { Location = "Building 5, Room 12" }));
    }

    [Fact]
    public void Bridge_outlook_events_count_as_work_for_redaction() =>
        Assert.Equal("Busy (work meeting)", Assert.Single(AiPrivacy.Events(
            [new CalendarEvent("x", Bridge.EventSource, "Secret", Now, Now.AddHours(1))], allowWork: false, new HashSet<string>())).Title);
}
