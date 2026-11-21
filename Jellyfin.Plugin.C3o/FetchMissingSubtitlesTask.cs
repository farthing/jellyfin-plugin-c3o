using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.C3o.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.C3o;

/// <summary>
/// Scheduled task: walks the library and, for every video missing an SRT in one of the configured
/// languages, searches the server's installed subtitle providers and saves the best match.
/// </summary>
/// <remarks>
/// Design notes (why it is not just the server's own "Download missing subtitles" task):
/// - Providers are called directly, not through ISubtitleManager.SearchSubtitles, because the
///   manager swallows provider errors and returns an empty list. An outage would then look like
///   "no subtitles exist" and be remembered for weeks.
/// - Saving still goes through ISubtitleManager.DownloadSubtitles so files get the server's own
///   naming (name.lang.srt), location rule and file-watcher handling.
/// - Attempts are remembered, so a run resumes where the provider's daily quota stopped the last one.
/// Credit: the library query and the refresh after a download follow the approach of Jellyfin's
/// own SubtitleScheduledTask (https://github.com/jellyfin/jellyfin). See CREDITS.md.
/// </remarks>
public class FetchMissingSubtitlesTask : IScheduledTask
{
    private const int MaxDownloadAttemptsPerItem = 3;
    private const int MaxConsecutiveSearchErrors = 8;

    // A dry run remembers nothing, so without its own limits it would search the same titles
    // again on every press of the button.
    private const int DryRunMaxResults = 25;
    private const int DryRunMaxSearches = 60;

