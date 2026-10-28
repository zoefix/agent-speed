using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Tests.Reporting;

/// <summary>METRICS 11.5 against report-vectors.json <c>scheduler</c>: the simulator's timelines with instant sends and scripted outcomes.</summary>
public sealed class ReportSchedulerTests
{
    private sealed record Simulation(List<string> Delivered, List<int> Lost, long Built, long Discarded, long Increments);

    /// <summary>Runs one case like <c>simulate_scheduler</c>: every 500 ms tick, increments into a ledger, outcomes per seq.</summary>
    private static Simulation Simulate(JsonElement item)
    {
        int Config(string name, int fallback) =>
            item.TryGetProperty("config", out var config) && config.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;
        var scheduler = new ReportScheduler(
            new ReportLimits(Config("intervalSeconds", 5), Config("heartbeatSeconds", 60), Config("maxAgeSeconds", 30)), jitter: () => 1);
        var increments = new Dictionary<long, long>();
        long incrementTotal = 0;
        if (item.TryGetProperty("increments", out var incrementList))
        {
            foreach (var spec in incrementList.EnumerateArray())
            {
                for (long t = spec.GetProperty("from").GetInt64(); t <= spec.GetProperty("to").GetInt64(); t += spec.GetProperty("step").GetInt64())
                {
                    increments[t] = increments.GetValueOrDefault(t) + spec.GetProperty("output").GetInt64();
                }
            }
        }

        var events = new Dictionary<long, List<ReportEvent>>();
        if (item.TryGetProperty("events", out var eventList))
        {
            foreach (var pair in eventList.EnumerateArray())
            {
                var e = pair[1].GetString() switch
                {
                    "pause" => ReportEvent.Pause,
                    "resume" => ReportEvent.Resume,
                    "networkOff" => ReportEvent.NetworkOff,
                    _ => ReportEvent.NetworkOn,
                };
                var at = pair[0].GetInt64();
                if (!events.TryGetValue(at, out var list))
                {
                    events[at] = list = [];
                }

                list.Add(e);
            }
        }

        var outcomes = new Dictionary<int, List<string>>();
        if (item.TryGetProperty("outcomes", out var outcomeMap))
        {
            foreach (var property in outcomeMap.EnumerateObject())
            {
                outcomes[int.Parse(property.Name, System.Globalization.CultureInfo.InvariantCulture)] =
                    property.Value.EnumerateArray().Select(v => v.GetString()!).ToList();
            }
        }

        var activity = item.TryGetProperty("activity", out var activityList)
            ? activityList.EnumerateArray().Select(r => (r[0].GetInt64(), r[1].GetInt64())).ToList()
            : [];
        var attempts = new Dictionary<int, int>();
        var outputs = new Dictionary<int, long>();
        long ledger = 0, built = 0, discarded = 0;
        var delivered = new List<string>();
        var lost = new List<int>();

        ReportAttemptOutcome Outcome(ReportTicket ticket)
        {
            var queue = outcomes.GetValueOrDefault(ticket.Seq) ?? [];
            var index = attempts.GetValueOrDefault(ticket.Seq);
            attempts[ticket.Seq] = index + 1;
            var name = index < queue.Count ? queue[index] : "ok";
            if (name == "ok")
            {
                return new ReportAttemptOutcome.Delivered(null);
            }

            return name.StartsWith("rateLimited:", StringComparison.Ordinal)
                ? new ReportAttemptOutcome.RateLimited(int.Parse(name[12..], System.Globalization.CultureInfo.InvariantCulture))
                : new ReportAttemptOutcome.Transient();
        }

        void Apply(IEnumerable<ReportAction> actions, long t)
        {
            foreach (var action in actions)
            {
                switch (action)
                {
                    case ReportAction.Discard { KeepingTick: false }:
                        discarded += ledger;
                        ledger = 0;
                        break;
                    case ReportAction.Build build:
                        outputs[build.Ticket.Seq] = ledger;
                        built += ledger;
                        ledger = 0;
                        break;
                    case ReportAction.Send send:
                        Apply(scheduler.Finish(send.Ticket, Outcome(send.Ticket), t), t);
                        break;
                    case ReportAction.Delivered done:
                        var ticket = done.Ticket;
                        delivered.Add($"{ticket.Seq},{ticket.BuiltAt},{ticket.WindowMs},{ReportBody.ActivityName(ticket.Activity)},{outputs.GetValueOrDefault(ticket.Seq, -1)}");
                        break;
                    case ReportAction.Lost gone:
                        lost.Add(gone.Ticket.Seq);
                        break;
                }
            }
        }

        foreach (var range in item.GetProperty("ticks").EnumerateArray())
        {
            for (var t = range[0].GetInt64(); t <= range[1].GetInt64(); t += 500)
            {
                var active = activity.Any(a => a.Item1 <= t && t <= a.Item2);
                var actions = scheduler.Tick(t, active, blocked: false, events.GetValueOrDefault(t));
                if (actions.Any(a => a is ReportAction.Discard { KeepingTick: true }))
                {
                    discarded += ledger;
                    ledger = 0;
                }

                var increment = increments.GetValueOrDefault(t);
                ledger += increment;
                incrementTotal += increment;
                Apply(actions.Where(a => a is not ReportAction.Discard { KeepingTick: true }).ToList(), t);
            }
        }

        lost.Sort();
        return new Simulation(delivered, lost, built, discarded + ledger, incrementTotal);
    }

