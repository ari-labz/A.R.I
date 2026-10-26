// Applies the colour overrides saved from the control panel (GET /api/theme) on top of the defaults in
// colours.css: each override sets --col-<name>. A failed fetch just leaves ARI's stock colours. The last
// overrides are cached so reloads don't flash the defaults.
(function () {
    var applied = [];
    function apply(t) {
        var root = document.documentElement.style, o = (t && t.overrides) || {};
        applied.forEach(function (n) { if (!(n in o)) root.removeProperty('--col-' + n); });
        applied = Object.keys(o);
        applied.forEach(function (n) { root.setProperty('--col-' + n, o[n]); });
    }
    window.ariApplyTheme = apply;
    try { var c = localStorage.getItem('ari_theme'); if (c) apply(JSON.parse(c)); } catch (e) {}
    // A single uploaded logo also rebrands the sidebar, where it has to be turned white (see colours/CSS).
    function brand(b) { document.documentElement.toggleAttribute('data-brand-sidebar-derived', !!(b && b.sidebarDerived)); }
    try { brand(JSON.parse(localStorage.getItem('ari_brand'))); } catch (e) {}
    fetch('/api/v1/branding').then(function (r) { return r.ok ? r.json() : null; }).then(function (b) {
        if (!b) return;
        brand(b);
        try { localStorage.setItem('ari_brand', JSON.stringify(b)); } catch (e) {}
    }).catch(function () {});
    fetch('/api/v1/theme').then(function (r) { return r.ok ? r.json() : null; }).then(function (t) {
        if (!t) return;
        apply(t);
        try { localStorage.setItem('ari_theme', JSON.stringify(t)); } catch (e) {}
    }).catch(function () {});
})();
