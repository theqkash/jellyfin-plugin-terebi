/* Terebi web client addition: a category selector on Live TV > Channels and on the Guide.
 * Loaded from index.html through the File Transformation plugin. It wraps ApiClient.getLiveTvChannels so
 * that, when a category is chosen, Jellyfin's own pages only receive the channels of that category. */
(function () {
    'use strict';
    if (window.__terebiLoaded) { return; }
    window.__terebiLoaded = true;

    var STORAGE = 'terebi.category';
    var cache = { at: 0, categories: null, byNumber: null };
    var patched = false;

    function current() { try { return localStorage.getItem(STORAGE) || ''; } catch (e) { return ''; } }
    function setCurrent(v) { try { if (v) { localStorage.setItem(STORAGE, v); } else { localStorage.removeItem(STORAGE); } } catch (e) { /* ignore */ } }

    function loadCategories(client) {
        if (cache.categories && Date.now() - cache.at < 300000) { return Promise.resolve(cache.categories); }
        return client.ajax({ type: 'GET', url: client.getUrl('Terebi/Public/Categories'), dataType: 'json' }).then(function (r) {
            cache.at = Date.now();
            cache.categories = r.categories || [];
            cache.byNumber = {};
            cache.categories.forEach(function (c) { c.numbers.forEach(function (n) { cache.byNumber[String(n)] = c.name; }); });
            return cache.categories;
        }).catch(function () { return cache.categories || []; });
    }

    function numbersOf(name) {
        var c = (cache.categories || []).filter(function (x) { return x.name === name; })[0];
        var set = {};
        if (c) { c.numbers.forEach(function (n) { set[String(n)] = true; }); }
        return set;
    }

    // ---- ApiClient wrapper -------------------------------------------------------------------
    function patchApiClient(client) {
        var proto = Object.getPrototypeOf(client);
        var target = proto && proto.getLiveTvChannels ? proto : client;
        if (patched || !target.getLiveTvChannels || target.getLiveTvChannels.__terebi) { return; }
        var original = target.getLiveTvChannels;
        var wrapped = function (options) {
            var self = this;
            var category = current();
            if (!category) { return original.call(self, options); }
            options = options || {};
            var startIndex = options.StartIndex || options.startIndex || 0;
            var limit = options.Limit || options.limit || 0;
            var wide = Object.assign({}, options, { StartIndex: 0, startIndex: 0, Limit: 10000, limit: 10000 });
            return loadCategories(self).then(function (cats) {
                // A remembered category that no longer exists (renamed, renumbered) must not hide every channel.
                if (!cats.some(function (c) { return c.name === category; })) {
                    setCurrent('');
                    return original.call(self, options);
                }

                return original.call(self, wide).then(function (result) {
                    var allowed = numbersOf(category);
                    var items = (result.Items || []).filter(function (i) { return allowed[String(i.ChannelNumber)] === true; });
                    var page = limit ? items.slice(startIndex, startIndex + limit) : items.slice(startIndex);
                    return Object.assign({}, result, { Items: page, TotalRecordCount: items.length, StartIndex: startIndex });
                });
            });
        };
        wrapped.__terebi = true;
        target.getLiveTvChannels = wrapped;
        patched = true;
    }

    // ---- UI ---------------------------------------------------------------------------------
    function buildSelect(client, onChange) {
        var sel = document.createElement('select');
        sel.className = 'terebi-cat emby-select-withcolor emby-select';
        sel.style.cssText = 'margin:0 .5em;padding:.35em 1.8em .35em .6em;border-radius:.3em;background:rgba(0,0,0,.35);color:inherit;border:1px solid rgba(255,255,255,.25);font-size:.95em;max-width:16em';
        var opt = document.createElement('option');
        opt.value = ''; opt.textContent = 'All categories';
        sel.appendChild(opt);
        loadCategories(client).then(function (cats) {
            cats.forEach(function (c) {
                var o = document.createElement('option');
                o.value = c.name; o.textContent = c.name + ' (' + c.numbers.length + ')';
                sel.appendChild(o);
            });
            sel.value = current();
        });
        sel.addEventListener('change', function () { setCurrent(sel.value); onChange(); });
        return sel;
    }

    function reloadChannelsTab() {
        var container = document.querySelector('#channelsTab #items');
        if (container && typeof container.refreshItems === 'function') { container.refreshItems(); return; }
        location.reload();
    }

    function reloadGuide() {
        var active = document.querySelector('.guideDateTabs .emby-tab-button-active') || document.querySelector('.guideDateTabs .emby-tab-button');
        var tabs = document.querySelector('.guideDateTabs');
        if (active && tabs) {
            // Switching away and back forces the guide to refetch channels.
            var buttons = tabs.querySelectorAll('.emby-tab-button');
            var other = Array.prototype.filter.call(buttons, function (b) { return b !== active; })[0];
            if (other) { other.click(); setTimeout(function () { active.click(); }, 150); return; }
        }
        location.reload();
    }

    function decorate() {
        var client = window.ApiClient;
        if (!client) { return; }
        patchApiClient(client);

        var wrapper = document.querySelector('#channelsTab .btnFilter-wrapper');
        if (wrapper && !wrapper.querySelector('.terebi-cat')) {
            wrapper.insertBefore(buildSelect(client, reloadChannelsTab), wrapper.firstChild);
        }

        var guideHead = document.querySelector('.tvGuideHeader .guide-channelTimeslotHeader');
        if (guideHead && !guideHead.querySelector('.terebi-cat')) {
            var sel = buildSelect(client, reloadGuide);
            sel.style.maxWidth = '9em';
            sel.style.fontSize = '.8em';
            guideHead.insertBefore(sel, guideHead.firstChild);
        }
    }

    var timer = null;
    function schedule() { clearTimeout(timer); timer = setTimeout(decorate, 250); }

    function init() {
        document.addEventListener('viewshow', schedule, true);
        window.addEventListener('hashchange', schedule);
        new MutationObserver(function () {
            if (document.querySelector('#channelsTab .btnFilter-wrapper, .tvGuideHeader')) { schedule(); }
        }).observe(document.body, { childList: true, subtree: true });
        schedule();
    }

    if (document.readyState === 'loading') { document.addEventListener('DOMContentLoaded', init, { once: true }); } else { init(); }
}());
