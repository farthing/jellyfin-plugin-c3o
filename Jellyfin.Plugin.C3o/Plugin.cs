using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.C3o.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.C3o;

/// <summary>
/// C-3.o: downloads SRT subtitles for library videos that do not have them.
/// </summary>
/// <remarks>
/// Credit: page and service registration, and the settings page structure, follow the official
/// Jellyfin Open Subtitles plugin (https://github.com/jellyfin/jellyfin-plugin-opensubtitles).
/// See CREDITS.md.
/// </remarks>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Key of the scheduled task, also used by the settings page to start it.</summary>
    public const string TaskKey = "C3oFetchMissingSubs";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Server paths.</param>
    /// <param name="xmlSerializer">Serializer for the configuration file.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Kept beside the plugin's configuration file, not in the plugins folder, so the
        // server never mistakes the data folder for a plugin.
        var dataDir = Path.Combine(applicationPaths.PluginConfigurationsPath, "Jellyfin.Plugin.C3o");
        State = new StateStore(Path.Combine(dataDir, "state.json"));
    }

    /// <summary>Gets the running plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>Gets the record of what has been tried and downloaded.</summary>
    public StateStore State { get; }

    /// <inheritdoc />
    public override string Name => "C-3.o";

    /// <inheritdoc />
    public override string Description => "Automatically searches for missing SRT subtitles. Every night it checks all your movies and TV episodes and, for each one that has no SRT subtitle in your preferred languages (up to five, in your order of preference), finds and downloads one. It uses whatever subtitle providers are already set up in Jellyfin, so there is no extra account or service to configure.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("3853ff20-a714-4171-ade4-7d89ef9b5fb2");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        return new[]
        {
            new PluginPageInfo
            {
                Name = "c3o",
                EmbeddedResourcePath = ns + ".Configuration.config.html"
            },
            new PluginPageInfo
            {
                Name = "c3ojs",
                EmbeddedResourcePath = ns + ".Configuration.config.js"
            }
        };
    }
}
