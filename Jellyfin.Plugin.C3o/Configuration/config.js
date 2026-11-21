const C3o = {
    pluginId: '3853ff20-a714-4171-ade4-7d89ef9b5fb2',
    taskKey: 'C3oFetchMissingSubs',
    maxLanguages: 5
};

function esc(value) {
    return String(value == null ? '' : value)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}

function when(utc) {
    return utc ? new Date(utc).toLocaleString() : '';
}

function getJson(path) {
    return ApiClient.getJSON(ApiClient.getUrl(path));
}

export default function (view) {
    let libraries = [];

    function fillLanguages(cultures, selected) {
        const sorted = cultures
            .filter(c => c.ThreeLetterISOLanguageName)
            .sort((a, b) => a.DisplayName.localeCompare(b.DisplayName));
        const options = '<option value="">None</option>' + sorted
            .map(c => `<option value="${esc(c.ThreeLetterISOLanguageName)}">${esc(c.DisplayName)}</option>`)
            .join('');
        view.querySelectorAll('.c3oLang').forEach((select, index) => {
            select.innerHTML = options;
            select.value = selected[index] || '';
        });
    }

    function fillLibraries(selected) {
        const all = selected.length === 0;
        const rows = libraries.map(l => `
            <div class="checkboxContainer">
                <label><input is="emby-checkbox" type="checkbox" class="c3oLibrary" data-id="${esc(l.Id)}" ${all || selected.includes(l.Id) ? 'checked' : ''} /><span>${esc(l.Name)}</span></label>
            </div>`).join('');
        view.querySelector('#c3oLibraries').innerHTML = '<div class="fieldDescription" style="margin-bottom: .5em;">Libraries</div>' + rows;
    }

    function showStatus(status) {
        const providers = status.Providers || [];
        view.querySelector('#c3oProviders').innerHTML = providers.length
            ? 'Subtitle providers on this server: <b>' + providers.map(esc).join(', ') + '</b>'
            : '<b style="color:#ff5252;">No subtitle provider is installed.</b> Install one first, for example Open Subtitles from the plugin catalogue.';

        const state = status.State || {};
        const run = state.LastRun;
        const el = view.querySelector('#c3oStatus');
        if (!run) {
            el.innerHTML = 'Has not run yet.';
        } else {
            const verb = run.DryRun ? 'would be downloaded' : 'downloaded';
            const head = run.FinishedUtc
                ? `Last run ${esc(when(run.StartedUtc))}${run.DryRun ? ' (dry run)' : ''}`
                : `Running since ${esc(when(run.StartedUtc))}${run.DryRun ? ' (dry run)' : ''}`;
            el.innerHTML = `${head}<br>
                <b>${run.Downloaded}</b> ${verb}, ${run.NotFound} not found, ${run.Failed} failed.<br>
                ${run.Videos} videos in scope; ${run.Checked} checks, of which ${run.AlreadyPresent} already had the subtitle and ${run.SkippedRecentlyTried} were tried recently.<br>
                ${esc(run.StopReason || '')}`;
        }

        const totals = state.Totals || [];
        view.querySelector('#c3oTotals').innerHTML = totals.length
            ? '<div class="fieldDescription">Remembered results, by language</div><table style="border-spacing: 1em .2em;"><tr><th align="left">Language</th><th align="right">Downloaded</th><th align="right">Not found</th><th align="right">Failed</th></tr>'
                + totals.map(t => `<tr><td>${esc(t.Language)}</td><td align="right">${t.Downloaded}</td><td align="right">${t.NotFound}</td><td align="right">${t.Failed}</td></tr>`).join('')
                + '</table>'
            : '';

        const recent = state.Recent || [];
        view.querySelector('#c3oRecent').innerHTML = recent.length
            ? '<div class="fieldDescription">Recent activity</div><table style="border-spacing: 1em .2em;">'
                + recent.map(r => `<tr><td>${esc(when(r.Utc))}</td><td>${esc(r.Status)}</td><td>${esc(r.Language)}</td><td>${esc(r.Title)}</td><td>${esc(r.Detail)}</td></tr>`).join('')
                + '</table>'
            : '';
    }

    function loadStatus() {
        return getJson('C3o/Status').then(showStatus).catch(() => {
            view.querySelector('#c3oStatus').innerHTML = 'Could not load the status.';
        });
    }

    view.addEventListener('viewshow', function () {
        Dashboard.showLoadingMsg();
        Promise.all([
            ApiClient.getPluginConfiguration(C3o.pluginId),
            ApiClient.getCultures(),
            getJson('C3o/Libraries')
        ]).then(([config, cultures, libs]) => {
            libraries = libs || [];
            fillLanguages(cultures, config.Languages || []);
            fillLibraries(config.LibraryIds || []);
            view.querySelector('#c3oOrder').value = config.LanguageFirstOrder ? 'language' : 'title';
            view.querySelector('#c3oMovies').checked = config.IncludeMovies;
            view.querySelector('#c3oEpisodes').checked = config.IncludeEpisodes;
            view.querySelector('#c3oEmbedded').checked = config.CountEmbeddedTextSubtitles;
            view.querySelector('#c3oUntagged').checked = config.UntaggedSrtCountsAsFirstLanguage;
            view.querySelector('#c3oHash').checked = config.RequireHashMatch;
            view.querySelector('#c3oSdh').checked = config.AllowHearingImpaired;
            view.querySelector('#c3oMt').checked = config.AllowMachineTranslated;
            view.querySelector('#c3oMax').value = config.MaxDownloadsPerRun;
            view.querySelector('#c3oSearches').value = config.MaxSearchesPerRun;
            view.querySelector('#c3oRetry').value = config.RetryAfterDays;
            view.querySelector('#c3oDelay').value = config.DelayBetweenSearchesMs;
            view.querySelector('#c3oDryRun').checked = config.DryRun;
            Dashboard.hideLoadingMsg();
        }).catch(() => {
            Dashboard.hideLoadingMsg();
            Dashboard.processErrorResponse({ statusText: 'Failed to load the C-3.o settings' });
        });
        loadStatus();
    });

    view.querySelector('#C3oConfigForm').addEventListener('submit', function (e) {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(C3o.pluginId).then(config => {
            const languages = [];
            view.querySelectorAll('.c3oLang').forEach(select => {
                if (select.value && !languages.includes(select.value) && languages.length < C3o.maxLanguages) {
                    languages.push(select.value);
                }
            });

            const boxes = Array.from(view.querySelectorAll('.c3oLibrary'));
            const ticked = boxes.filter(b => b.checked).map(b => b.getAttribute('data-id'));
            if (boxes.length > 0 && ticked.length === 0) {
                Dashboard.hideLoadingMsg();
                Dashboard.processErrorResponse({ statusText: 'Tick at least one library' });
                return;
            }

            config.Languages = languages;
            // Empty means "all", so a library added later is included automatically.
            config.LibraryIds = ticked.length === boxes.length ? [] : ticked;
            config.LanguageFirstOrder = view.querySelector('#c3oOrder').value !== 'title';
            config.IncludeMovies = view.querySelector('#c3oMovies').checked;
            config.IncludeEpisodes = view.querySelector('#c3oEpisodes').checked;
            config.CountEmbeddedTextSubtitles = view.querySelector('#c3oEmbedded').checked;
            config.UntaggedSrtCountsAsFirstLanguage = view.querySelector('#c3oUntagged').checked;
            config.RequireHashMatch = view.querySelector('#c3oHash').checked;
            config.AllowHearingImpaired = view.querySelector('#c3oSdh').checked;
            config.AllowMachineTranslated = view.querySelector('#c3oMt').checked;
            config.MaxDownloadsPerRun = Math.max(0, parseInt(view.querySelector('#c3oMax').value, 10) || 0);
            config.MaxSearchesPerRun = Math.max(0, parseInt(view.querySelector('#c3oSearches').value, 10) || 0);
            config.RetryAfterDays = Math.max(1, parseInt(view.querySelector('#c3oRetry').value, 10) || 30);
            config.DelayBetweenSearchesMs = Math.max(0, parseInt(view.querySelector('#c3oDelay').value, 10) || 0);
            config.DryRun = view.querySelector('#c3oDryRun').checked;

            return ApiClient.updatePluginConfiguration(C3o.pluginId, config).then(result => {
                Dashboard.processPluginConfigurationUpdateResult(result);
            });
        }).catch(() => {
            Dashboard.hideLoadingMsg();
            Dashboard.processErrorResponse({ statusText: 'Failed to save the C-3.o settings' });
        });
        return false;
    });

    view.querySelector('#c3oRun').addEventListener('click', function () {
        ApiClient.getScheduledTasks().then(tasks => {
            const task = (tasks || []).find(t => t.Key === C3o.taskKey);
            if (!task) {
                Dashboard.processErrorResponse({ statusText: 'The C-3.o task was not found. Restart the server once after installing.' });
                return;
            }

            if (task.State !== 'Idle') {
                Dashboard.alert('C-3.o is already running. Progress is shown under Dashboard, Scheduled Tasks.');
                return;
            }

            return ApiClient.startScheduledTask(task.Id).then(() => {
                Dashboard.alert('Started. Progress is shown under Dashboard, Scheduled Tasks; press "Refresh status" here for the results.');
                setTimeout(loadStatus, 3000);
            });
        }).catch(() => {
            Dashboard.processErrorResponse({ statusText: 'Could not start the task' });
        });
    });

    view.querySelector('#c3oRefresh').addEventListener('click', loadStatus);

    view.querySelector('#c3oReset').addEventListener('click', function () {
        ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('C3o/Reset') }).then(() => {
            Dashboard.alert('Done. Titles with no subtitle found will be searched again on the next run.');
            loadStatus();
        }).catch(() => {
            Dashboard.processErrorResponse({ statusText: 'Could not reset' });
        });
    });
}
