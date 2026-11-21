using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.C3o;

/// <summary>
/// The one place that decides which libraries C-3.o looks at, shared by the task and the
/// settings page so the two can never disagree.
/// </summary>
public static class LibraryScope
{
    /// <summary>Gets the libraries that can hold movies or TV episodes.</summary>
    /// <param name="libraryManager">Library access.</param>
    /// <returns>Movie, TV and mixed-content libraries.</returns>
    public static List<BaseItem> GetLibraries(ILibraryManager libraryManager)
    {
        return libraryManager.RootFolder.Children
            .Where(IsVideoLibrary)
            .ToList();
    }

    private static bool IsVideoLibrary(BaseItem library)
    {
        // A folder that does not declare a type is treated as mixed content and kept.
        if (library is not IHasCollectionType typed)
        {
            return true;
        }

        return typed.CollectionType is null
            or CollectionType.movies
            or CollectionType.tvshows;
    }
}
