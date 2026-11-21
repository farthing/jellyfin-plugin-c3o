using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.C3o.Api;

/// <summary>
/// Admin-only endpoints used by the settings page.
/// </summary>
[ApiController]
[Route("C3o")]
[Produces(MediaTypeNames.Application.Json)]
[Authorize(Policy = Policies.RequiresElevation)]
public class C3oController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IEnumerable<ISubtitleProvider> _providers;

    /// <summary>
    /// Initializes a new instance of the <see cref="C3oController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library access.</param>
    /// <param name="providers">Installed subtitle providers.</param>
    public C3oController(ILibraryManager libraryManager, IEnumerable<ISubtitleProvider> providers)
    {
        _libraryManager = libraryManager;
        _providers = providers;
    }

    /// <summary>Last run, totals, recent activity and the installed subtitle providers.</summary>
    /// <returns>The status snapshot.</returns>
    [HttpGet("Status")]
    public ActionResult GetStatus()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        return Ok(new
        {
            State = plugin.State.Snapshot(),
            Providers = _providers.Select(p => p.Name).OrderBy(n => n).ToList()
        });
    }

    /// <summary>
    /// Libraries as the task sees them. The settings page uses these ids, so the ids it saves are
    /// by construction the ones the task compares against.
    /// </summary>
    /// <returns>Id and name of each library.</returns>
    [HttpGet("Libraries")]
    public ActionResult GetLibraries()
    {
        var libraries = LibraryScope.GetLibraries(_libraryManager)
            .Select(l => new { Id = l.Id.ToString("N"), l.Name })
            .OrderBy(l => l.Name)
            .ToList();
        return Ok(libraries);
    }

    /// <summary>Forgets remembered attempts so they are searched again on the next run.</summary>
    /// <param name="all">True to also forget what was downloaded.</param>
    /// <returns>How many entries were removed.</returns>
    [HttpPost("Reset")]
    public ActionResult Reset([FromQuery] bool all = false)
    {
        var plugin = Plugin.Instance;
        return plugin is null ? NotFound() : Ok(new { Removed = plugin.State.Reset(!all) });
    }
}