    [Fact]
    public void VectorTimelines()
    {
        var cases = ReportSpec.Vector("scheduler").EnumerateArray().ToList();
        Assert.Equal(11, cases.Count);
        foreach (var item in cases)
        {
            var name = item.Text("name");
            var expect = item.GetProperty("expect");
            var delivered = expect.GetProperty("delivered").EnumerateArray()
                .Select(row => string.Join(',', row.EnumerateArray().Select(Cell))).ToList();
            var lost = expect.TryGetProperty("lost", out var lostList) ? lostList.EnumerateArray().Select(v => v.GetInt32()).ToList() : [];
            var got = Simulate(item);
            Assert.True(delivered.SequenceEqual(got.Delivered),
                $"{name}\nexpected: {string.Join(" | ", delivered)}\ngot:      {string.Join(" | ", got.Delivered)}");
            Assert.True(lost.SequenceEqual(got.Lost), $"{name} lost: expected [{string.Join(',', lost)}], got [{string.Join(',', got.Lost)}]");
            Assert.True(got.Built + got.Discarded == got.Increments, $"{name}: exactly once ({got.Built} + {got.Discarded} != {got.Increments})");
        }
    }

    private static string Cell(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    /// <summary>Rate guarantees: nothing while blocked.</summary>
    [Fact]
    public void BlockedSendsNothing()
    {
        var scheduler = new ReportScheduler(ReportLimits.Standard, jitter: () => 1);
        var sends = 0;
        for (long t = 0; t <= 60_000; t += 500)
        {
            sends += scheduler.Tick(t, active: true, blocked: true).Count(a => a is ReportAction.Send);
        }

        Assert.Equal(0, sends);
        Assert.Equal(0, scheduler.Seq);
    }

    /// <summary>An attempt still on the wire is never doubled: the next due report waits and replaces older waiting ones.</summary>
    [Fact]
    public void OneRequestInFlight()
    {
        var scheduler = new ReportScheduler(ReportLimits.Standard, jitter: () => 1);
        var inFlight = new List<ReportTicket>();
        var lost = new List<int>();
        for (long t = 0; t <= 19_500; t += 500)
        {
            foreach (var action in scheduler.Tick(t, active: true, blocked: false))
            {
                if (action is ReportAction.Send send)
                {
                    inFlight.Add(send.Ticket);
                }
                else if (action is ReportAction.Lost gone)
                {
                    lost.Add(gone.Ticket.Seq);
                }
            }
        }

        Assert.Equal([1], inFlight.Select(t => t.Seq));
        Assert.Equal([2], lost);
        var actions = scheduler.Finish(inFlight[0], new ReportAttemptOutcome.Delivered(10), 19_500);
        Assert.Contains(new ReportAction.Delivered(inFlight[0]), actions);
        Assert.Contains(actions, a => a is ReportAction.Send { Ticket.Seq: 3 });
        Assert.Equal(10_000, scheduler.IntervalMs);
        Assert.Equal(60_000, scheduler.HeartbeatMs);
    }

    /// <summary>A user's pause sends one leaving report with a single attempt; turning off does the same.</summary>
    [Fact]
    public void LeavingIsSingleAttempt()
    {
        var scheduler = new ReportScheduler(ReportLimits.Standard, jitter: () => 1);
        for (long t = 0; t <= 6_000; t += 500)
        {
            foreach (var send in scheduler.Tick(t, active: true, blocked: false).OfType<ReportAction.Send>().ToList())
            {
                scheduler.Finish(send.Ticket, new ReportAttemptOutcome.Delivered(null), t);
            }
        }

        var actions = scheduler.Tick(6_500, active: true, blocked: false, [ReportEvent.Stop]);
        var leaving = actions.OfType<ReportAction.Send>().Select(s => s.Ticket).Single();
        Assert.Equal(ReportActivity.Leaving, leaving.Activity);
        Assert.True(leaving.Single);
        var after = scheduler.Finish(leaving, new ReportAttemptOutcome.Transient(), 6_500);
        Assert.Equal([new ReportAction.Lost(leaving)], after);
        Assert.All(scheduler.Tick(7_000, active: true, blocked: false), a => Assert.Equal(new ReportAction.Discard(false), a));
        Assert.True(scheduler.Stopped);
        scheduler.StartRun();
        Assert.False(scheduler.Stopped);
        Assert.Equal(0, scheduler.Seq);
    }

    [Fact]
    public void NonceReplayedIsAttemptedAgainOnce()
    {
        var scheduler = new ReportScheduler(ReportLimits.Standard, jitter: () => 1);
        ReportTicket? ticket = null;
        for (long t = 0; t <= 5_000; t += 500)
        {
            foreach (var send in scheduler.Tick(t, active: true, blocked: false).OfType<ReportAction.Send>())
            {
                ticket = send.Ticket;
            }
        }

        Assert.NotNull(ticket);
        Assert.Equal([new ReportAction.Send(ticket.Value)], scheduler.Finish(ticket.Value, new ReportAttemptOutcome.Again(), 5_000));
        Assert.Equal([new ReportAction.Lost(ticket.Value)], scheduler.Finish(ticket.Value, new ReportAttemptOutcome.Again(), 5_000));
    }

    [Fact]
    public void NetworkOffLosesTheOutboxAndQuietSendsLeaving()
    {
        var scheduler = new ReportScheduler(ReportLimits.Standard, jitter: () => 1);
        var sends = new List<ReportTicket>();
        for (long t = 0; t <= 5_000; t += 500)
        {
            sends.AddRange(scheduler.Tick(t, active: true, blocked: false).OfType<ReportAction.Send>().Select(s => s.Ticket));
        }

        Assert.Single(sends);
        Assert.True(scheduler.IsBusy);
        var off = scheduler.Tick(5_500, active: true, blocked: false, [ReportEvent.NetworkOff]);
        Assert.Contains(new ReportAction.Lost(sends[0]), off);
        Assert.False(scheduler.IsBusy);
        Assert.Empty(scheduler.Finish(sends[0], new ReportAttemptOutcome.Delivered(null), 5_600));

        var quiet = new ReportScheduler(ReportLimits.Standard, jitter: () => 1);
        var activities = new List<ReportActivity>();
        for (long t = 0; t <= 700_000; t += 500)
        {
            foreach (var send in quiet.Tick(t, active: t <= 1_000, blocked: false).OfType<ReportAction.Send>().ToList())
            {
                activities.Add(send.Ticket.Activity);
                quiet.Finish(send.Ticket, new ReportAttemptOutcome.Delivered(null), t);
            }
        }

        Assert.Equal(ReportActivity.Leaving, activities[^1]);
        Assert.Equal(1, activities.Count(a => a == ReportActivity.Leaving));
        Assert.Equal(ReportMode.Quiet, quiet.Mode);
        Assert.Null(quiet.NextDueAt);
    }

    [Fact]
    public void BackoffGrowsAndIsCapped()
    {
        var scheduler = new ReportScheduler(ReportLimits.Standard, jitter: () => 1);
        long t = 0;
        var lastBackoff = long.MinValue;
        for (var failures = 0; failures < 12; t += 500)
        {
            foreach (var send in scheduler.Tick(t, active: true, blocked: false).OfType<ReportAction.Send>().ToList())
            {
                scheduler.Finish(send.Ticket, new ReportAttemptOutcome.Rejected(), t);
                failures++;
                if (failures >= 2)
                {
                    var expected = Math.Min(300_000, 5_000L << (failures - 2));
                    Assert.Equal(t + expected, scheduler.BackoffUntil);
                    Assert.True(scheduler.BackoffUntil >= lastBackoff);
                    lastBackoff = scheduler.BackoffUntil;
                }
            }
        }

        Assert.Equal(12, scheduler.Failures);
    }

    [Fact]
    public void OutcomesFollowTheReactionTable()
    {
        static (ReportAttemptOutcome Outcome, ReportReaction Reaction) Classify(int status, string? code, Dictionary<string, string>? headers = null,
            bool pending = false)
        {
            var body = code is null ? [] : Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"" + code + "\",\"serverTime\":1792656931}}");
            return ReportOutcome.Classify(ReportReply.Parse(status, headers ?? [], body, DateTimeOffset.UtcNow), pending);
        }

        Assert.Equal(new ReportAttemptOutcome.Delivered(null), Classify(200, null).Outcome);
        Assert.Equal(new ReportAttemptOutcome.Transient(), Classify(503, null).Outcome);
        Assert.Equal(new ReportAttemptOutcome.RateLimited(5), Classify(503, null, new() { ["Retry-After"] = "2" }).Outcome);
        Assert.Equal(new ReportAttemptOutcome.RateLimited(60), Classify(429, "rate_limited").Outcome);
        Assert.Equal(new ReportAttemptOutcome.RateLimited(3600), Classify(429, "rate_limited", new() { ["retry-after"] = "7200" }).Outcome);
        Assert.Equal(new ReportAttemptOutcome.Transient(), Classify(302, null).Outcome);
        Assert.Equal(new ReportReaction.CorrectClock(1_792_656_931), Classify(401, "timestamp_out_of_window").Reaction);
        Assert.Equal(new ReportAttemptOutcome.Again(), Classify(401, "nonce_replayed").Outcome);
        Assert.Equal(new ReportReaction.RecheckRegistration(), Classify(401, "signature_invalid").Reaction);
        Assert.Equal((new ReportAttemptOutcome.Again(), new ReportReaction.UsePendingKey()), Classify(401, "signature_invalid", pending: true));
        Assert.Equal(new ReportReaction.NeedsUser(ReportProblem.ApiKey), Classify(401, "api_key_required").Reaction);
        Assert.Equal(new ReportReaction.NeedsUser(ReportProblem.Forbidden), Classify(403, null).Reaction);
        Assert.Equal(new ReportReaction.ReRegister(), Classify(404, "unknown_device").Reaction);
        Assert.Equal(new ReportReaction.Strike(true), Classify(404, null).Reaction);
        Assert.Equal(new ReportReaction.NewRun(), Classify(409, "seq_conflict").Reaction);
        Assert.Equal(new ReportReaction.NeedsUser(ReportProblem.KeyMismatch), Classify(409, "device_conflict").Reaction);
        Assert.Equal(new ReportReaction.NeedsUser(ReportProblem.Gone), Classify(410, null).Reaction);
        Assert.Equal(new ReportReaction.RefreshDiscovery(true), Classify(400, "version_unsupported").Reaction);
        Assert.Equal(new ReportReaction.RefreshDiscovery(false), Classify(413, null).Reaction);
        Assert.Equal(new ReportReaction.Strike(false), Classify(422, "payload_invalid").Reaction);
        Assert.Equal(new ReportReaction.Strike(false), Classify(418, "teapot_mode").Reaction);
        Assert.Equal(ReportReaction.None, Classify(422, "report_stale").Reaction);
        Assert.Equal(new ReportAttemptOutcome.Transient(), ReportOutcome.Classify(null, hasPendingKey: false).Outcome);
    }

