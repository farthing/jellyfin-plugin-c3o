using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Jellyfin.Plugin.C3o;

/// <summary>Outcome of the last attempt for one video and language.</summary>
public static class AttemptStatus
{
    /// <summary>A subtitle was downloaded and saved.</summary>
    public const string Downloaded = "Downloaded";

    /// <summary>The providers answered but had nothing usable.</summary>
    public const string NotFound = "NotFound";

    /// <summary>Candidates existed but every download failed.</summary>
    public const string Failed = "Failed";
}

/// <summary>One remembered attempt.</summary>
public class StateEntry
{
    /// <summary>Gets or sets the outcome, one of <see cref="AttemptStatus"/>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets when the attempt happened (UTC).</summary>
    public DateTime Utc { get; set; }

    /// <summary>Gets or sets the three-letter language.</summary>
    public string Language { get; set; } = string.Empty;
}

/// <summary>One line of the recent-activity list on the settings page.</summary>
public class ActivityEntry
{
    /// <summary>Gets or sets when it happened (UTC).</summary>
    public DateTime Utc { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the three-letter language.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Gets or sets the outcome.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider and release name, or the reason.</summary>
    public string Detail { get; set; } = string.Empty;
}

/// <summary>Totals for one run of the task.</summary>
public class RunSummary
{
    /// <summary>Gets or sets when the run started (UTC).</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>Gets or sets when the run ended (UTC). Null while running.</summary>
    public DateTime? FinishedUtc { get; set; }

    /// <summary>Gets or sets a value indicating whether nothing was downloaded on purpose.</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets the number of videos in scope.</summary>
    public int Videos { get; set; }

    /// <summary>Gets or sets the number of video and language pairs checked.</summary>
    public int Checked { get; set; }

    /// <summary>Gets or sets the pairs that already had the subtitle.</summary>
    public int AlreadyPresent { get; set; }

    /// <summary>Gets or sets the pairs skipped because they were tried recently.</summary>
    public int SkippedRecentlyTried { get; set; }

    /// <summary>Gets or sets the pairs searched at the providers.</summary>
    public int Searched { get; set; }

    /// <summary>Gets or sets the subtitles downloaded (or, in a dry run, that would be).</summary>
    public int Downloaded { get; set; }

    /// <summary>Gets or sets the pairs with no usable result.</summary>
    public int NotFound { get; set; }

    /// <summary>Gets or sets the pairs where every download failed.</summary>
    public int Failed { get; set; }

    /// <summary>Gets or sets why the run ended.</summary>
    public string StopReason { get; set; } = string.Empty;
}

/// <summary>Everything the plugin remembers between runs.</summary>
public class StateData
{
    /// <summary>Gets or sets attempts keyed by "itemId|language".</summary>
    public Dictionary<string, StateEntry> Items { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the newest activity, newest first.</summary>
    public List<ActivityEntry> Recent { get; set; } = new();

    /// <summary>Gets or sets the last run.</summary>
    public RunSummary? LastRun { get; set; }
}

/// <summary>
/// Thread-safe JSON file holding what has been tried, so a run continues where the last stopped
/// and titles with no subtitles are not searched again every night.
/// </summary>
public sealed class StateStore
{
    private const int MaxRecent = 100;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly Lock _lock = new();
    private StateData _data;
    private int _unsaved;

    /// <summary>
    /// Initializes a new instance of the <see cref="StateStore"/> class.
    /// </summary>
    /// <param name="path">Full path of the JSON file.</param>
    public StateStore(string path)
    {
        _path = path;
        _data = Load(path);
    }

    /// <summary>Builds the dictionary key for a video and language.</summary>
    /// <param name="itemId">The video id.</param>
    /// <param name="language">The three-letter language.</param>
    /// <returns>The key.</returns>
    public static string Key(Guid itemId, string language) => itemId.ToString("N") + "|" + language;

    /// <summary>Gets the remembered attempt for a key, if any.</summary>
    /// <param name="key">Key from <see cref="Key"/>.</param>
    /// <returns>The entry or null.</returns>
    public StateEntry? Get(string key)
    {
        lock (_lock)
        {
            return _data.Items.TryGetValue(key, out var e) ? e : null;
        }
    }

