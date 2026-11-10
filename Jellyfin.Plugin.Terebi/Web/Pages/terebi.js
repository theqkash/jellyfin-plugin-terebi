/* Terebi admin UI. Loaded by Jellyfin's plugin page loader (data-controller). */
export default function (view) {
    'use strict';
    var pluginId = '7d3e1b52-6f4a-4c8e-9b0d-2a5c7e9f1b34';
    var api = function (method, path, body, query) {
        var url = ApiClient.getUrl('Terebi/' + path, query || {});
        var opts = { type: method, url: url, dataType: 'json' };
        if (body !== undefined) { opts.contentType = 'application/json'; opts.data = JSON.stringify(body); }
        return ApiClient.ajax(opts).catch(function (e) {
            if (e && e.status === 204) { return null; }
            if (e && e.status === 202) { return null; }
            throw e;
        });
    };
    var $ = function (sel, root) { return (root || view).querySelector(sel); };
    var $$ = function (sel, root) { return Array.prototype.slice.call((root || view).querySelectorAll(sel)); };
    var esc = function (s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); };
    var log = function (msg) { var el = $('#tbLog'); el.textContent = new Date().toLocaleTimeString() + '  ' + msg + '\n' + el.textContent; };
    var state = { sources: [], channels: [], rules: [], selected: {}, status: null };

    // ---- tabs ----
    $$('.tb-tabs button').forEach(function (b) {
        b.addEventListener('click', function () {
            $$('.tb-tabs button').forEach(function (x) { x.classList.toggle('active', x === b); });
            $$('.tb-panel').forEach(function (p) { p.classList.toggle('active', p.dataset.panel === b.dataset.tab); });
            if (b.dataset.tab === 'channels') { loadChannels(); }
            if (b.dataset.tab === 'catalog') { loadCatalogFilters(); }
            if (b.dataset.tab === 'rules') { loadRules(); }
            if (b.dataset.tab === 'epg') { loadEpg(); }
            if (b.dataset.tab === 'sources') { loadSources(); }
            if (b.dataset.tab === 'status') { loadStatus(); }
        });
    });

    // ---- status ----
    function loadStatus() {
        return api('GET', 'Status').then(function (s) {
            state.status = s;
            $('#tbVersion').textContent = 'v' + s.version;
            var stats = [
                ['Sources', s.sources.length], ['Channels', s.enabledChannels + ' / ' + s.channels], ['Streams', s.streams],
                ['Dead streams', s.deadStreams], ['Unprobed', s.unprobedStreams], ['Open now', s.openStreams.length],
                ['Logos cached', (s.logos ? s.logos.cached : 0) + (s.logos && (s.logos.pending || s.logos.failed) ? ' (' + s.logos.pending + ' pending, ' + s.logos.failed + ' failed)' : '')],
                ['Guide refresh', s.guideRefreshRunning ? 'running' : 'idle']
            ];
            $('#tbStats').innerHTML = stats.map(function (x) { return '<div class="tb-card tb-stat"><b>' + esc(x[1]) + '</b>' + esc(x[0]) + '</div>'; }).join('');
            $('#tbOpen').innerHTML = s.openStreams.length ? s.openStreams.map(function (o) { return esc(o.channelName) + ' ← ' + esc(o.activeStreamRef) + ' · ' + o.consumerCount + ' viewer(s)' + (o.externalConsumers ? ' (' + o.externalConsumers + ' external)' : '') + ' · ' + o.switches + ' switch(es) · ' + Math.round(o.bytesWritten / 1048576) + ' MiB · last read ' + (o.secondsSinceRead == null ? '?' : o.secondsSinceRead + 's ago') + ' · sessions ' + (o.watchingSessions == null ? '?' : o.watchingSessions); }).join('<br>') : 'none';
            $('#tbEpgStatus').innerHTML = s.epg.length ? s.epg.map(function (e) { return esc(e.url) + ' → ' + (e.error ? '<span class="tb-pill tb-dead">' + esc(e.error) + '</span>' : e.channels + ' channels, ' + e.programmes + ' programmes kept'); }).join('<br>') : 'no EPG downloaded yet';
        });
    }

    // ---- sources ----
    function loadSources() {
        return api('GET', 'Sources').then(function (list) {
            state.sources = list;
            var tb = $('#tbSources tbody');
            tb.innerHTML = list.map(function (s) {
                return '<tr class="' + (s.enabled ? '' : 'tb-row-disabled') + '">' +
                    '<td>' + esc(s.name) + '<br><span class="tb-muted" style="font-size:.8em">id ' + esc(s.id) + '</span></td><td>' + esc(s.type) + (s.maxConnections ? ' <span class="tb-pill">' + s.maxConnections + ' conn</span>' : '') + '</td>' +
                    '<td class="tb-muted" style="max-width:22em;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">' + esc(s.url) + '</td>' +
                    '<td>' + (s.streamCount || 0) + '</td><td class="tb-muted">' + (s.lastRefreshUtc ? new Date(s.lastRefreshUtc).toLocaleString() : '–') + (s.lastError ? '<br><span class="tb-pill tb-dead">' + esc(s.lastError) + '</span>' : '') + '</td>' +
                    '<td>' + s.priority + '</td>' +
                    '<td><button class="tb-btn" data-src="' + s.id + '" data-action="editSource">Edit</button> <button class="tb-btn" data-src="' + s.id + '" data-action="testSource">Test</button> <button class="tb-btn danger" data-src="' + s.id + '" data-action="deleteSource">Delete</button></td></tr>';
            }).join('') || '<tr><td colspan="7" class="tb-muted">No sources yet. Add an Xtream account or an M3U playlist, or import from the Xtream plugin on the Status tab.</td></tr>';
            var sel = $('#tbCatSource'); sel.innerHTML = '<option value="">all sources</option>' + list.map(function (s) { return '<option value="' + s.id + '">' + esc(s.name) + '</option>'; }).join('');
        });
    }

    function editSource(s) {
        var isX = s.type === 'Xtream';
        openEditor('<h3>' + (s.name ? 'Edit source' : 'New ' + s.type + ' source') + '</h3>' +
            '<label>Name</label><input type="text" id="edName" value="' + esc(s.name) + '">' +
            '<label>' + (isX ? 'Xtream base URL (http://host:port/)' : 'M3U URL or file path') + '</label><input type="text" id="edUrl" value="' + esc(s.url) + '">' +
            (isX ? '<label>Username</label><input type="text" id="edUser" value="' + esc(s.username || '') + '"><label>Password</label><input type="text" id="edPass" value="' + esc(s.password || '') + '">' +
                '<label>Output</label><select id="edOut"><option value="ts">ts</option><option value="m3u8">m3u8</option></select>' : '') +
            '<label>Priority (lower = preferred)</label><input type="number" id="edPrio" value="' + (s.priority || 100) + '">' +
            '<label>Max simultaneous connections (0 = unknown)</label><input type="number" id="edMax" value="' + (s.maxConnections || 0) + '">' +
            '<label>Include categories (regex, empty = all)</label><input type="text" id="edInc" value="' + esc(s.includeCategories || '') + '">' +
            '<label>Exclude categories (regex)</label><input type="text" id="edExc" value="' + esc(s.excludeCategories || '') + '">' +
            '<label>Category for all channels from this source (empty = use provider categories)</label><input type="text" id="edCatLabel" value="' + esc(s.categoryLabel || '') + '">' +
            '<label>Logo priority (higher wins over other sources; the iptv-org logo index uses the value from Settings)</label><input type="number" id="edLogoPrio" value="' + (s.logoPriority || 0) + '">' +
            '<label>Country of the channels (two letters, e.g. JP; used for logos, EPG and merging when names carry no region)</label><input type="text" id="edCountry" maxlength="2" value="' + esc(s.country || '') + '">' +
            '<label>EPG (XMLTV) URLs for this source, one per line</label><textarea id="edEpg" rows="3" style="width:100%">' + esc((s.epgUrls || []).join('\n')) + '</textarea>' +
            (isX ? '<label><input type="checkbox" id="edProvEpg" ' + (s.useProviderEpg !== false ? 'checked' : '') + '> Use the account\'s own XMLTV feed (xmltv.php) for EPG</label>' : '') +
            '<label><input type="checkbox" id="edAuto" ' + (s.autoAddChannels !== false ? 'checked' : '') + '> Auto-add new streams as channels</label>' +
            '<label><input type="checkbox" id="edEnabled" ' + (s.enabled !== false ? 'checked' : '') + '> Enabled</label>' +
            '<div class="tb-toolbar"><button class="tb-btn primary" id="edSave">Save</button><button class="tb-btn" id="edCancel">Cancel</button></div>');
        if (isX) { $('#edOut').value = s.xtreamOutput || 'ts'; }
        $('#edSave').addEventListener('click', function () {
            s.name = $('#edName').value.trim(); s.url = $('#edUrl').value.trim(); s.priority = parseInt($('#edPrio').value || '100', 10);
            s.maxConnections = parseInt($('#edMax').value || '0', 10); s.includeCategories = $('#edInc').value.trim() || null; s.excludeCategories = $('#edExc').value.trim() || null; s.categoryLabel = $('#edCatLabel').value.trim() || null;
            s.epgUrls = $('#edEpg').value.split('\n').map(function (x) { return x.trim(); }).filter(Boolean);
            s.autoAddChannels = $('#edAuto').checked; s.enabled = $('#edEnabled').checked; s.logoPriority = parseInt($('#edLogoPrio').value || '0', 10); s.country = $('#edCountry').value.trim().toUpperCase() || null;
            if (isX) { s.username = $('#edUser').value.trim(); s.password = $('#edPass').value; s.xtreamOutput = $('#edOut').value; s.useProviderEpg = $('#edProvEpg').checked; }
            api('POST', 'Sources', s).then(function () { closeEditor(); loadSources(); log('Source saved: ' + s.name); });
        });
        $('#edCancel').addEventListener('click', closeEditor);
    }

    // ---- channels ----
    function loadChannels() {
        var q = { q: $('#tbChanQ').value.trim(), category: $('#tbChanCat').value };
        return api('GET', 'Channels', undefined, q).then(function (list) {
            if ($('#tbChanProblems').checked) {
                list = list.filter(function (c) { return !c.enabled || c.streams.some(function (s) { return s.health.dead || s.missing; }) || !c.hasEpg; });
            }
            state.channels = list;
            $('#tbChanCount').textContent = list.length + ' channels';
            var cats = {}; state.channels.forEach(function (c) { cats[c.category || ''] = 1; });
            var sel = $('#tbChanCat'); var cur = sel.value;
            sel.innerHTML = '<option value="">all categories</option>' + Object.keys(cats).sort().map(function (c) { return '<option value="' + esc(c) + '">' + esc(c || '(none)') + '</option>'; }).join('');
            sel.value = cur;
            $('#tbChannels tbody').innerHTML = list.map(rowChannel).join('');
        });
    }

    function healthPill(h) {
        if (h.dead) { return '<span class="tb-pill tb-dead" title="' + esc(h.lastError || '') + '">dead</span>'; }
        if (h.unknown) { return '<span class="tb-pill tb-unknown">?</span>'; }
        return '<span class="tb-pill tb-ok">' + (h.latencyMs ? h.latencyMs + ' ms' : 'ok') + '</span>';
    }

    function rowChannel(c) {
        var streams = c.streams.map(function (s) {
            return '<div>' + healthPill(s.health) + '<span class="tb-pill">' + esc(s.sourceName || s.sourceId) + '</span>' + (s.variant && s.variant !== 'default' ? '<span class="tb-pill">' + esc(s.variant) + '</span>' : '') + '<span class="' + (s.missing ? 'tb-dead' : '') + '">' + esc(s.name) + '</span>' + (s.enabled ? '' : ' <span class="tb-muted">(off)</span>') + '</div>';
        }).join('');
        return '<tr class="' + (c.enabled ? '' : 'tb-row-disabled') + '" data-id="' + c.id + '">' +
            '<td><input type="checkbox" class="tbSel" data-id="' + c.id + '" ' + (state.selected[c.id] ? 'checked' : '') + '></td>' +
            '<td>' + c.number + '</td><td>' + (c.logo ? '<img class="tb-logo" src="' + esc(c.logo) + '" loading="lazy">' : '') + '</td>' +
            '<td><b>' + esc(c.name) + '</b>' + (c.nameMode === 'FollowPrimary' ? ' <span class="tb-pill" title="name follows provider">↻</span>' : '') + (c.pinned ? ' <span class="tb-pill" title="edited by hand">pinned</span>' : '') + '</td>' +
            '<td>' + esc(c.category) + '</td><td>' + streams + '</td>' +
            '<td>' + (c.hasEpg ? '<span class="tb-pill tb-ok">EPG</span>' : (c.epgIds.length ? '<span class="tb-pill tb-unknown" title="ids: ' + esc(c.epgIds.join(', ')) + '">id, no data</span>' : '<span class="tb-pill">–</span>')) + '</td>' +
            '<td><button class="tb-btn" data-action="editChannel" data-id="' + c.id + '">Edit</button></td></tr>';
    }

    function editChannel(c) {
        var streams = c.streams.map(function (s, i) {
            return '<li data-ref="' + esc(s.ref) + '"><span class="tb-handle">☰</span><input type="checkbox" class="edStreamOn" ' + (s.enabled ? 'checked' : '') + '>' + healthPill(s.health) + '<span class="tb-pill">' + esc(s.sourceName || s.sourceId) + '</span>' + esc(s.name) + ' <span class="tb-muted">' + esc(s.category) + '</span>' +
                ' <button class="tb-btn" data-up="' + i + '" title="move up">↑</button><button class="tb-btn" data-probe="' + esc(s.ref) + '">probe</button><button class="tb-btn" data-split="' + esc(s.ref) + '" title="move to its own channel">split</button></li>';
        }).join('');
        openEditor('<h3>' + esc(c.name) + '</h3>' +
            '<label>Name</label><input type="text" id="edName" value="' + esc(c.name) + '">' +
            '<label>Name mode</label><select id="edMode"><option value="Fixed">Fixed</option><option value="FollowPrimary">Follow primary stream (event channels)</option></select>' +
            '<label>Number</label><input type="number" id="edNumber" value="' + c.number + '">' +
            '<label>Category</label><input type="text" id="edCategory" value="' + esc(c.category) + '">' +
            '<label>Logo URL</label><input type="text" id="edLogo" value="' + esc(c.logo || '') + '">' +
            '<label>EPG ids (XMLTV channel ids, comma separated; first with data wins)</label><input type="text" id="edEpg" value="' + esc(c.epgIds.join(', ')) + '"> <button class="tb-btn" id="edEpgFind">find…</button><div id="edEpgHits" class="tb-muted"></div>' +
            '<label><input type="checkbox" id="edEnabled" ' + (c.enabled ? 'checked' : '') + '> Enabled</label>' +
            '<label>Streams (order = failover order)</label><ul class="tb-streams" id="edStreams">' + streams + '</ul>' +
            '<label>Add stream from catalog (search)</label><input type="text" id="edAddQ" placeholder="type to search provider names"><div id="edAddHits" class="tb-muted"></div>' +
            '<div class="tb-toolbar"><button class="tb-btn primary" id="edSave">Save</button><button class="tb-btn" id="edCancel">Cancel</button><button class="tb-btn danger" id="edDelete">Delete channel</button></div>');
        $('#edMode').value = c.nameMode;
        var working = JSON.parse(JSON.stringify(c));
        var rerenderStreams = function () {
            $('#edStreams').innerHTML = working.streams.map(function (s, i) {
                return '<li data-ref="' + esc(s.ref) + '"><span class="tb-handle">☰</span><input type="checkbox" class="edStreamOn" ' + (s.enabled ? 'checked' : '') + '>' + healthPill(s.health || {}) + '<span class="tb-pill">' + esc(s.sourceName || s.sourceId) + '</span>' + esc(s.name) + ' <button class="tb-btn" data-up="' + i + '">↑</button><button class="tb-btn" data-probe="' + esc(s.ref) + '">probe</button><button class="tb-btn" data-split="' + esc(s.ref) + '">split</button><button class="tb-btn" data-rm="' + i + '">✕</button></li>';
            }).join('');
        };
        rerenderStreams();
        $('#edStreams').addEventListener('click', function (e) {
            var t = e.target;
            if (t.dataset.up !== undefined) { var i = +t.dataset.up; if (i > 0) { var tmp = working.streams[i - 1]; working.streams[i - 1] = working.streams[i]; working.streams[i] = tmp; rerenderStreams(); } }
            if (t.dataset.rm !== undefined) { working.streams.splice(+t.dataset.rm, 1); rerenderStreams(); }
            if (t.dataset.probe) { t.textContent = '…'; api('POST', 'Health/Probe', { ref: t.dataset.probe }).then(function (h) { t.textContent = h.consecutiveFailures ? 'dead: ' + (h.lastError || '') : 'ok ' + h.latencyMs + ' ms'; }); }
            if (t.dataset.split) { api('POST', 'Channels/' + c.id + '/Split', { ref: t.dataset.split }).then(function () { closeEditor(); loadChannels(); }); }
        });
        $('#edStreams').addEventListener('change', function (e) {
            if (e.target.classList.contains('edStreamOn')) { var ref = e.target.closest('li').dataset.ref; working.streams.forEach(function (s) { if (s.ref === ref) { s.enabled = e.target.checked; } }); }
        });
        var addTimer = null;
        $('#edAddQ').addEventListener('input', function () {
            clearTimeout(addTimer);
            var q = $('#edAddQ').value.trim();
            if (q.length < 2) { $('#edAddHits').innerHTML = ''; return; }
            addTimer = setTimeout(function () {
                api('GET', 'Catalog', undefined, { q: q, take: 15 }).then(function (r) {
                    $('#edAddHits').innerHTML = r.items.map(function (s) { return '<div><button class="tb-btn" data-add="' + esc(s.ref) + '" data-name="' + esc(s.name) + '" data-src="' + esc(s.sourceId) + '">+</button> ' + esc(s.name) + ' <span class="tb-pill">' + esc(s.category) + '</span>' + (s.channelName ? ' <span class="tb-muted">in ' + esc(s.channelName) + '</span>' : '') + '</div>'; }).join('');
                });
            }, 300);
        });
        $('#edAddHits').addEventListener('click', function (e) {
            var b = e.target.closest('button[data-add]');
            if (!b) { return; }
            if (working.streams.some(function (s) { return s.ref === b.dataset.add; })) { return; }
            working.streams.push({ ref: b.dataset.add, sourceId: b.dataset.src, name: b.dataset.name, enabled: true, health: {} });
            rerenderStreams();
        });
        $('#edEpgFind').addEventListener('click', function () {
            api('GET', 'Epg/Channels', undefined, { q: $('#edName').value }).then(function (list) {
                $('#edEpgHits').innerHTML = list.slice(0, 20).map(function (x) { return '<div><button class="tb-btn" data-epg="' + esc(x.id) + '">use</button> ' + esc(x.id) + ' <span class="tb-muted">' + esc(x.name) + (x.hasPrograms ? '' : ' (no programmes)') + '</span></div>'; }).join('') || 'no XMLTV channel matches';
            });
        });
        $('#edEpgHits').addEventListener('click', function (e) {
            var b = e.target.closest('button[data-epg]');
            if (b) { var cur = $('#edEpg').value.trim(); $('#edEpg').value = cur ? b.dataset.epg + ', ' + cur : b.dataset.epg; }
        });
        $('#edSave').addEventListener('click', function () {
            var payload = {
                id: c.id, name: $('#edName').value.trim(), nameMode: $('#edMode').value, matchKey: c.matchKey, number: parseInt($('#edNumber').value || '0', 10),
                category: $('#edCategory').value.trim(), tags: c.tags || [], logo: $('#edLogo').value.trim() || null,
                epgIds: $('#edEpg').value.split(',').map(function (x) { return x.trim(); }).filter(Boolean),
                enabled: $('#edEnabled').checked, auto: c.auto, pinned: true, region: c.region, createdUtc: c.createdUtc || new Date().toISOString(),
                streams: working.streams.map(function (s, i) { return { ref: s.ref, priority: i, enabled: s.enabled !== false, matchedName: s.name, matchedCategory: s.category, variant: s.variant }; })
            };
            api('POST', 'Channels', payload).then(function () { closeEditor(); loadChannels(); log('Channel saved: ' + payload.name); });
        });
        $('#edCancel').addEventListener('click', closeEditor);
        $('#edDelete').addEventListener('click', function () { if (confirm('Delete channel "' + c.name + '"?')) { api('DELETE', 'Channels/' + c.id).then(function () { closeEditor(); loadChannels(); }); } });
    }

    function selectedIds() { return Object.keys(state.selected).filter(function (k) { return state.selected[k]; }); }

    // ---- catalog ----
    function loadCatalogFilters() {
        if (!state.sources.length) { loadSources(); }
        api('GET', 'Catalog/Categories').then(function (cats) {
            var sel = $('#tbCatCategory');
            sel.innerHTML = '<option value="">all categories</option>' + cats.map(function (c) { return '<option value="' + esc(c.category) + '">' + esc(c.category) + ' (' + c.count + ')</option>'; }).join('');
        });
    }
    function loadCatalog() {
        var q = { sourceId: $('#tbCatSource').value || undefined, category: $('#tbCatCategory').value || undefined, q: $('#tbCatQ').value.trim() || undefined, take: 300 };
        api('GET', 'Catalog', undefined, q).then(function (r) {
            $('#tbCatCount').textContent = r.total + ' streams';
            $('#tbCatalog tbody').innerHTML = r.items.map(function (s) {
                return '<tr><td>' + (s.logo ? '<img class="tb-logo" src="' + esc(s.logo) + '" loading="lazy">' : '') + '</td><td>' + esc(s.name) + (s.isDash ? ' <span class="tb-pill tb-dead">DASH</span>' : '') + '</td><td class="tb-muted">' + esc(s.category) + '</td><td class="tb-muted">' + esc(s.epgId || '') + '</td><td>' + healthPill(s.health) + '</td>' +
                    '<td>' + (s.channelName ? esc(s.channelName) : '<span class="tb-muted">unassigned</span>') + '</td>' +
                    '<td>' + (s.channelId ? '' : '<button class="tb-btn" data-action="createFromStream" data-ref="' + esc(s.ref) + '" data-name="' + esc(s.name) + '" data-cat="' + esc(s.category) + '" data-src="' + esc(s.sourceId) + '" data-epg="' + esc(s.epgId || '') + '" data-logo="' + esc(s.logo || '') + '">+ channel</button>') + '</td></tr>';
            }).join('');
        });
    }

    // ---- rules ----
    function loadRules() {
        var ready = state.sources.length ? Promise.resolve() : api('GET', 'Sources').then(function (list) { state.sources = list; });
        ready.then(function () { return api('GET', 'Rules'); }).then(function (rules) { state.rules = rules; renderRules(); });
    }
    function renderRules() {
        $('#tbRules tbody').innerHTML = state.rules.map(function (r, i) {
            return '<tr data-i="' + i + '"><td><input type="checkbox" data-f="enabled" ' + (r.enabled ? 'checked' : '') + '></td>' +
                '<td><input type="text" data-f="name" value="' + esc(r.name) + '" style="width:9em"></td>' +
                '<td><select multiple data-f="sourceIds" size="2" title="Sources the rule applies to; none selected = all sources" style="width:9em">' + state.sources.map(function (s) { return '<option value="' + s.id + '"' + ((r.sourceIds || []).indexOf(s.id) >= 0 ? ' selected' : '') + '>' + esc(s.name) + '</option>'; }).join('') + '</select></td>' +
                '<td><input type="text" data-f="categoryPattern" value="' + esc(r.categoryPattern || '') + '" style="width:13em"></td>' +
                '<td><input type="text" data-f="namePattern" value="' + esc(r.namePattern || '') + '" style="width:11em"></td>' +
                '<td><input type="text" data-f="category" value="' + esc(r.category) + '" style="width:9em"></td>' +
                '<td><input type="number" data-f="numberStart" value="' + (r.numberStart || 0) + '" style="width:6em"></td>' +
                '<td><input type="checkbox" data-f="autoAdd" ' + (r.autoAdd ? 'checked' : '') + '></td>' +
                '<td><input type="checkbox" data-f="followProviderName" ' + (r.followProviderName ? 'checked' : '') + '></td>' +
                '<td><input type="checkbox" data-f="exclude" ' + (r.exclude ? 'checked' : '') + '></td>' +
                '<td><button class="tb-btn" data-action="ruleUp" data-i="' + i + '">↑</button><button class="tb-btn danger" data-action="ruleDel" data-i="' + i + '">✕</button></td></tr>';
        }).join('') || '<tr><td colspan="11" class="tb-muted">No rules. Without rules every stream becomes a channel in its provider category, numbered from the provider number.</td></tr>';
    }
    function collectRules() {
        $$('#tbRules tbody tr[data-i]').forEach(function (tr) {
            var r = state.rules[+tr.dataset.i];
            $$('[data-f]', tr).forEach(function (inp) {
                if (inp.tagName === 'SELECT' && inp.multiple) { r[inp.dataset.f] = Array.prototype.filter.call(inp.options, function (o) { return o.selected; }).map(function (o) { return o.value; }); return; }
                r[inp.dataset.f] = inp.type === 'checkbox' ? inp.checked : (inp.type === 'number' ? parseInt(inp.value || '0', 10) : inp.value);
            });
        });
    }

    // ---- epg ----
    function loadEpg() {
        api('GET', 'Epg/Urls').then(function (urls) { $('#tbEpgUrls').value = urls.join('\n'); });
    }

    // ---- editor drawer ----
    function openEditor(html) { var ed = $('#tbEditor'); ed.innerHTML = html; ed.hidden = false; }
    function closeEditor() { $('#tbEditor').hidden = true; }

    // ---- settings ----
    var numFields = ['SourceRefreshMinutes', 'HealthCheckHours', 'HealthCheckConcurrencyPerSource', 'DeadAfterFailures', 'StreamBufferMiB', 'StreamStartTimeoutSeconds', 'StreamStallTimeoutSeconds', 'EpgRefreshHours', 'LogoIndexPriority', 'LogoCacheDays', 'StreamLingerSeconds', 'WatchdogIdleSeconds'];
    var boolFields = ['HideDeadChannels', 'FollowProviderNamesByDefault', 'EnableChannelFolders', 'EnableWebCategoryFilter', 'AutoRefreshGuide', 'EnableLogoIndex', 'ProbeSingleConnectionSources', 'WatchdogRequireSession', 'AutoMapEpgByName', 'ShowNowPlayingInFolderNames'];
    function loadSettings() {
        ApiClient.getPluginConfiguration(pluginId).then(function (cfg) {
            numFields.forEach(function (f) { $('#' + f).value = cfg[f]; });
            boolFields.forEach(function (f) { $('#' + f).checked = !!cfg[f]; });
            $('#UserAgent').value = cfg.UserAgent || '';
        });
    }
    $('#TerebiConfigForm').addEventListener('submit', function (e) {
        e.preventDefault();
        ApiClient.getPluginConfiguration(pluginId).then(function (cfg) {
            numFields.forEach(function (f) { cfg[f] = parseInt($('#' + f).value || '0', 10); });
            boolFields.forEach(function (f) { cfg[f] = $('#' + f).checked; });
            cfg.UserAgent = $('#UserAgent').value;
            return ApiClient.updatePluginConfiguration(pluginId, cfg);
        }).then(function (r) { Dashboard.processPluginConfigurationUpdateResult(r); });
        return false;
    });

    // ---- global click handler ----
    view.addEventListener('click', function (e) {
        var b = e.target.closest('[data-action]');
        if (!b) { return; }
        var a = b.dataset.action;
        if (a === 'refreshSources') { api('POST', 'Sources/Refresh').then(function () { log('Source refresh started (see Jellyfin log for progress)'); setTimeout(loadStatus, 8000); }); }
        if (a === 'runHealth') { api('POST', 'Health/Run').then(function () { log('Health check started'); }); }
        if (a === 'refreshEpg') { api('POST', 'Epg/Refresh').then(function () { log('EPG download started'); }); }
        if (a === 'refreshGuide') { api('POST', 'Guide/Refresh').then(function () { log('Jellyfin guide refresh started'); }); }
        if (a === 'refreshLogos') { api('POST', 'Logos/Refresh').then(function () { log('Logo refresh started (index, lineup, downloads, guide)'); }); }
        if (a === 'importXtream') { api('POST', 'Import/XtreamPlugin').then(function (r) { log(r.message + ' (' + r.numbersRemembered + ' numbers)'); loadSources(); }).catch(function (x) { log('Import failed: ' + (x && x.status)); }); }
        if (a === 'exportAll') {
            api('GET', 'Export', undefined, { includeSecrets: confirm('Include source passwords in the export?') }).then(function (data) {
                var w = window.open('', '_blank');
                if (w) { w.document.write('<pre>' + esc(JSON.stringify(data, null, 2)) + '</pre>'); w.document.title = 'terebi-export.json'; } else { log('Popup blocked; export printed to console'); console.log(JSON.stringify(data)); }
            });
        }
        if (a === 'importAll') {
            var txt = prompt('Paste an export JSON (this replaces sources, channels and rules):');
            if (txt) { try { api('POST', 'Import', JSON.parse(txt)).then(function (r) { log('Imported: ' + r.sources + ' sources, ' + r.channels + ' channels, ' + r.rules + ' rules'); loadSources(); }); } catch (e) { log('Invalid JSON'); } }
        }
        if (a === 'newSource') { editSource({ id: Math.random().toString(16).slice(2, 10), name: '', type: b.dataset.type, enabled: true, priority: 100, url: '', epgUrls: [], autoAddChannels: true, xtreamOutput: 'ts' }); }
        if (a === 'editSource') { editSource(state.sources.find(function (s) { return s.id === b.dataset.src; })); }
        if (a === 'testSource') { b.textContent = '…'; api('POST', 'Sources/' + b.dataset.src + '/Test').then(function (r) { b.textContent = 'Test'; log((r.ok ? 'OK: ' : 'FAILED: ') + r.message); loadSources(); }); }
        if (a === 'deleteSource') { if (confirm('Delete this source and detach its streams?')) { api('DELETE', 'Sources/' + b.dataset.src).then(loadSources); } }
        if (a === 'loadChannels') { loadChannels(); }
        if (a === 'editChannel') { editChannel(state.channels.find(function (c) { return c.id === b.dataset.id; })); }
        if (a === 'bulkMerge') { var ids = selectedIds(); if (ids.length > 1) { api('POST', 'Channels/Merge', { ids: ids }).then(function () { state.selected = {}; loadChannels(); }); } }
        if (a === 'bulkEnable' || a === 'bulkDisable') { api('POST', 'Channels/Bulk', { ids: selectedIds(), enabled: a === 'bulkEnable' }).then(loadChannels); }
        if (a === 'bulkCategory') { var cat = prompt('Category for selected channels'); if (cat !== null) { api('POST', 'Channels/Bulk', { ids: selectedIds(), category: cat }).then(loadChannels); } }
        if (a === 'bulkRenumber') { var from = parseInt(prompt('First number') || '0', 10); if (from > 0) { api('POST', 'Channels/Bulk', { ids: selectedIds(), renumberFrom: from }).then(loadChannels); } }
        if (a === 'bulkFollow') { api('POST', 'Channels/Bulk', { ids: selectedIds(), nameMode: 'FollowPrimary' }).then(loadChannels); }
        if (a === 'bulkDelete') { if (confirm('Delete ' + selectedIds().length + ' channels?')) { api('POST', 'Channels/Bulk', { ids: selectedIds(), delete: true }).then(function () { state.selected = {}; loadChannels(); }); } }
        if (a === 'rebuild') { api('POST', 'Channels/Rebuild').then(function (r) { log('Rebuild: ' + r.summary); loadChannels(); }); }
        if (a === 'applyNumbers') { api('POST', 'Import/ApplyNumbers').then(function (r) { log('Applied imported numbers to ' + r.applied + ' channels'); loadChannels(); }); }
        if (a === 'suggestions') {
            api('GET', 'Channels/Suggestions').then(function (groups) {
                $('#tbSuggestions').innerHTML = groups.length ? groups.map(function (g) {
                    return '<div class="tb-card"><b>' + esc(g.key) + '</b> ' + g.channels.map(function (c) { return '<span class="tb-pill">' + c.number + ' ' + esc(c.name) + '</span>'; }).join('') + ' <button class="tb-btn primary" data-action="mergeGroup" data-ids="' + g.channels.map(function (c) { return c.id; }).join(',') + '">merge</button></div>';
                }).join('') : '<div class="tb-muted">No merge suggestions.</div>';
            });
        }
        if (a === 'mergeGroup') { api('POST', 'Channels/Merge', { ids: b.dataset.ids.split(',') }).then(function () { b.closest('.tb-card').remove(); loadChannels(); }); }
        if (a === 'loadCatalog') { loadCatalog(); }
        if (a === 'createFromStream') {
            var payload = { name: b.dataset.name, number: 0, category: b.dataset.cat, logo: b.dataset.logo || null, epgIds: b.dataset.epg ? [b.dataset.epg] : [], enabled: true, auto: false, pinned: true, tags: [], streams: [{ ref: b.dataset.ref, priority: 0, enabled: true }] };
            api('POST', 'Channels', payload).then(function () { log('Channel created: ' + payload.name); loadCatalog(); });
        }
        if (a === 'loadPreset') {
            api('GET', 'Rules/Presets').then(function (names) {
                var pn = prompt('Available presets: ' + names.join(', ') + '\nType the preset to load (replaces current rules):', names[0] || '');
                if (pn && names.indexOf(pn) >= 0) { api('POST', 'Rules/Presets/' + pn).then(function () { log('Preset loaded: ' + pn); loadRules(); }); }
            });
        }
        if (a === 'reflow') { if (confirm('Re-apply rules to every non-pinned channel (categories and numbers will be recomputed)?')) { api('POST', 'Channels/Reflow').then(function (r) { log('Reflow: ' + r.summary); loadChannels(); }); } }
        if (a === 'addRule') { collectRules(); state.rules.push({ id: Math.random().toString(16).slice(2, 10), name: 'New rule', enabled: true, sourceIds: [], categoryPattern: '', namePattern: '', category: '', numberStart: 0, autoAdd: true, followProviderName: false, exclude: false }); renderRules(); }
        if (a === 'ruleUp') { collectRules(); var i = +b.dataset.i; if (i > 0) { var t = state.rules[i - 1]; state.rules[i - 1] = state.rules[i]; state.rules[i] = t; renderRules(); } }
        if (a === 'ruleDel') { collectRules(); state.rules.splice(+b.dataset.i, 1); renderRules(); }
        if (a === 'saveRules') { collectRules(); api('POST', 'Rules', state.rules).then(function () { log('Rules saved'); }); }
        if (a === 'saveEpgUrls') { api('POST', 'Epg/Urls', $('#tbEpgUrls').value.split('\n').map(function (x) { return x.trim(); }).filter(Boolean)).then(function () { log('EPG URLs saved'); }); }
        if (a === 'epgSuggestions') {
            api('GET', 'Epg/Suggestions').then(function (list) {
                $('#tbEpgSuggestions').innerHTML = list.length ? list.map(function (s) { return '<div>' + esc(s.channel) + ' → ' + s.hits.map(function (h) { return '<button class="tb-btn" data-action="epgApply" data-ch="' + s.channelId + '" data-id="' + esc(h.id) + '">' + esc(h.id) + (h.hasPrograms ? '' : ' (no data)') + '</button>'; }).join(' ') + '</div>'; }).join('') : '<div class="tb-muted">Nothing to suggest.</div>';
                state.epgSuggestions = list;
            });
        }
        if (a === 'epgApply') { applyEpg(b.dataset.ch, b.dataset.id).then(function () { b.textContent = '✓ ' + b.dataset.id; }); }
        if (a === 'epgApplyAll') { (state.epgSuggestions || []).reduce(function (p, s) { return p.then(function () { return applyEpg(s.channelId, s.hits[0].id); }); }, Promise.resolve()).then(function () { log('EPG ids applied'); }); }
    });
    function applyEpg(channelId, epgId) {
        return api('GET', 'Channels/' + channelId).then(function (c) {
            if (c.epgIds.indexOf(epgId) < 0) { c.epgIds.unshift(epgId); }
            c.streams = c.streams.map(function (s, i) { return { ref: s.ref, priority: i, enabled: s.enabled, matchedName: s.name, matchedCategory: s.category, variant: s.variant }; });
            return api('POST', 'Channels', c);
        });
    }
    view.addEventListener('change', function (e) {
        if (e.target.classList.contains('tbSel')) { state.selected[e.target.dataset.id] = e.target.checked; }
        if (e.target.id === 'tbChanAll') { $$('.tbSel').forEach(function (cb) { cb.checked = e.target.checked; state.selected[cb.dataset.id] = e.target.checked; }); }
    });
    $('#tbChanQ').addEventListener('keydown', function (e) { if (e.key === 'Enter') { loadChannels(); } });
    $('#tbCatQ').addEventListener('keydown', function (e) { if (e.key === 'Enter') { loadCatalog(); } });

    view.addEventListener('viewshow', function () {
        loadStatus();
        loadSources();
        loadSettings();
    });
}
