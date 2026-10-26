// First-run guided tour. Runs on the chat page and on the control panel; progress lives in localStorage so it
// survives the navigation between them. The tour finishes when the Language model module has been turned on.
(function () {
    var KEY = 'ari_tour';
    var onCp = /controlpanel\.html$/.test(location.pathname);
    var page = onCp ? 'cp' : 'chat';

    var STEPS = [
        { id: 'chat', page: 'chat', sel: '#input-wrap', title: 'Talk to ARI',
          text: 'This is where you chat with ARI. Type a message and press Enter. (She will not answer yet — we will wake her up at the end of this tour.)' },
        { id: 'new', page: 'chat', sel: '#btn-new-chat', title: 'New chats',
          text: 'Start a fresh conversation any time with New chat. Each chat is its own thread with its own history.' },
        { id: 'recents', page: 'chat', sel: '#thread-list', title: 'Your chats',
          text: 'Your own conversations are listed here under Recents. Click one to pick it up where you left off.' },
        { id: 'user', page: 'chat', sel: '#btn-user-prefs', title: 'Your settings',
          text: 'Your name, password and personal preferences live here.' },
        { id: 'cp', page: 'chat', sel: '#btn-control-panel', title: 'The control panel', click: true,
          text: 'This is where you run ARI itself: servers, agents, modules and more. Click it to open it.' },

        { id: 'servers-nav', page: 'cp', sel: '[data-tab="servers"]', title: 'Servers', click: true,
          text: 'Start with Servers. Click it.' },
        { id: 'servers', page: 'cp', sel: '#tab-servers', title: 'Servers run the models',
          text: 'A server is one running copy of a model. The setup wizard made "Main Server" for you. You can add more — for example a big, smart model and a small, fast one — and run several at once if you have the memory. Each server has its own Start and Stop buttons. Servers only run once the Language model module (the last step) is on.' },
        { id: 'agents-nav', page: 'cp', sel: '[data-tab="agents"]', title: 'Agents', click: true,
          text: 'Next, Agents. Click it.' },
        { id: 'agents', page: 'cp', sel: '#tab-agents', title: 'Agents do the work',
          text: 'An agent is a role ARI plays — chatting, remembering, tidying up. Every agent is pointed at a server (its brain) and can be switched on or off. By default they all use your Main Server.' },
        { id: 'modules-nav', page: 'cp', sel: '[data-tab="modules"]', title: 'Modules', click: true,
          text: 'Last stop: Modules. Click it.' },
        { id: 'modules', page: 'cp', sel: '#mod-list > div:first-child', title: 'Wake her up', waitLlm: true,
          text: 'Modules are ARI\'s abilities, and they all start switched off — right now she is very simple. Voice, Discord, the calendar and more can each be turned on or off here whenever you like. Turn on the Language model to start your server and bring ARI to life.' },
        { id: 'finish', page: 'cp', sel: null, title: 'ARI is awake', final: true,
          text: 'The Language model is running. Head back to the home page and say hello to ARI.' },
    ];

    function byId(id) { for (var i = 0; i < STEPS.length; i++) if (STEPS[i].id === id) return STEPS[i]; return null; }
    function cur() { try { return localStorage.getItem(KEY); } catch (e) { return null; } }
    function setCur(id) { try { id ? localStorage.setItem(KEY, id) : localStorage.removeItem(KEY); } catch (e) {} }
    function authHeaders() {
        var t = null;
        try { t = sessionStorage.getItem('ari_cp_token') || sessionStorage.getItem('ari_token'); } catch (e) {}
        return t ? { Authorization: 'Bearer ' + t } : {};
    }

    var ui = null, pollTimer = null, waitTimer = null;

    function build() {
        if (ui) return ui;
        var spot = document.createElement('div');
        spot.style.cssText = 'position:fixed;z-index:100000;pointer-events:none;border-radius:8px;transition:all .25s;'
            + 'box-shadow:0 0 0 9999px rgba(0,0,0,.6),0 0 0 3px #CADDE2;';
        var card = document.createElement('div');
        card.style.cssText = 'position:fixed;z-index:100001;width:320px;max-width:calc(100vw - 24px);padding:16px 18px;border-radius:12px;'
            + 'background:#101618;color:#E8F0F2;font:14px/1.5 system-ui,sans-serif;box-shadow:0 8px 32px rgba(0,0,0,.5);border:1px solid #3f5b63;';
        document.body.append(spot, card);
        ui = { spot: spot, card: card };
        return ui;
    }

    function teardown() {
        if (pollTimer) clearInterval(pollTimer);
        if (waitTimer) clearInterval(waitTimer);
        pollTimer = waitTimer = null;
        if (ui) { ui.spot.remove(); ui.card.remove(); ui = null; }
    }

    function place(el) {
        var u = build();
        var card = u.card;
        if (!el) {
            u.spot.style.cssText += ';left:50%;top:50%;width:0;height:0;';
            card.style.left = '50%'; card.style.top = '50%'; card.style.transform = 'translate(-50%,-50%)';
            return;
        }
        card.style.transform = '';
        var r = el.getBoundingClientRect();
        var pad = 6;
        var vw = window.innerWidth, vh = window.innerHeight;
        var big = r.height > vh * 0.7;      // a whole tab: highlight just its top strip
        var h = big ? Math.min(r.height, 120) : r.height;
        u.spot.style.left = (r.left - pad) + 'px'; u.spot.style.top = (r.top - pad) + 'px';
        u.spot.style.width = (r.width + pad * 2) + 'px'; u.spot.style.height = (h + pad * 2) + 'px';
        var cw = 320, ch = card.offsetHeight || 160;
        var left, top;
        if (big) { left = vw - cw - 24; top = Math.max(12, vh - ch - 24); if (left < r.left) left = r.left + 12; }
        else if (r.right + cw + 24 < vw) { left = r.right + 16; top = r.top; }
        else if (r.left - cw - 24 > 0)   { left = r.left - cw - 16; top = r.top; }
        else                              { left = Math.max(12, (vw - cw) / 2); top = r.bottom + 16; }
        top = Math.min(Math.max(12, top), vh - ch - 12);
        card.style.left = Math.min(Math.max(12, left), vw - cw - 12) + 'px';
        card.style.top = top + 'px';
    }

    function btn(label, primary, fn) {
        var b = document.createElement('button');
        b.textContent = label;
        b.style.cssText = 'padding:7px 14px;border-radius:8px;border:' + (primary ? 'none' : '1px solid #3f5b63') + ';cursor:pointer;font:600 13px system-ui;'
            + (primary ? 'background:#CADDE2;color:#101618;' : 'background:none;color:#E8F0F2;');
        b.addEventListener('click', fn);
        return b;
    }

    function finishTour() {
        setCur(null);
        teardown();
        fetch('/api/v1/setup/tutorial-complete', { method: 'POST', headers: authHeaders(), credentials: 'same-origin' }).catch(function () {});
    }

    function go(id) { setCur(id); show(); }
    function nextOf(step) { var i = STEPS.indexOf(step); return STEPS[i + 1] ? STEPS[i + 1].id : null; }

    function show() {
        teardown();
        var id = cur(); if (!id) return;
        var step = byId(id); if (!step) { setCur(null); return; }
        if (step.page !== page) return;               // the other page will pick it up

        var tries = 0;
        function attempt() {
            var el = step.sel ? document.querySelector(step.sel) : null;
            if (step.sel && (!el || el.getBoundingClientRect().width === 0)) { if (++tries > 60) return; setTimeout(attempt, 250); return; }
            render(step, el);
        }
        attempt();
    }

    function render(step, el) {
        var u = build();
        if (el) el.scrollIntoView({ block: 'nearest' });
        var idx = STEPS.indexOf(step) + 1;
        u.card.innerHTML = '';
        var t = document.createElement('div');
        t.style.cssText = 'font-weight:700;font-size:15px;margin-bottom:6px;';
        t.textContent = step.title;
        var p = document.createElement('div');
        p.style.cssText = 'color:#C4D3D8;margin-bottom:14px;';
        p.textContent = step.text;
        var row = document.createElement('div');
        row.style.cssText = 'display:flex;align-items:center;gap:8px;';
        var n = document.createElement('span');
        n.style.cssText = 'font-size:12px;color:#8aa3ab;flex:1;';
        n.textContent = idx + ' / ' + STEPS.length;
        row.appendChild(n);

        if (step.final) {
            row.appendChild(btn('Go to the home page', true, function () { finishTour(); location.href = '/'; }));
        } else {
            row.appendChild(btn('Skip tour', false, function () { finishTour(); }));
            if (!step.click && !step.waitLlm) row.appendChild(btn('Next', true, function () { go(nextOf(step)); }));
        }
        u.card.append(t, p, row);

        if (step.click && el) {
            el.addEventListener('click', function once() {
                el.removeEventListener('click', once, true);
                setCur(nextOf(step));                   // saved before any navigation the click causes
                setTimeout(show, 150);
            }, true);
        }
        if (step.waitLlm) {
            pollTimer = setInterval(function () {
                fetch('/api/v1/admin/modules', { headers: authHeaders(), credentials: 'same-origin' })
                    .then(function (r) { return r.json(); })
                    .then(function (d) {
                        var m = (d.modules || []).filter(function (x) { return x.key === 'LLM'; })[0];
                        if (m && m.running) go(nextOf(step));
                    }).catch(function () {});
            }, 1500);
        }
        place(el);
        // The control panel's nav and tabs finish laying out after first paint, so keep the spotlight on its target.
        waitTimer = setInterval(function () { if (ui) place(el); }, 300);
        window.addEventListener('resize', function r() { if (!ui) { window.removeEventListener('resize', r); return; } place(el); });
    }

    window.ariTour = {
        start: function () { setCur(STEPS[0].id); show(); },
        resume: show,
    };

    if (cur()) {
        if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', show);
        else show();
    }
})();