    /// <summary>Remembers an attempt and adds it to the activity list.</summary>
    /// <param name="key">Key from <see cref="Key"/>.</param>
    /// <param name="status">One of <see cref="AttemptStatus"/>.</param>
    /// <param name="language">The three-letter language.</param>
    /// <param name="title">Title for the activity list.</param>
    /// <param name="detail">Provider and release, or the reason.</param>
    public void Record(string key, string status, string language, string title, string detail)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            _data.Items[key] = new StateEntry { Status = status, Utc = now, Language = language };

            // "Not found" is the common case and would bury the useful lines.
            if (!string.Equals(status, AttemptStatus.NotFound, StringComparison.Ordinal))
            {
                AddActivityLocked(new ActivityEntry { Utc = now, Title = title, Language = language, Status = status, Detail = detail });
            }

            _unsaved++;
        }

        if (_unsaved >= 20)
        {
            Save();
        }
    }

    /// <summary>Adds a line to the activity list without remembering an attempt (dry runs).</summary>
    /// <param name="entry">The line.</param>
    public void AddActivity(ActivityEntry entry)
    {
        lock (_lock)
        {
            AddActivityLocked(entry);
        }
    }

    /// <summary>Stores the summary of the current or last run.</summary>
    /// <param name="summary">The summary.</param>
    public void SetLastRun(RunSummary summary)
    {
        lock (_lock)
        {
            _data.LastRun = summary;
        }
    }

    /// <summary>Forgets attempts older than the retry period; they are re-evaluated anyway.</summary>
    /// <param name="olderThan">Age limit.</param>
    public void Prune(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        lock (_lock)
        {
            foreach (var key in _data.Items.Where(kv => kv.Value.Utc < cutoff).Select(kv => kv.Key).ToList())
            {
                _data.Items.Remove(key);
            }
        }
    }

    /// <summary>Forgets remembered attempts so they are searched again.</summary>
    /// <param name="onlyUnsuccessful">True to keep the "downloaded" entries.</param>
    /// <returns>How many entries were removed.</returns>
    public int Reset(bool onlyUnsuccessful)
    {
        int removed;
        lock (_lock)
        {
            var keys = _data.Items
                .Where(kv => !onlyUnsuccessful || !string.Equals(kv.Value.Status, AttemptStatus.Downloaded, StringComparison.Ordinal))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in keys)
            {
                _data.Items.Remove(key);
            }

            removed = keys.Count;
        }

        Save();
        return removed;
    }

    /// <summary>Builds the snapshot returned to the settings page.</summary>
    /// <returns>Last run, totals per language and recent activity.</returns>
    public object Snapshot()
    {
        lock (_lock)
        {
            var totals = _data.Items.Values
                .GroupBy(e => e.Language, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new
                {
                    Language = g.Key,
                    Downloaded = g.Count(e => e.Status == AttemptStatus.Downloaded),
                    NotFound = g.Count(e => e.Status == AttemptStatus.NotFound),
                    Failed = g.Count(e => e.Status == AttemptStatus.Failed)
                })
                .ToList();

            return new
            {
                _data.LastRun,
                Totals = totals,
                Recent = _data.Recent.Take(30).ToList()
            };
        }
    }

    /// <summary>Writes the file. Safe to call at any time.</summary>
    public void Save()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_data, JsonOptions));
                File.Move(tmp, _path, true);
                _unsaved = 0;
            }
            catch (IOException)
            {
                // State is a convenience; losing a save only means some titles are searched again.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static StateData Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<StateData>(File.ReadAllText(path), JsonOptions);
                if (data is not null)
                {
                    data.Items = new Dictionary<string, StateEntry>(data.Items ?? new(), StringComparer.Ordinal);
                    data.Recent ??= new();
                    return data;
                }
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }

        return new StateData();
    }

    private void AddActivityLocked(ActivityEntry entry)
    {
        _data.Recent.Insert(0, entry);
        if (_data.Recent.Count > MaxRecent)
        {
            _data.Recent.RemoveRange(MaxRecent, _data.Recent.Count - MaxRecent);
        }
    }
}
