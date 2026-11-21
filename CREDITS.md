# Credits

C-3.o is built on other people's work. Nothing below was copied verbatim; the structure and
approach were learned from these projects.

## Jellyfin

- Project: [jellyfin/jellyfin](https://github.com/jellyfin/jellyfin), GPL-2.0.
- What was taken: the plugin API this plugin is written against, and the approach of the
  server's own `SubtitleScheduledTask` (how to query the library for videos and how to refresh
  an item after a subtitle is saved).
- Where it is used: `Jellyfin.Plugin.C3o/FetchMissingSubtitlesTask.cs`.

## Jellyfin Open Subtitles plugin

- Project: [jellyfin/jellyfin-plugin-opensubtitles](https://github.com/jellyfin/jellyfin-plugin-opensubtitles), GPL-3.0.
- What was taken: the structure of a plugin settings page and how a plugin registers its
  pages and services.
- Where it is used: `Jellyfin.Plugin.C3o/Plugin.cs` and `Jellyfin.Plugin.C3o/Configuration/`.

## Subtitles

C-3.o downloads nothing by itself. Every subtitle comes from a provider plugin installed on your
server (for example Open Subtitles) and belongs to whoever made and uploaded it.

## Name

The name is a nod to a certain protocol droid. C-3.o is a fan-made, non-commercial project and
is not affiliated with or endorsed by Lucasfilm, Disney, or the Jellyfin project.
