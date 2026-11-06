using System.Buffers.Binary;
using System.Text;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>
/// The zone the app sends (REMOTE 5.3, 8.1). Windows has no TZif files, so the data is generated from <see cref="TimeZoneInfo"/>:
/// read back here, it must give the zone's own offset at every instant the relay labels days in.
/// </summary>
public sealed class RemoteZoneTests
{
    public static TheoryData<string> Zones() =>
        ["America/New_York", "Europe/London", "Asia/Taipei", "Australia/Lord_Howe", "Asia/Kathmandu", "UTC", "America/Sao_Paulo", "Pacific/Chatham"];

    private static readonly string[] UtcIds = ["UTC", "Etc/UTC", "Etc/GMT"];

    private static readonly long FirstSecond = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    private static readonly long EndSecond = new DateTimeOffset(2101, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    [Theory]
    [MemberData(nameof(Zones))]
    public void GeneratedDataGivesTheZonesOffsets(string id)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
        var remote = RemoteZone.Current(zone);
        if (id == "UTC")
        {
            Assert.Contains(remote.Id, UtcIds);
        }
        else
        {
            Assert.Equal(id, remote.Id);
        }

        AssertMatches(zone, remote.Tzif);
    }

    [Fact]
    public void CustomZoneWithRulesIsSentAsUtcWithItsOwnData()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2000, 1, 1), DateTime.MaxValue.Date, TimeSpan.FromMinutes(30),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test Rules +3", TimeSpan.FromHours(3), "Test Rules", "Test Standard", "Test Daylight", [rule]);
        var remote = RemoteZone.Current(zone);
        Assert.Equal("UTC", remote.Id);
        Assert.Equal("UTC", RemoteZone.IanaId(zone));
        var tzif = AssertMatches(zone, remote.Tzif);
        Assert.Contains(tzif.Types, t => t.Offset == 3 * 3600 && !t.Dst);
        Assert.Contains(tzif.Types, t => t.Offset == (3 * 3600) + 1800 && t.Dst);
        Assert.Equal(2 * 101, tzif.Transitions.Count(t => t >= new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()));
    }

    [Fact]
    public void FixedZonesHaveOneTypeAndNoTransitions()
    {
        foreach (var (offset, id) in new[] { (1, "Etc/GMT-1"), (-5, "Etc/GMT+5"), (14, "Etc/GMT-14"), (-12, "Etc/GMT+12") })
        {
            var zone = TimeZoneInfo.CreateCustomTimeZone($"Test Fixed {offset}", TimeSpan.FromHours(offset), "Test", "Test");
            var remote = RemoteZone.Current(zone);
            Assert.Equal(id, remote.Id);
            var tzif = AssertMatches(zone, remote.Tzif);
            Assert.Empty(tzif.Transitions);
            Assert.Equal(offset * 3600, Assert.Single(tzif.Types).Offset);
        }

        var odd = TimeZoneInfo.CreateCustomTimeZone("Test Fixed +5:45", new TimeSpan(5, 45, 0), "Test", "Test");
        Assert.Equal("UTC", RemoteZone.Current(odd).Id);
        Assert.Equal((5 * 3600) + (45 * 60), Assert.Single(Read(RemoteZone.Current(odd).Tzif).Types).Offset);
        Assert.Contains(RemoteZone.IanaId(TimeZoneInfo.CreateCustomTimeZone("Test Fixed 0", TimeSpan.Zero, "Test", "Test")), UtcIds);
    }

    [Fact]
    public void LordHoweHasItsHalfHourDaylightTime()
    {
        var tzif = Read(RemoteZone.Current(TimeZoneInfo.FindSystemTimeZoneById("Australia/Lord_Howe")).Tzif);
        Assert.Contains(tzif.Types, t => t.Offset == 37_800 && !t.Dst);
        Assert.Contains(tzif.Types, t => t.Offset == 39_600 && t.Dst);
        Assert.Contains(tzif.Types, t => t.Name == "+1030");
        Assert.Contains(tzif.Types, t => t.Name == "+11");
    }

    [Fact]
    public void KathmanduHasItsQuarterHour()
    {
        var tzif = Read(RemoteZone.Current(TimeZoneInfo.FindSystemTimeZoneById("Asia/Kathmandu")).Tzif);
        var now = tzif.OffsetAt(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
        Assert.Equal(20_700, now);
        Assert.Contains(tzif.Types, t => t.Name == "+0545");
    }

    [Fact]
    public void DataIsATzifVersion2File()
    {
        var data = RemoteZone.Current(TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).Tzif;
        Assert.Equal("TZif2"u8.ToArray(), data[..5]);
        Assert.True(RemoteZone.IsTzif(data));
        var tzif = Read(data);
        Assert.Equal('2', tzif.Version);
        Assert.Equal("\n\n"u8.ToArray(), tzif.Footer);
        Assert.True(tzif.Transitions.Length > 2 * 120, $"{tzif.Transitions.Length} transitions");
    }

    [Fact]
    public void CreateValidatesIdsAndData()
    {
        var data = RemoteZone.Current(TimeZoneInfo.Utc).Tzif;
        foreach (var id in new[] { "UTC", "America/New_York", "Etc/GMT+5", "Etc/GMT-14", "America/Argentina/Buenos_Aires", "EST5EDT", new string('a', 64) })
        {
            Assert.True(RemoteZone.Create(id, data) is { } zone && zone.Id == id, id);
        }

        foreach (var id in new[] { "", "Bad Zone", "../etc/passwd", "a..b", "Zone;rm", "Zone\n", "時區", new string('a', 65), "Zone$(id)", "C:\\Zone" })
        {
            Assert.True(RemoteZone.Create(id, data) is null, id);
        }

        Assert.Null(RemoteZone.Create("UTC", "nope"u8.ToArray()));
        Assert.Null(RemoteZone.Create("UTC", data[..43]));
        Assert.Null(RemoteZone.Create("UTC", [.. "TZIF"u8, .. data[4..]]));
        Assert.NotNull(RemoteZone.Create("UTC", data[..44]));
        Assert.Throws<ArgumentNullException>(() => RemoteZone.Create(null!, data));
    }

    [Fact]
    public void IanaIdIsPlausible()
    {
        Assert.Equal("America/New_York", RemoteZone.IanaId(TimeZoneInfo.FindSystemTimeZoneById("America/New_York")));
        Assert.Equal("America/New_York", RemoteZone.IanaId(TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")));
        Assert.Equal("Asia/Taipei", RemoteZone.IanaId(TimeZoneInfo.FindSystemTimeZoneById("Taipei Standard Time")));
        Assert.Equal("Europe/London", RemoteZone.IanaId(TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time")));
        Assert.Contains(RemoteZone.IanaId(TimeZoneInfo.Utc), UtcIds);

        var local = RemoteZone.IanaId(TimeZoneInfo.Local);
        Assert.NotNull(RemoteZone.Create(local, RemoteZone.Current().Tzif));
        Assert.Equal(local, RemoteZone.Current().Id);
        if (local != "UTC")
        {
            var named = TimeZoneInfo.FindSystemTimeZoneById(local);
            var instant = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
            Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(instant), named.GetUtcOffset(instant));
        }
    }

    [Fact]
    public void CurrentFollowsAChangedZone()
    {
        var taipei = RemoteZone.Current(TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei"));
        var london = RemoteZone.Current(TimeZoneInfo.FindSystemTimeZoneById("Europe/London"));
        Assert.Equal("Asia/Taipei", taipei.Id);
        Assert.Equal("Europe/London", london.Id);
        Assert.NotEqual(taipei.Tzif, london.Tzif);
        Assert.Equal(taipei.Tzif, RemoteZone.Current(TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei")).Tzif);
    }

    /// <summary>The data read back gives <paramref name="zone"/>'s offset at noon and just after midnight of every day of 2024–2026,
    /// every hour of those years, around every transition, and in 1970 and 2099.</summary>
    private static Tzif AssertMatches(TimeZoneInfo zone, byte[] data)
    {
        var tzif = Read(data);
        var checks = 0;

        void Check(long second, string what)
        {
            if (second < FirstSecond || second >= EndSecond)
            {
                return;
            }

            var instant = DateTimeOffset.FromUnixTimeSeconds(second);
            var expected = (int)zone.GetUtcOffset(instant).TotalSeconds;
            var actual = tzif.OffsetAt(second);
            if (expected != actual)
            {
                Assert.Fail($"{zone.Id} at {instant:u} ({what}): TZif gives {actual} s, TimeZoneInfo {expected} s");
            }

            checks++;
        }

        for (var day = new DateOnly(2024, 1, 1); day <= new DateOnly(2026, 12, 31); day = day.AddDays(1))
        {
            foreach (var time in new[] { new TimeOnly(0, 30), new TimeOnly(12, 0) })
            {
                var local = day.ToDateTime(time, DateTimeKind.Unspecified);
                if (!zone.IsInvalidTime(local))
                {
                    Check(new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero).ToUnixTimeSeconds(), $"local {time}");
                }
            }
        }

        for (var second = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
             second < new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(); second += 3600)
        {
            Check(second, "hourly");
        }

        foreach (var transition in tzif.Transitions)
        {
            foreach (var delta in new long[] { -3600, -60, -1, 0, 1, 60, 3600 })
            {
                Check(transition + delta, $"transition {delta:+0;-0;0}");
            }
        }

        foreach (var instant in new[]
        {
            new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(1970, 1, 1, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(1970, 7, 1, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(1970, 12, 31, 23, 59, 59, TimeSpan.Zero),
            new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2099, 1, 15, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2099, 7, 1, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2099, 12, 31, 23, 59, 59, TimeSpan.Zero),
            new DateTimeOffset(2100, 12, 31, 23, 59, 59, TimeSpan.Zero),
        })
        {
            Check(instant.ToUnixTimeSeconds(), "edge year");
        }

        Assert.True(checks > 26_000, $"{checks} checks");
        return tzif;
    }

    private sealed record TzifType(int Offset, bool Dst, string Name);

    private sealed record Tzif(char Version, long[] Transitions, byte[] Indices, TzifType[] Types, byte[] Footer)
    {
        /// <summary>RFC 8536: the type of the last transition at or before <paramref name="second"/>, type 0 before the first.</summary>
        public int OffsetAt(long second)
        {
            var index = Array.BinarySearch(Transitions, second);
            var position = index >= 0 ? index : ~index - 1;
            return position < 0 ? Types[0].Offset : Types[Indices[position]].Offset;
        }
    }

    /// <summary>A small RFC 8536 reader: skips the version 1 block, reads the version 2 block with 64-bit times.</summary>
    private static Tzif Read(byte[] data)
    {
        Assert.True(data.Length >= 44, "shorter than a header");
        Assert.Equal("TZif"u8.ToArray(), data[..4]);
        var v1 = Counts(data, 0);
        Assert.Equal((0, 0, 0, 0, 1, 4), v1);
        var at = 44 + (v1.Time * 5) + (v1.Type * 6) + v1.Chars + (v1.Leap * 8) + v1.Std + v1.Ut;
        Assert.Equal("TZif"u8.ToArray(), data[at..(at + 4)]);
        var version = (char)data[at + 4];
        Assert.True(version >= '2', $"version {version}");
        var counts = Counts(data, at);
        Assert.Equal((0, 0, 0), (counts.Ut, counts.Std, counts.Leap));
        Assert.InRange(counts.Type, 1, 255);
        at += 44;

        var transitions = new long[counts.Time];
        for (var i = 0; i < counts.Time; i++)
        {
            transitions[i] = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(at + (i * 8), 8));
        }

        at += counts.Time * 8;
        var indices = data[at..(at + counts.Time)];
        at += counts.Time;
        var raw = new (int Offset, bool Dst, int Name)[counts.Type];
        for (var i = 0; i < counts.Type; i++)
        {
            var type = data.AsSpan(at + (i * 6), 6);
            Assert.InRange(type[4], (byte)0, (byte)1);
            raw[i] = (BinaryPrimitives.ReadInt32BigEndian(type), type[4] == 1, type[5]);
        }

        at += counts.Type * 6;
        var chars = data[at..(at + counts.Chars)];
        at += counts.Chars;
        var footer = data[at..];

        for (var i = 1; i < transitions.Length; i++)
        {
            Assert.True(transitions[i] > transitions[i - 1], $"transition {i} is not after the previous one");
        }

        Assert.All(indices, index => Assert.InRange(index, (byte)0, (byte)(counts.Type - 1)));
        var types = raw.Select(t =>
        {
            Assert.InRange(t.Name, 0, chars.Length - 1);
            var end = Array.IndexOf(chars, (byte)0, t.Name);
            Assert.True(end > t.Name, "designation is not NUL-terminated");
            var name = Encoding.ASCII.GetString(chars, t.Name, end - t.Name);
            Assert.InRange(name.Length, 3, 6);
            Assert.All(name, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '+' or '-', name));
            return new TzifType(t.Offset, t.Dst, name);
        }).ToArray();
        Assert.True(footer.Length >= 2 && footer[0] == '\n' && footer[^1] == '\n', "footer is not newline-enclosed");
        return new Tzif(version, transitions, indices, types, footer);
    }

    private static (int Ut, int Std, int Leap, int Time, int Type, int Chars) Counts(byte[] data, int at)
    {
        int Count(int index) => BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at + 20 + (index * 4), 4));
        return (Count(0), Count(1), Count(2), Count(3), Count(4), Count(5));
    }
}