    [Fact]
    public void RetryAfterAcceptsHttpDates()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_792_656_931);
        Assert.Equal(20, ReportReply.RetryAfter("20", now));
        Assert.Equal(60, ReportReply.RetryAfter("Thu, 22 Oct 2026 08:16:31 GMT", now));
        Assert.Equal(0, ReportReply.RetryAfter("Thu, 22 Oct 2026 08:00:00 GMT", now));
        Assert.Null(ReportReply.RetryAfter("soon", now));
    }

    [Fact]
    public void RepliesAreValidatedAndSanitized()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_792_656_931);
        var success = ReportReply.Parse(201, new Dictionary<string, string>(), Encoding.UTF8.GetBytes(
            """{"status":"created","deviceId":"6f1c2a9e-3b4d-4e5f-8a7b-9c0d1e2f3a4b","username":"zoe-mbp","profileUrl":"http://example.com/u","intervalSeconds":2,"rank":0,"transferred":true}"""),
            now);
        Assert.True(success.IsSuccess);
        Assert.Equal("created", success.Result);
        Assert.Equal("6f1c2a9e-3b4d-4e5f-8a7b-9c0d1e2f3a4b", success.DeviceId);
        Assert.Equal("zoe-mbp", success.Username);
        Assert.Null(success.ProfileUrl);
        Assert.Equal(5, success.IntervalSeconds);
        Assert.Null(success.Rank);
        Assert.True(success.Transferred);

        var profile = ReportReply.Parse(200, new Dictionary<string, string>(),
            Encoding.UTF8.GetBytes("""{"status":"accepted","profileUrl":"https://example.com/u/1","intervalSeconds":900,"rank":12}"""), now);
        Assert.Equal("https://example.com/u/1", profile.ProfileUrl);
        Assert.Equal(300, profile.IntervalSeconds);
        Assert.Equal(12, profile.Rank);

        var unknown = ReportReply.Parse(200, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("""{"status":"hooray","deviceId":"X"}"""), now);
        Assert.Null(unknown.Result);
        Assert.Null(unknown.DeviceId);

        var message = new string('m', 300) + "\u0007\u202E";
        var error = ReportReply.Parse(429, new Dictionary<string, string>(), Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { error = new { code = "rate_limited", message, retryAfterSeconds = 20, field = "/a\u0001b", reason = "x", supported = new[] { 1, 2 } } })),
            now);
        Assert.Equal("rate_limited", error.Code);
        Assert.Equal(20, error.RetryAfterSeconds);
        Assert.Equal(200, error.LogMessage!.Length);
        Assert.Equal("/ab", error.Field);
        Assert.Equal([1, 2], error.Supported);

        var badCode = ReportReply.Parse(400, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("""{"error":{"code":"Bad-Code"}}"""), now);
        Assert.Null(badCode.Code);
        Assert.Null(ReportReply.Parse(400, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("<html>"), now).Code);
        Assert.Equal(30, ReportReply.Parse(503, new Dictionary<string, string> { ["Retry-After"] = "30" },
            Encoding.UTF8.GetBytes("""{"error":{"code":"x","retryAfterSeconds":99}}"""), now).RetryAfterSeconds);
    }
}
