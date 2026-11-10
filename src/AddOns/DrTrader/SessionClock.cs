// GOF - trading-session windows (Asia / London / New York / COMEX active / Overnight), DST-aware,
// relative volume by elapsed time, and the user-defined economic news blackout filter.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace NinjaTrader.NinjaScript.AddOns.DrTrader
{
    public static class TimeZoneResolver
    {
        private static readonly string[,] Map = new string[,]
        {
            { "Eastern Standard Time", "America/New_York" },
            { "Central Standard Time", "America/Chicago" },
            { "GMT Standard Time", "Europe/London" },
            { "Tokyo Standard Time", "Asia/Tokyo" },
            { "China Standard Time", "Asia/Shanghai" },
            { "Singapore Standard Time", "Asia/Singapore" },
            { "W. Europe Standard Time", "Europe/Berlin" },
            { "UTC", "Etc/UTC" }
        };

        /// <summary>Accepts Windows or IANA ids. Falls back to UTC (and reports it) if nothing matches.</summary>
        public static TimeZoneInfo Find(string id, out bool found)
        {
            found = true;
            if (string.IsNullOrEmpty(id)) { found = false; return TimeZoneInfo.Utc; }
            TimeZoneInfo z = TryFind(id);
            if (z != null) return z;
            for (int i = 0; i < Map.GetLength(0); i++)
            {
                if (string.Equals(Map[i, 0], id, StringComparison.OrdinalIgnoreCase)) { z = TryFind(Map[i, 1]); if (z != null) return z; }
                if (string.Equals(Map[i, 1], id, StringComparison.OrdinalIgnoreCase)) { z = TryFind(Map[i, 0]); if (z != null) return z; }
            }
            found = false;
            return TimeZoneInfo.Utc;
        }

        private static TimeZoneInfo TryFind(string id)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception) { return null; }
        }

        public static DateTime Convert(DateTime t, TimeZoneInfo from, TimeZoneInfo to)
        {
            if (from == null || to == null || from.Id == to.Id) return t;
            DateTime u = DateTime.SpecifyKind(t, DateTimeKind.Unspecified);
            try { return TimeZoneInfo.ConvertTime(u, from, to); }
            catch (ArgumentException)
            {
                // invalid local time (inside a DST spring-forward gap): shift by one hour and convert
                return TimeZoneInfo.ConvertTime(u.AddHours(1), from, to).AddHours(-1);
            }
        }
    }

    public sealed class SessionWindowDef
    {
        public string Name;
        public TimeSpan Start;
        public TimeSpan End;
        public TimeZoneInfo Zone;
        public bool Enabled = true;
        public bool CrossesMidnight { get { return Start > End; } }

        /// <summary>Parses "HH:mm-HH:mm". Returns null on failure.</summary>
        public static SessionWindowDef Parse(string name, string range, string zoneId, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrEmpty(range)) { error = name + ": empty range"; return null; }
            string[] parts = range.Split('-');
            if (parts.Length != 2) { error = name + ": expected HH:mm-HH:mm"; return null; }
            TimeSpan a, b;
            if (!TimeSpan.TryParseExact(parts[0].Trim(), "hh\\:mm", CultureInfo.InvariantCulture, out a) ||
                !TimeSpan.TryParseExact(parts[1].Trim(), "hh\\:mm", CultureInfo.InvariantCulture, out b))
            { error = name + ": bad time format"; return null; }
            bool found;
            SessionWindowDef d = new SessionWindowDef();
            d.Name = name;
            d.Start = a;
            d.End = b;
            d.Zone = TimeZoneResolver.Find(zoneId, out found);
            if (!found) error = name + ": time zone '" + zoneId + "' not found, UTC used";
            return d;
        }
    }

    public struct WindowInstance
    {
        public SessionWindowDef Def;
        public DateTime LocalStart;   // start of this occurrence, in the window's own zone
        public DateTime LocalTime;    // the queried time in the window's zone
        public string Key;
        public double ElapsedMinutes { get { return (LocalTime - LocalStart).TotalMinutes; } }
    }

    /// <summary>
    /// Maps platform timestamps (NinjaTrader bar/tick times are in the platform time zone) to named windows.
    /// Each window is defined in its OWN time zone, so DST in New York and London is handled independently
    /// (e.g. the 2-3 weeks per year when US and UK clocks are not aligned).
    /// </summary>
    public sealed class SessionClock
    {
        public TimeZoneInfo PlatformZone = TimeZoneInfo.Local;
        public readonly List<SessionWindowDef> Windows = new List<SessionWindowDef>();
        public SessionWindowDef Rth;
        public readonly List<string> Errors = new List<string>();

        private long cacheMinute = long.MinValue;
        private readonly List<WindowInstance> cache = new List<WindowInstance>();
        private bool cacheRth;

        public void AddWindow(string name, string range, string zoneId, bool enabled)
        {
            string err;
            SessionWindowDef d = SessionWindowDef.Parse(name, range, zoneId, out err);
            if (!string.IsNullOrEmpty(err)) Errors.Add(err);
            if (d == null) return;
            d.Enabled = enabled;
            Windows.Add(d);
            cacheMinute = long.MinValue;
        }

        public void SetRth(string range, string zoneId)
        {
            string err;
            Rth = SessionWindowDef.Parse("RTH", range, zoneId, out err);
            if (!string.IsNullOrEmpty(err)) Errors.Add(err);
            cacheMinute = long.MinValue;
        }

        public static bool TryInstance(SessionWindowDef d, DateTime local, out WindowInstance inst)
        {
            inst = new WindowInstance();
            TimeSpan tod = local.TimeOfDay;
            DateTime start;
            if (!d.CrossesMidnight)
            {
                if (tod < d.Start || tod >= d.End) return false;
                start = local.Date + d.Start;
            }
            else
            {
                if (tod >= d.Start) start = local.Date + d.Start;
                else if (tod < d.End) start = local.Date.AddDays(-1) + d.Start;
                else return false;
            }
            inst.Def = d;
            inst.LocalStart = start;
            inst.LocalTime = local;
            inst.Key = d.Name + "|" + start.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>Active windows at the given platform time. The returned list is reused (do not keep it).</summary>
        public List<WindowInstance> GetActive(DateTime platformTime, out bool inRth)
        {
            long minute = platformTime.Ticks / TimeSpan.TicksPerMinute;
            if (minute == cacheMinute)
            {
                // refresh LocalTime so elapsed minutes stay exact within the minute
                for (int i = 0; i < cache.Count; i++)
                {
                    WindowInstance w = cache[i];
                    w.LocalTime = TimeZoneResolver.Convert(platformTime, PlatformZone, w.Def.Zone);
                    cache[i] = w;
                }
                inRth = cacheRth;
                return cache;
            }
            cache.Clear();
            for (int i = 0; i < Windows.Count; i++)
            {
                SessionWindowDef d = Windows[i];
                if (!d.Enabled) continue;
                DateTime local = TimeZoneResolver.Convert(platformTime, PlatformZone, d.Zone);
                WindowInstance inst;
                if (TryInstance(d, local, out inst)) cache.Add(inst);
            }
            cacheRth = true;
            if (Rth != null)
            {
                WindowInstance r;
                cacheRth = TryInstance(Rth, TimeZoneResolver.Convert(platformTime, PlatformZone, Rth.Zone), out r);
            }
            cacheMinute = minute;
            inRth = cacheRth;
            return cache;
        }

        public string ActiveNames(DateTime platformTime)
        {
            bool rth;
            List<WindowInstance> l = GetActive(platformTime, out rth);
            if (l.Count == 0) return "-";
            string s = string.Empty;
            for (int i = 0; i < l.Count; i++) s += (i > 0 ? "+" : string.Empty) + l[i].Def.Name;
            return s;
        }
    }

    /// <summary>
    /// Relative volume = cumulative volume so far in the current period / average cumulative volume of the
    /// previous N periods at the same elapsed time. Uses only completed past periods (no look-ahead).
    /// </summary>
    public sealed class RelativeVolumeTracker
    {
        private readonly int slotMinutes;
        private readonly int lookback;
        private readonly List<long[]> history = new List<long[]>();
        private List<long> current = new List<long>();
        private string currentKey;

        public RelativeVolumeTracker(int slotMinutes, int lookbackPeriods)
        {
            this.slotMinutes = Math.Max(1, slotMinutes);
            lookback = Math.Max(1, lookbackPeriods);
        }

        public int HistoryCount { get { return history.Count; } }

        public void Begin(string key)
        {
            if (key == currentKey) return;
            if (currentKey != null && current.Count > 0)
            {
                history.Add(current.ToArray());
                while (history.Count > lookback) history.RemoveAt(0);
            }
            current = new List<long>();
            currentKey = key;
        }

        public void Add(double elapsedMinutes, long volume)
        {
            int slot = Math.Max(0, (int)(elapsedMinutes / slotMinutes));
            long last = current.Count > 0 ? current[current.Count - 1] : 0;
            while (current.Count <= slot) current.Add(last);
            for (int i = slot; i < current.Count; i++) current[i] += volume;
        }

        public long CurrentCumulative { get { return current.Count > 0 ? current[current.Count - 1] : 0; } }

        public double Rvol(double elapsedMinutes)
        {
            if (history.Count < 2) return double.NaN;
            int slot = Math.Max(0, (int)(elapsedMinutes / slotMinutes));
            double sum = 0; int c = 0;
            for (int i = 0; i < history.Count; i++)
            {
                long[] h = history[i];
                if (h.Length == 0) continue;
                sum += h[Math.Min(slot, h.Length - 1)];
                c++;
            }
            if (c == 0 || sum <= 0) return double.NaN;
            long cur = current.Count == 0 ? 0 : current[Math.Min(slot, current.Count - 1)];
            return cur / (sum / c);
        }
    }

    public sealed class NewsEvent
    {
        public DateTime PlatformTime;
        public string Label;
        public DateTime BlackoutStart;
        public DateTime BlackoutEnd;
    }

    /// <summary>
    /// User-defined news blackout. There is NO built-in economic calendar: events come from the indicator
    /// property (text) and/or a CSV file the user maintains, each line "yyyy-MM-dd HH:mm,LABEL" in EventZone.
    /// </summary>
    public sealed class NewsFilter
    {
        public readonly List<NewsEvent> Events = new List<NewsEvent>();
        public readonly List<string> Errors = new List<string>();
        public int MinutesBefore = 10;
        public int MinutesAfter = 20;

        public void Load(string inlineText, string filePath, TimeZoneInfo eventZone, TimeZoneInfo platformZone)
        {
            Events.Clear();
            Errors.Clear();
            if (!string.IsNullOrEmpty(inlineText))
                foreach (string line in inlineText.Split(new char[] { ';', '\n', '\r', '|' }, StringSplitOptions.RemoveEmptyEntries))
                    ParseLine(line, eventZone, platformZone);
            if (!string.IsNullOrEmpty(filePath))
            {
                try
                {
                    if (File.Exists(filePath))
                        foreach (string line in File.ReadAllLines(filePath)) ParseLine(line, eventZone, platformZone);
                    else Errors.Add("News file not found: " + filePath);
                }
                catch (Exception ex) { Errors.Add("News file error: " + ex.Message); }
            }
            Events.Sort(delegate(NewsEvent a, NewsEvent b) { return a.PlatformTime.CompareTo(b.PlatformTime); });
        }

        private void ParseLine(string line, TimeZoneInfo eventZone, TimeZoneInfo platformZone)
        {
            string l = line.Trim();
            if (l.Length == 0 || l.StartsWith("#")) return;
            string[] parts = l.Split(',');
            DateTime t;
            if (!DateTime.TryParseExact(parts[0].Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
            {
                Errors.Add("Bad news line: " + l);
                return;
            }
            NewsEvent e = new NewsEvent();
            e.PlatformTime = TimeZoneResolver.Convert(t, eventZone, platformZone);
            e.Label = parts.Length > 1 ? parts[1].Trim() : "NEWS";
            e.BlackoutStart = e.PlatformTime.AddMinutes(-MinutesBefore);
            e.BlackoutEnd = e.PlatformTime.AddMinutes(MinutesAfter);
            Events.Add(e);
        }

        /// <summary>Returns the event whose blackout window contains t, or null.</summary>
        public NewsEvent ActiveAt(DateTime platformTime)
        {
            for (int i = 0; i < Events.Count; i++)
            {
                NewsEvent e = Events[i];
                if (platformTime >= e.BlackoutStart && platformTime <= e.BlackoutEnd) return e;
                if (e.BlackoutStart > platformTime) break;
            }
            return null;
        }
    }
}
