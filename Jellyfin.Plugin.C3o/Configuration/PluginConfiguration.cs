using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.C3o.Configuration;

/// <summary>
/// Settings shown on the plugin's settings page.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>The most languages the plugin will fetch.</summary>
    public const int MaxLanguages = 5;

    /// <summary>
    /// Gets or sets the wanted languages as three-letter codes, highest priority first.
    /// Empty means the plugin does nothing.
    /// </summary>
    public string[] Languages { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the libraries to process (ids from C3o/Libraries). Empty means all.</summary>
    public string[] LibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets a value indicating whether movies are processed.</summary>
    public bool IncludeMovies { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether TV episodes are processed.</summary>
    public bool IncludeEpisodes { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a text subtitle track inside the video file
    /// counts as "already has this language".
    /// </summary>
    public bool CountEmbeddedTextSubtitles { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether an SRT file with no language in its name
    /// counts as the first language.
    /// </summary>
    public bool UntaggedSrtCountsAsFirstLanguage { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether language 1 is finished across the whole library
    /// before language 2 starts. When false, every language is fetched for a title before moving on.
    /// </summary>
    public bool LanguageFirstOrder { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether only exact file (hash) matches are downloaded.</summary>
    public bool RequireHashMatch { get; set; }

    /// <summary>Gets or sets a value indicating whether hearing-impaired subtitles may be used.</summary>
    public bool AllowHearingImpaired { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether machine or AI translated subtitles may be used.</summary>
    public bool AllowMachineTranslated { get; set; }

    /// <summary>Gets or sets the most subtitles to download in one run. 0 means no cap.</summary>
    public int MaxDownloadsPerRun { get; set; }

    /// <summary>
    /// Gets or sets the most provider searches in one run. 0 means no cap. Keeps a first pass over a
    /// large library from sending tens of thousands of requests to a free service in one night.
    /// </summary>
    public int MaxSearchesPerRun { get; set; } = 500;

    /// <summary>Gets or sets how many days to wait before searching again for a subtitle that was not found.</summary>
    public int RetryAfterDays { get; set; } = 30;

    /// <summary>Gets or sets the pause between provider searches, in milliseconds.</summary>
    public int DelayBetweenSearchesMs { get; set; } = 1000;

    /// <summary>Gets or sets a value indicating whether the run only reports what it would download.</summary>
    public bool DryRun { get; set; }
}
