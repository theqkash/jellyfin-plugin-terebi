/* Terebi tab for Jellyfin's Live TV page: categories on the left, channels with logo, now/next and a progress
 * bar on the right, one click to play. Loaded from index.html through the File Transformation plugin and
 * attached to the header tabs of the Live TV page whenever that page is shown. */
(function () {
    'use strict';
    if (window.__terebiTvLoaded) { return; }
    window.__terebiTvLoaded = true;

    var KEY_CAT = 'terebi.tv.category';
    var KEY_LAST = 'terebi.tv.wasActive';
    var TAB_INDEX = 'terebi';
    var state = { categories: [], byNumber: {}, channels: [], nextByChannel: {}, category: '', query: '', sessionId: null, timer: null, tick: null, loading: false, active: false };

    function store(k, v) { try { if (v === null || v === '') { localStorage.removeItem(k); } else { localStorage.setItem(k, v); } } catch (e) { /* ignore */ } }
    function load(k) { try { return localStorage.getItem(k) || ''; } catch (e) { return ''; } }
    function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
    function api(client, method, path, query, body) {
        var opts = { type: method, url: client.getUrl(path, query || {}), dataType: 'json' };
        if (body !== undefined) { opts.contentType = 'application/json'; opts.data = JSON.stringify(body); }
        return client.ajax(opts);
    }
    function fmtTime(iso) { var d = new Date(iso); return (d.getHours() < 10 ? '0' : '') + d.getHours() + ':' + (d.getMinutes() < 10 ? '0' : '') + d.getMinutes(); }

    // ---- styles ---------------------------------------------------------------------------
    var css = [
        '.terebiTv{display:flex;gap:1.2em;padding:0 3.3%;box-sizing:border-box;min-height:60vh}',
        '.terebiTv-side{flex:0 0 15em;max-width:15em;position:sticky;top:6em;align-self:flex-start;max-height:calc(100vh - 8em);overflow:auto}',
        '.terebiTv-search{width:100%;box-sizing:border-box;padding:.6em .8em;border-radius:.4em;border:1px solid rgba(255,255,255,.18);background:rgba(255,255,255,.07);color:inherit;font-size:1em;margin:0 0 .8em 0}',
        '.terebiTv-cat{display:flex;justify-content:space-between;align-items:center;padding:.55em .8em;border-radius:.4em;cursor:pointer;color:inherit;opacity:.85;border:0;background:transparent;width:100%;text-align:left;font:inherit}',
        '.terebiTv-cat:hover{background:rgba(255,255,255,.08);opacity:1}',
        '.terebiTv-cat.is-active{background:#00a4dc;color:#fff;opacity:1}',
        '.terebiTv-cat small{opacity:.7;font-size:.8em;margin-left:.6em}',
        '.terebiTv-main{flex:1 1 auto;min-width:0}',
        '.terebiTv-head{display:flex;align-items:baseline;justify-content:space-between;margin:.2em 0 .8em 0}',
        '.terebiTv-head h2{margin:0;font-size:1.4em}',
        '.terebiTv-head span{opacity:.6;font-size:.9em}',
        '.terebiTv-list{display:block}',
        '.terebiTv-row{display:grid;grid-template-columns:7em 3.2em minmax(10em,1.1fr) minmax(0,2fr);gap:0 1em;align-items:center;padding:.5em .6em;border-radius:.5em;cursor:pointer;border-bottom:1px solid rgba(255,255,255,.06)}',
        '.terebiTv-row:hover{background:rgba(255,255,255,.08)}',
        '.terebiTv-logo{width:7em;height:3.94em;border-radius:.4em;background:#1e1f26 center/cover no-repeat;flex:none}',
        '.terebiTv-num{opacity:.6;font-variant-numeric:tabular-nums;text-align:right}',
        '.terebiTv-name{font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}',
        '.terebiTv-prog{min-width:0}',
        '.terebiTv-now{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}',
        '.terebiTv-now b{font-weight:600}',
        '.terebiTv-bar{height:3px;background:rgba(255,255,255,.15);border-radius:2px;margin:.35em 0;overflow:hidden}',
        '.terebiTv-bar i{display:block;height:100%;background:#00a4dc;width:0}',
        '.terebiTv-next{opacity:.6;font-size:.88em;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}',
        '.terebiTv-empty{opacity:.6;padding:2em;text-align:center}',
        '.terebiTv-play{opacity:0;transition:opacity .15s;justify-self:end;font-size:1.6em;grid-column:4;grid-row:1}',
        '@media (max-width:900px){.terebiTv{flex-direction:column}.terebiTv-side{position:static;max-width:none;flex-basis:auto;max-height:none;display:flex;flex-wrap:wrap;gap:.3em}.terebiTv-search{flex-basis:100%}.terebiTv-cat{width:auto}.terebiTv-row{grid-template-columns:5em 2.6em 1fr}.terebiTv-prog{grid-column:1 / -1}}'
    ].join('\n');
    function ensureStyle() {
        if (document.getElementById('terebiTvStyle')) { return; }
        var st = document.createElement('style'); st.id = 'terebiTvStyle'; st.textContent = css; document.head.appendChild(st);
    }

    // ---- data -----------------------------------------------------------------------------
    function loadCategories(client) {
        return api(client, 'GET', 'Terebi/Public/Categories').then(function (r) {
            state.categories = r.categories || [];
            state.byNumber = {};
            state.categories.forEach(function (c) { c.numbers.forEach(function (n) { state.byNumber[String(n)] = c.name; }); });
        }).catch(function () { state.categories = []; });
    }

    function loadChannels(client) {
        return api(client, 'GET', 'LiveTv/Channels', { UserId: client.getCurrentUserId(), Limit: 5000, AddCurrentProgram: true, EnableUserData: false, EnableImageTypes: 'Primary', ImageTypeLimit: 1, SortBy: 'SortName' })
            .then(function (r) {
                state.channels = (r.Items || []).map(function (i) {
                    return { id: i.Id, name: i.Name, number: parseInt(i.ChannelNumber, 10) || 0, numberText: i.ChannelNumber || '', img: i.ImageTags && i.ImageTags.Primary ? client.getUrl('Items/' + i.Id + '/Images/Primary', { maxHeight: 120, tag: i.ImageTags.Primary, quality: 90 }) : null, now: i.CurrentProgram || null, category: state.byNumber[String(i.ChannelNumber)] || 'Other' };
                }).sort(function (a, b) { return a.number - b.number; });
            });
    }

    function loadNext(client, channels) {
        // Next programme per channel, in slices, without blocking the first paint.
        var pending = channels.filter(function (c) { return state.nextByChannel[c.id] === undefined; });
        var slice = pending.slice(0, 50);
        if (!slice.length) { return Promise.resolve(); }
        var now = new Date().toISOString();
        return api(client, 'GET', 'LiveTv/Programs', { ChannelIds: slice.map(function (c) { return c.id; }).join(','), MinStartDate: now, SortBy: 'StartDate', Limit: slice.length * 3, EnableUserData: false, EnableImages: false })
            .then(function (r) {
                var seen = {};
                (r.Items || []).forEach(function (p) {
                    // The first programme that starts after now is "next"; the one on air is already known.
                    if (!seen[p.ChannelId] && new Date(p.StartDate) > new Date()) { seen[p.ChannelId] = true; state.nextByChannel[p.ChannelId] = p; }
                });
                slice.forEach(function (c) { if (state.nextByChannel[c.id] === undefined) { state.nextByChannel[c.id] = null; } });
                renderRowsNext();
                if (pending.length > slice.length && state.active) { return loadNext(client, channels); }
            }).catch(function () { /* next is a nicety */ });
    }

    function findSession(client) {
        if (state.sessionId) { return Promise.resolve(state.sessionId); }
        return api(client, 'GET', 'Sessions', { DeviceId: client.deviceId() }).then(function (list) {
            var s = (list || [])[0];
            state.sessionId = s ? s.Id : null;
            return state.sessionId;
        }).catch(function () { return null; });
    }

    function play(client, channel) {
        // The web client accepts remote play commands for its own session, which starts playback in place.
        findSession(client).then(function (sid) {
            if (!sid) { throw new Error('no session'); }
            return api(client, 'POST', 'Sessions/' + sid + '/Playing', { ItemIds: channel.id, PlayCommand: 'PlayNow' });
        }).catch(function () {
            window.location.hash = '#/details?id=' + channel.id + '&serverId=' + client.serverId();
        });
    }

    // ---- rendering ------------------------------------------------------------------------
    function visibleChannels() {
        var q = state.query.trim().toLowerCase();
        return state.channels.filter(function (c) {
            if (state.category && c.category !== state.category) { return false; }
            if (q && c.name.toLowerCase().indexOf(q) < 0 && c.numberText.indexOf(q) < 0 && !(c.now && (c.now.Name || '').toLowerCase().indexOf(q) >= 0)) { return false; }
            return true;
        });
    }

    function progress(p) {
        if (!p || !p.StartDate || !p.EndDate) { return 0; }
        var s = new Date(p.StartDate).getTime(), e = new Date(p.EndDate).getTime(), n = Date.now();
        if (e <= s) { return 0; }
        return Math.max(0, Math.min(100, (n - s) / (e - s) * 100));
    }

    function rowHtml(c) {
        var now = c.now;
        var nowText = now ? '<b>' + esc(now.Name) + '</b>' + (now.EpisodeTitle ? ' <span style="opacity:.75">' + esc(now.EpisodeTitle) + '</span>' : '') + ' <span style="opacity:.55">' + fmtTime(now.StartDate) + ' to ' + fmtTime(now.EndDate) + '</span>' : '<span style="opacity:.45">No guide data</span>';
        return '<div class="terebiTv-row itemAction" data-action="play" data-id="' + esc(c.id) + '" data-serverid="' + esc(window.ApiClient ? window.ApiClient.serverId() : '') + '" data-type="TvChannel" data-mediatype="Video" data-isfolder="false" title="' + esc(c.name) + '">' +
            '<div class="terebiTv-logo" style="' + (c.img ? 'background-image:url(&quot;' + esc(c.img) + '&quot;);background-size:cover' : '') + '"></div>' +
            '<div class="terebiTv-num">' + esc(c.numberText) + '</div>' +
            '<div class="terebiTv-name">' + esc(c.name) + '</div>' +
            '<div class="terebiTv-prog"><div class="terebiTv-now">' + nowText + '</div>' +
            (now ? '<div class="terebiTv-bar"><i style="width:' + progress(now).toFixed(1) + '%"></i></div>' : '') +
            '<div class="terebiTv-next" data-next="' + esc(c.id) + '">' + nextText(c.id) + '</div></div>' +
            '</div>';
    }

    function nextText(id) {
        var p = state.nextByChannel[id];
        if (!p) { return p === null ? '' : '&nbsp;'; }
        return 'Next: ' + fmtTime(p.StartDate) + ' ' + esc(p.Name) + (p.EpisodeTitle ? ' ' + esc(p.EpisodeTitle) : '');
    }

    function renderRowsNext() {
        var panel = document.getElementById('terebiTab');
        if (!panel) { return; }
        Array.prototype.forEach.call(panel.querySelectorAll('.terebiTv-next[data-next]'), function (el) { el.innerHTML = nextText(el.getAttribute('data-next')); });
    }

    function renderBars() {
        var panel = document.getElementById('terebiTab');
        if (!panel) { return; }
        var byId = {};
        state.channels.forEach(function (c) { byId[c.id] = c; });
        Array.prototype.forEach.call(panel.querySelectorAll('.terebiTv-row'), function (row) {
            var c = byId[row.getAttribute('data-id')];
            var bar = row.querySelector('.terebiTv-bar i');
            if (c && bar && c.now) {
                if (new Date(c.now.EndDate).getTime() < Date.now()) { refresh(window.ApiClient); return; }
                bar.style.width = progress(c.now).toFixed(1) + '%';
            }
        });
    }

    function render(client) {
        var panel = document.getElementById('terebiTab');
        if (!panel) { return; }
        var total = state.channels.length;
        var list = visibleChannels();
        var cats = '<button class="terebiTv-cat' + (state.category ? '' : ' is-active') + '" data-cat="">All channels<small>' + total + '</small></button>' +
            state.categories.map(function (c) { return '<button class="terebiTv-cat' + (state.category === c.name ? ' is-active' : '') + '" data-cat="' + esc(c.name) + '">' + esc(c.name) + '<small>' + c.numbers.length + '</small></button>'; }).join('');
        panel.innerHTML = '<div class="terebiTv">' +
            '<div class="terebiTv-side"><input class="terebiTv-search" type="search" placeholder="Search channel or programme" value="' + esc(state.query) + '">' + cats + '</div>' +
            '<div class="terebiTv-main"><div class="terebiTv-head"><h2>' + esc(state.category || 'All channels') + '</h2><span>' + list.length + ' channels</span></div>' +
            '<div is="emby-itemscontainer" class="terebiTv-list">' + (list.length ? list.map(rowHtml).join('') : '<div class="terebiTv-empty">' + (state.loading ? 'Loading' : 'Nothing here') + '</div>') + '</div>' +
            '</div></div>';

        var search = panel.querySelector('.terebiTv-search');
        search.addEventListener('input', function () { state.query = search.value; renderMainOnly(client); });
        panel.querySelector('.terebiTv-side').addEventListener('click', function (e) {
            var b = e.target.closest('.terebiTv-cat');
            if (!b) { return; }
            state.category = b.getAttribute('data-cat') || '';
            store(KEY_CAT, state.category);
            render(client);
            loadNext(client, visibleChannels());
        });
        bindFallbackPlay(client, panel);
    }

    // Jellyfin's items container starts playback itself for ".itemAction[data-action=play]" rows. When the
    // element did not upgrade (older web client), fall back to the session play command.
    function bindFallbackPlay(client, panel) {
        var list = panel.querySelector('.terebiTv-list');
        if (!list || typeof list.refreshItems === 'function' || list.getAttribute('data-terebi-fallback')) { return; }
        list.setAttribute('data-terebi-fallback', '1');
        list.addEventListener('click', function (e) {
            var row = e.target.closest('.terebiTv-row');
            if (!row) { return; }
            var id = row.getAttribute('data-id');
            var c = state.channels.filter(function (x) { return x.id === id; })[0];
            if (c) { play(client, c); }
        });
    }

    function renderMainOnly(client) {
        var panel = document.getElementById('terebiTab');
        var main = panel && panel.querySelector('.terebiTv-main');
        if (!main) { return; }
        var list = visibleChannels();
        main.innerHTML = '<div class="terebiTv-head"><h2>' + esc(state.category || 'All channels') + '</h2><span>' + list.length + ' channels</span></div>' +
            '<div is="emby-itemscontainer" class="terebiTv-list">' + (list.length ? list.map(rowHtml).join('') : '<div class="terebiTv-empty">Nothing here</div>') + '</div>';
        bindFallbackPlay(client, panel);
        loadNext(client, list);
    }

    function refresh(client) {
        if (state.loading) { return; }
        state.loading = true;
        Object.keys(state.nextByChannel).forEach(function (id) {
            var p = state.nextByChannel[id];
            if (p === null || (p && new Date(p.StartDate).getTime() <= Date.now())) { delete state.nextByChannel[id]; }
        });
        loadCategories(client).then(function () { return loadChannels(client); }).then(function () {
            state.loading = false;
            render(client);
            loadNext(client, visibleChannels());
        }).catch(function () {
            state.loading = false;
            var panel = document.getElementById('terebiTab');
            if (panel) { panel.innerHTML = '<div class="terebiTv-empty">Could not load channels. Is Terebi running?</div>'; }
        });
    }

    // ---- tab wiring -----------------------------------------------------------------------
    function livePage() {
        var page = document.getElementById('liveTvSuggestedPage');
        return page && !page.classList.contains('hide') ? page : null;
    }

    function activate(client) {
        var page = livePage();
        if (!page) { return; }
        state.active = true;
        store(KEY_LAST, '1');
        page.classList.add('terebi-active');
        var panel = document.getElementById('terebiTab');
        if (!panel) {
            panel = document.createElement('div');
            panel.id = 'terebiTab';
            panel.className = 'pageTabContent';
            panel.style.cssText = 'display:block;padding-top:.5em';
            var container = page.querySelector('.liveTvContainer') || page;
            container.appendChild(panel);
        }
        panel.style.display = 'block';
        panel.classList.add('is-active');
        Array.prototype.forEach.call(page.querySelectorAll('.pageTabContent'), function (el) { if (el !== panel) { el.classList.add('terebi-hide'); el.classList.remove('is-active'); } });
        var slider = document.querySelector('.headerTabs .emby-tabs-slider');
        if (slider) {
            Array.prototype.forEach.call(slider.querySelectorAll('.emby-tab-button'), function (b) { b.classList.toggle('is-active', b.getAttribute('data-index') === TAB_INDEX); });
        }
        refresh(client);
        clearInterval(state.timer); state.timer = setInterval(function () { if (state.active && livePage()) { refresh(client); } }, 60000);
        clearInterval(state.tick); state.tick = setInterval(function () { if (state.active) { renderBars(); } }, 15000);
    }

    function deactivate() {
        state.active = false;
        store(KEY_LAST, '');
        clearInterval(state.timer); clearInterval(state.tick);
        var page = document.getElementById('liveTvSuggestedPage');
        if (page) {
            page.classList.remove('terebi-active');
            Array.prototype.forEach.call(page.querySelectorAll('.pageTabContent.terebi-hide'), function (el) { el.classList.remove('terebi-hide'); });
        }
        var panel = document.getElementById('terebiTab');
        if (panel) { panel.style.display = 'none'; panel.classList.remove('is-active'); }
        // Give the clicked tab its content back if Jellyfin has not done it yet.
        if (page && !page.querySelector('.pageTabContent.is-active')) {
            var active = document.querySelector('.headerTabs .emby-tab-button.is-active');
            var idx = active ? active.getAttribute('data-index') : '0';
            var content = page.querySelector('.pageTabContent[data-index="' + idx + '"]') || page.querySelector('.pageTabContent[data-index="0"]');
            if (content) { content.classList.add('is-active'); }
        }
    }

    function ensureHideStyle() {
        if (document.getElementById('terebiTvHide')) { return; }
        var st = document.createElement('style'); st.id = 'terebiTvHide';
        st.textContent = '.liveTvPage.terebi-active .pageTabContent.terebi-hide{display:none !important}';
        document.head.appendChild(st);
    }

    function ensureTab() {
        var client = window.ApiClient;
        if (!client) { return; }
        var page = livePage();
        var slider = document.querySelector('.headerTabs .emby-tabs-slider');
        if (!page || !slider || !document.body.classList.contains('withSectionTabs')) {
            if (state.active && !page) { deactivate(); }
            return;
        }

        ensureStyle();
        ensureHideStyle();
        var button = slider.querySelector('.emby-tab-button[data-index="' + TAB_INDEX + '"]');
        if (!button) {
            button = document.createElement('button');
            button.type = 'button';
            button.className = 'emby-tab-button';
            button.setAttribute('data-index', TAB_INDEX);
            button.innerHTML = '<div class="emby-button-foreground">Terebi</div>';
            button.addEventListener('click', function (e) {
                // Keep emby-tabs out of it: it would try to load a tab controller that does not exist.
                e.stopPropagation();
                e.preventDefault();
                activate(client);
            }, true);
            slider.appendChild(button);
            // Any other tab: give the page back to Jellyfin.
            slider.addEventListener('click', function (e) {
                var b = e.target.closest('.emby-tab-button');
                if (b && b !== button && state.active) { deactivate(); }
            }, true);
            var tabs = slider.parentElement;
            if (tabs && tabs.refresh) { try { tabs.refresh(); } catch (err) { /* ignore */ } }
        }

        // Coming back to Live TV with Terebi as the last used tab: reopen it.
        if (!state.active && load(KEY_LAST) === '1' && !page.getAttribute('data-terebi-restored')) {
            page.setAttribute('data-terebi-restored', '1');
            activate(client);
        }
        if (state.active) {
            button.classList.add('is-active');
        }
    }

    state.category = load(KEY_CAT);

    var pending = null;
    function schedule() { clearTimeout(pending); pending = setTimeout(ensureTab, 200); }
    function init() {
        document.addEventListener('viewshow', function () {
            var page = document.getElementById('liveTvSuggestedPage');
            if (page) { page.removeAttribute('data-terebi-restored'); }
            if (!livePage() && state.active) { deactivate(); }
            schedule();
        }, true);
        window.addEventListener('hashchange', schedule);
        new MutationObserver(function () {
            if (document.querySelector('.headerTabs .emby-tabs-slider') && document.getElementById('liveTvSuggestedPage')) { schedule(); }
        }).observe(document.body, { childList: true, subtree: true });
        schedule();
    }
    if (document.readyState === 'loading') { document.addEventListener('DOMContentLoaded', init, { once: true }); } else { init(); }
}());