    private readonly ILibraryManager _libraryManager;
    private readonly ISubtitleManager _subtitleManager;
    private readonly ILocalizationManager _localization;
    private readonly ISubtitleProvider[] _providers;
    private readonly ILogger<FetchMissingSubtitlesTask> _logger;
    private readonly Dictionary<string, string?> _languageCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="FetchMissingSubtitlesTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Library access.</param>
    /// <param name="subtitleManager">Saves downloaded subtitles.</param>
    /// <param name="localization">Language code lookup.</param>
    /// <param name="providers">Every subtitle provider installed on the server.</param>
    /// <param name="logger">Logger.</param>
    public FetchMissingSubtitlesTask(
        ILibraryManager libraryManager,
        ISubtitleManager subtitleManager,
        ILocalizationManager localization,
        IEnumerable<ISubtitleProvider> providers,
        ILogger<FetchMissingSubtitlesTask> logger)
    {
        _libraryManager = libraryManager;
        _subtitleManager = subtitleManager;
        _localization = localization;
        _providers = providers.ToArray();
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Download missing SRT subtitles";

    /// <inheritdoc />
    public string Key => Plugin.TaskKey;

    /// <inheritdoc />
    public string Description => "Finds videos with no SRT in the languages set on the C-3.o settings page and downloads them.";

    /// <inheritdoc />
    public string Category => "C-3.o";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Provider quotas are per day, so once a night is the natural rhythm.
        return new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var config = plugin.Configuration;
        var state = plugin.State;
        var summary = new RunSummary { StartedUtc = DateTime.UtcNow, DryRun = config.DryRun };
        state.SetLastRun(summary);

        try
        {
            summary.StopReason = await Run(config, state, summary, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            summary.StopReason = "Cancelled.";
            throw;
        }
        catch (Exception ex)
        {
            summary.StopReason = "Error: " + ex.Message;
            _logger.LogError(ex, "C-3.o run failed");
            throw;
        }
        finally
        {
            summary.FinishedUtc = DateTime.UtcNow;
            state.SetLastRun(summary);
            state.Save();
            _logger.LogInformation(
                "C-3.o finished: {Downloaded} downloaded, {NotFound} not found, {Failed} failed, {Present} already present, {Skipped} tried recently. {Reason}",
                summary.Downloaded,
                summary.NotFound,
                summary.Failed,
                summary.AlreadyPresent,
                summary.SkippedRecentlyTried,
                summary.StopReason);
        }
    }

    private async Task<string> Run(
        PluginConfiguration config,
        StateStore state,
        RunSummary summary,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var languages = ResolveLanguages(config);
        if (languages.Count == 0)
        {
            return "No languages are set. Choose at least one on the settings page.";
        }

        if (_providers.Length == 0)
        {
            return "No subtitle provider plugin is installed (for example Open Subtitles).";
        }

        var retryAfter = TimeSpan.FromDays(Math.Max(1, config.RetryAfterDays));
        state.Prune(retryAfter);

        var videos = GetVideos(config);
        summary.Videos = videos.Count;
        if (videos.Count == 0)
        {
            return "No videos in the selected libraries.";
        }

        var providerIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var exhausted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var presence = new Dictionary<Guid, Presence>();
        var total = (double)videos.Count * languages.Count;
        var consecutiveSearchErrors = 0;
        var delay = Math.Clamp(config.DelayBetweenSearchesMs, 0, 60000);
        var maxDownloads = config.MaxDownloadsPerRun;
        var maxSearches = config.MaxSearchesPerRun;
        if (config.DryRun)
        {
            maxDownloads = maxDownloads > 0 ? Math.Min(maxDownloads, DryRunMaxResults) : DryRunMaxResults;
            maxSearches = maxSearches > 0 ? Math.Min(maxSearches, DryRunMaxSearches) : DryRunMaxSearches;
        }

        foreach (var (video, language, isFirstLanguage) in Sequence(videos, languages, config.LanguageFirstOrder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            summary.Checked++;
            progress.Report(100d * summary.Checked / total);

            var key = StateStore.Key(video.Id, language.ThreeLetter);
            var previous = state.Get(key);
            if (previous is not null && DateTime.UtcNow - previous.Utc < retryAfter)
            {
                if (string.Equals(previous.Status, AttemptStatus.Downloaded, StringComparison.Ordinal))
                {
                    summary.AlreadyPresent++;
                }
                else
                {
                    summary.SkippedRecentlyTried++;
                }

                continue;
            }

            if (!presence.TryGetValue(video.Id, out var present))
            {
                present = Analyse(video, config);
                presence[video.Id] = present;
            }

            if (present.Languages.Contains(language.ThreeLetter)
                || (isFirstLanguage && present.UntaggedSrt && config.UntaggedSrtCountsAsFirstLanguage))
            {
                summary.AlreadyPresent++;
                continue;
            }

            var libraryOptions = _libraryManager.GetLibraryOptions(video);
            var active = _providers
                .Where(p => !exhausted.Contains(p.Name)
                    && p.SupportedMediaTypes.Contains(video is Episode ? VideoContentType.Episode : VideoContentType.Movie)
                    && !libraryOptions.DisabledSubtitleFetchers.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (active.Length == 0)
            {
                if (exhausted.Count > 0)
                {
                    return "Stopped: the download limit of " + string.Join(", ", exhausted) + " is used up. The next run continues from here.";
                }

                // Every provider is switched off for this library; nothing to do for this video.
                continue;
            }

            var title = DisplayTitle(video);
            summary.Searched++;
            var search = await Search(video, language, active, config, exhausted, cancellationToken).ConfigureAwait(false);

            if (search.Candidates.Count == 0)
            {
                if (search.HadError)
                {
                    // Not remembered: an error says nothing about whether a subtitle exists.
                    consecutiveSearchErrors++;
                    if (consecutiveSearchErrors >= MaxConsecutiveSearchErrors)
                    {
                        return "Stopped: the subtitle provider failed " + consecutiveSearchErrors.ToString(CultureInfo.InvariantCulture) + " searches in a row. See the server log.";
                    }
                }
                else
                {
                    consecutiveSearchErrors = 0;
                    summary.NotFound++;
                    if (!config.DryRun)
                    {
                        state.Record(key, AttemptStatus.NotFound, language.ThreeLetter, title, string.Empty);
                    }
                }
            }
            else if (config.DryRun)
            {
                consecutiveSearchErrors = 0;
                var best = search.Candidates[0];
                summary.Downloaded++;
                state.AddActivity(new ActivityEntry
                {
                    Utc = DateTime.UtcNow,
                    Title = title,
                    Language = language.ThreeLetter,
                    Status = "DryRun",
                    Detail = Describe(best)
                });
                _logger.LogInformation("Dry run: would download {Language} for {Title} from {Detail}", language.ThreeLetter, title, Describe(best));
            }
            else
            {
                consecutiveSearchErrors = 0;
                var outcome = await Download(video, language, search.Candidates, providerIds, exhausted, cancellationToken).ConfigureAwait(false);
                if (outcome.Saved is not null)
                {
                    summary.Downloaded++;
                    present.Languages.Add(language.ThreeLetter);
                    state.Record(key, AttemptStatus.Downloaded, language.ThreeLetter, title, Describe(outcome.Saved));
                    _logger.LogInformation("Downloaded {Language} subtitle for {Title} from {Detail}", language.ThreeLetter, title, Describe(outcome.Saved));

                    try
                    {
                        // Makes the new file show up as a subtitle track straight away.
                        await video.RefreshMetadata(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Subtitle saved but refreshing {Title} failed; the next library scan will pick it up", title);
                    }
                }
                else if (!outcome.RateLimited)
                {
                    summary.Failed++;
                    state.Record(key, AttemptStatus.Failed, language.ThreeLetter, title, outcome.Error ?? "Download failed");
                }

                // Rate limited with nothing saved: leave it unrecorded so the next run tries again.
            }

            if (maxDownloads > 0 && summary.Downloaded >= maxDownloads)
            {
                return config.DryRun
                    ? "Dry run stopped after " + summary.Downloaded.ToString(CultureInfo.InvariantCulture) + " results. Nothing was downloaded."
                    : "Stopped: reached the per-run cap of " + maxDownloads.ToString(CultureInfo.InvariantCulture) + " downloads. The next run continues from here.";
            }

            if (maxSearches > 0 && summary.Searched >= maxSearches)
            {
                return config.DryRun
                    ? "Dry run stopped after " + summary.Searched.ToString(CultureInfo.InvariantCulture) + " searches. Nothing was downloaded."
                    : "Stopped: reached the per-run cap of " + maxSearches.ToString(CultureInfo.InvariantCulture) + " searches. The next run continues from here.";
            }

            if (exhausted.Count >= _providers.Length)
            {
                return "Stopped: the download limit of " + string.Join(", ", exhausted) + " is used up. The next run continues from here.";
            }

            if (delay > 0)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        return "Finished: every video was checked.";
    }

    private static IEnumerable<(Video Video, WantedLanguage Language, bool IsFirst)> Sequence(
        List<Video> videos,
        List<WantedLanguage> languages,
        bool languageFirst)
    {
        if (languageFirst)
        {
            for (var l = 0; l < languages.Count; l++)
            {
                foreach (var video in videos)
                {
                    yield return (video, languages[l], l == 0);
                }
            }
        }
        else
        {
            foreach (var video in videos)
            {
                for (var l = 0; l < languages.Count; l++)
                {
                    yield return (video, languages[l], l == 0);
                }
            }
        }
    }

    private static string DisplayTitle(Video video)
    {
        if (video is Episode episode)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} S{1:00}E{2:00}",
                episode.SeriesName ?? "Unknown series",
                episode.ParentIndexNumber ?? 0,
                episode.IndexNumber ?? 0);
        }

        return video.ProductionYear.HasValue
            ? string.Format(CultureInfo.InvariantCulture, "{0} ({1})", video.Name, video.ProductionYear.Value)
            : video.Name;
    }

    private static string Describe(Candidate candidate)
    {
        var info = candidate.Info;
        var match = info.IsHashMatch == true ? ", exact file match" : string.Empty;
        return string.IsNullOrWhiteSpace(info.Name)
            ? candidate.Provider.Name + match
            : candidate.Provider.Name + ": " + info.Name + match;
    }

    private static bool IsSrt(string? format)
    {
        return string.Equals(format, "srt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(format, "subrip", StringComparison.OrdinalIgnoreCase);
    }

    private List<WantedLanguage> ResolveLanguages(PluginConfiguration config)
    {
        var result = new List<WantedLanguage>();
        foreach (var raw in config.Languages ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw) || result.Count >= PluginConfiguration.MaxLanguages)
            {
                continue;
            }

            var culture = _localization.FindLanguageInfo(raw.Trim());
            if (culture?.ThreeLetterISOLanguageName is null || string.IsNullOrEmpty(culture.TwoLetterISOLanguageName))
            {
                _logger.LogWarning("C-3.o: language '{Language}' is not known to the server and is ignored", raw);
                continue;
            }

            if (result.All(l => !string.Equals(l.ThreeLetter, culture.ThreeLetterISOLanguageName, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(new WantedLanguage(culture.ThreeLetterISOLanguageName, culture.TwoLetterISOLanguageName));
            }
        }

        return result;
    }

    private List<Video> GetVideos(PluginConfiguration config)
    {
        var types = new List<BaseItemKind>();
        if (config.IncludeMovies)
        {
            types.Add(BaseItemKind.Movie);
        }

        if (config.IncludeEpisodes)
        {
            types.Add(BaseItemKind.Episode);
        }

        var byId = new Dictionary<Guid, Video>();
        if (types.Count == 0)
        {
            return new List<Video>();
        }

        var wanted = new HashSet<string>(config.LibraryIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var library in LibraryScope.GetLibraries(_libraryManager))
        {
            if (wanted.Count > 0 && !wanted.Contains(library.Id.ToString("N")))
            {
                continue;
            }

            var query = new InternalItemsQuery
            {
                MediaTypes = new[] { MediaType.Video },
                IsVirtualItem = false,
                IncludeItemTypes = types.ToArray(),
                DtoOptions = new DtoOptions(true),
                SourceTypes = new[] { SourceType.Library },
                Parent = library,
                Recursive = true
            };

            foreach (var item in _libraryManager.GetItemList(query))
            {
                // Only real files can take a sidecar subtitle (not discs, not .strm links).
                if (item is Video video
                    && video.VideoType == VideoType.VideoFile
                    && !string.IsNullOrEmpty(video.Path)
                    && !video.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                {
                    byId[video.Id] = video;
                }
            }
        }

        // Newest additions first, so new arrivals are not stuck behind the whole back catalogue.
        return byId.Values.OrderByDescending(v => v.DateCreated).ToList();
    }

    private Presence Analyse(Video video, PluginConfiguration config)
    {
        var presence = new Presence();
        foreach (var stream in video.GetMediaStreams())
        {
            // A forced track only covers foreign-language lines, so it does not count as having subtitles.
            if (stream.Type != MediaStreamType.Subtitle || stream.IsForced)
            {
                continue;
            }

            if (stream.IsExternal)
            {
                var isSrt = IsSrt(stream.Codec)
                    || (stream.Path is not null && stream.Path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase));
                if (!isSrt)
                {
                    continue;
                }

                var language = Canonical(stream.Language);
                if (language is null)
                {
                    presence.UntaggedSrt = true;
                }
                else
                {
                    presence.Languages.Add(language);
                }
            }
            else if (config.CountEmbeddedTextSubtitles && stream.IsTextSubtitleStream)
            {
                var language = Canonical(stream.Language);
                if (language is not null)
                {
                    presence.Languages.Add(language);
                }
            }
        }

        return presence;
    }

    /// <summary>Maps any language tag ("en", "eng", "fre", "fra", "pt-BR") to one three-letter code.</summary>
    private string? Canonical(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        tag = tag.Trim();
        if (string.Equals(tag, "und", StringComparison.OrdinalIgnoreCase) || string.Equals(tag, "unk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (_languageCache.TryGetValue(tag, out var cached))
        {
            return cached;
        }

        var culture = _localization.FindLanguageInfo(tag);
        if (culture is null)
        {
            var dash = tag.IndexOfAny(new[] { '-', '_' });
            if (dash > 0)
            {
                culture = _localization.FindLanguageInfo(tag.Substring(0, dash));
            }
        }

        var result = culture?.ThreeLetterISOLanguageName ?? tag.ToLowerInvariant();
        _languageCache[tag] = result;
        return result;
    }

    private async Task<SearchResult> Search(
        Video video,
        WantedLanguage language,
        ISubtitleProvider[] providers,
        PluginConfiguration config,
        HashSet<string> exhausted,
        CancellationToken cancellationToken)
    {
        var result = new SearchResult();
        foreach (var provider in providers)
        {
            // A fresh request per provider: providers are free to modify it.
            var request = new SubtitleSearchRequest
            {
                ContentType = video is Episode ? VideoContentType.Episode : VideoContentType.Movie,
                Language = language.ThreeLetter,
                TwoLetterISOLanguageName = language.TwoLetter,
                MediaPath = video.Path,
                Name = video.Name,
                IndexNumber = video.IndexNumber,
                ParentIndexNumber = video.ParentIndexNumber,
                ProductionYear = video.ProductionYear,
                ProviderIds = video.ProviderIds,
                RuntimeTicks = video.RunTimeTicks,
                IsPerfectMatch = config.RequireHashMatch,

                // False on purpose: in automated mode Open Subtitles answers "nothing" once the
                // daily quota is gone, which is indistinguishable from "no subtitle exists".
                IsAutomated = false
            };

            if (video is Episode episode)
            {
                request.IndexNumberEnd = episode.IndexNumberEnd;
                request.SeriesName = episode.SeriesName;
            }

            try
            {
                var found = await provider.Search(request, cancellationToken).ConfigureAwait(false);
                foreach (var info in found ?? Enumerable.Empty<RemoteSubtitleInfo>())
                {
                    if (IsUsable(info, config))
                    {
                        result.Candidates.Add(new Candidate(provider, info));
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (RateLimitExceededException)
            {
                exhausted.Add(provider.Name);
            }
            catch (Exception ex)
            {
                result.HadError = true;
                _logger.LogWarning(ex, "C-3.o: {Provider} search failed for {Path}", provider.Name, video.Path);
            }
        }

        result.Candidates = result.Candidates
            .OrderByDescending(c => c.Info.IsHashMatch == true)
            .ThenBy(c => c.Info.MachineTranslated == true || c.Info.AiTranslated == true)
            .ThenBy(c => c.Info.HearingImpaired == true)
            .ThenByDescending(c => c.Info.DownloadCount ?? 0)
            .ThenByDescending(c => c.Info.CommunityRating ?? 0)
            .ToList();
        return result;
    }

    private static bool IsUsable(RemoteSubtitleInfo info, PluginConfiguration config)
    {
        if (string.IsNullOrEmpty(info.Id) || !IsSrt(info.Format) || info.Forced == true)
        {
            return false;
        }

        if (!config.AllowMachineTranslated && (info.MachineTranslated == true || info.AiTranslated == true))
        {
            return false;
        }

        if (!config.AllowHearingImpaired && info.HearingImpaired == true)
        {
            return false;
        }

        return !config.RequireHashMatch || info.IsHashMatch == true;
    }

    private async Task<DownloadOutcome> Download(
        Video video,
        WantedLanguage language,
        List<Candidate> candidates,
        Dictionary<string, string> providerIds,
        HashSet<string> exhausted,
        CancellationToken cancellationToken)
    {
        var outcome = new DownloadOutcome();
        var libraryOptions = _libraryManager.GetLibraryOptions(video);
        var attempts = 0;

        foreach (var candidate in candidates)
        {
            if (exhausted.Contains(candidate.Provider.Name))
            {
                continue;
            }

            if (attempts >= MaxDownloadAttemptsPerItem)
            {
                break;
            }

            if (!providerIds.TryGetValue(candidate.Provider.Name, out var providerId))
            {
                // The manager addresses providers by an id it derives itself; ask it for the mapping.
                foreach (var supported in _subtitleManager.GetSupportedProviders(video))
                {
                    providerIds[supported.Name] = supported.Id;
                }

                if (!providerIds.TryGetValue(candidate.Provider.Name, out providerId))
                {
                    outcome.Error = "Provider " + candidate.Provider.Name + " is not registered with the server";
                    continue;
                }
            }

            attempts++;
            try
            {
                await _subtitleManager
                    .DownloadSubtitles(video, libraryOptions, providerId + "_" + candidate.Info.Id, cancellationToken)
                    .ConfigureAwait(false);
                outcome.Saved = candidate;
                outcome.RateLimited = false;
                return outcome;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (RateLimitExceededException)
            {
                exhausted.Add(candidate.Provider.Name);
                outcome.RateLimited = true;
            }
            catch (Exception ex)
            {
                outcome.Error = ex.Message;
                _logger.LogWarning(
                    ex,
                    "C-3.o: download of {Language} for {Path} from {Provider} failed",
                    language.ThreeLetter,
                    video.Path,
                    candidate.Provider.Name);
            }
        }

        // Only "rate limited" if that is the sole reason nothing was saved.
        if (outcome.Error is not null)
        {
            outcome.RateLimited = false;
        }

        return outcome;
    }

    private sealed record WantedLanguage(string ThreeLetter, string TwoLetter);

    private sealed record Candidate(ISubtitleProvider Provider, RemoteSubtitleInfo Info);

    private sealed class Presence
    {
        public HashSet<string> Languages { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool UntaggedSrt { get; set; }
    }

    private sealed class SearchResult
    {
        public List<Candidate> Candidates { get; set; } = new();

        public bool HadError { get; set; }
    }

    private sealed class DownloadOutcome
    {
        public Candidate? Saved { get; set; }

        public bool RateLimited { get; set; }

        public string? Error { get; set; }
    }
}
