// App shell: navigation, state from the host, settings, event wiring.

const DEFAULT_SETTINGS = {
  lang: "ru", theme: "dark", accent: "#4F8CFF", tint: 0, font: "Manrope", scale: 1, anim: "full",
  streamerMode: false, mapBackground: true, refreshSec: 3,
  rpc: {
    enabled: true, clientId: "",
    game: { large: "map", small: "agent" },
    lobby: { large: "logo", small: "rank" },
    showName: false, showParty: true, showScore: true, showTimer: true,
    button: { enabled: false, label: "Exora", url: "" },
  },
};

const PAGES = [
  { id: "home", icon: "home", render: renderHome },
  { id: "match", icon: "crosshair", render: renderMatch },
  { id: "friends", icon: "users", render: renderFriends },
  { id: "rpc", icon: "discord", render: renderRpc },
  { id: "settings", icon: "settings", render: renderSettings, bottom: true },
];

let STATE = host ? { connection: "connecting" } : MOCK.state;
let NEWS = host ? [] : MOCK.news;
let SETTINGS = structuredClone(DEFAULT_SETTINGS);
const UI = { page: "home", friendQuery: "", rpcPreview: "game", openPlayers: new Set(), launching: false };

const $content = document.getElementById("content");

function navLabel(id) {
  return t("nav." + id);
}

function renderNav() {
  const inMatch = STATE.match ? `<span class="nav-badge">${STATE.match.state === "PREGAME" ? "•" : "LIVE"}</span>` : "";
  const item = (p) => `<button class="nav-item${UI.page === p.id ? " on" : ""}" data-page="${p.id}">${icon(p.icon)}<span>${navLabel(p.id)}</span>${p.id === "match" ? inMatch : ""}</button>`;
  const nav = document.getElementById("nav");
  const next = nav.cloneNode(false);
  next.innerHTML = `<span class="nav-pill"></span>` +
    PAGES.filter((p) => !p.bottom).map(item).join("") + `<div class="nav-sep"></div>` + PAGES.filter((p) => p.bottom).map(item).join("");
  morph(nav, next);
  layoutNav();
}

// The highlight behind the active menu item is one element that slides between items.
function layoutNav() {
  const nav = document.getElementById("nav");
  const on = nav.querySelector(".nav-item.on");
  const pill = nav.querySelector(".nav-pill");
  if (!on || !pill) return;
  placePill(pill, { top: on.offsetTop + "px", height: on.offsetHeight + "px" });
}

function renderSidebarFoot() {
  const st = STATE.status;
  const me = STATE.me;
  const live = !!(st && st.state);
  let stateText = STATE.connection === "connecting" ? t("state.connecting")
    : STATE.connection === "ok" && !live ? t("state.online") : stateLabel(st);
  patchHtml(document.getElementById("me-card"), `
    ${rankImg(me && me.rank)}
    <div class="meta">
      <div class="state"><span class="dot${live ? " live" : ""}"></span>${esc(stateText)}</div>
      <div class="name">${me ? `${esc(me.name)}<span class="tag">#${esc(me.tag)}</span>` : "—"}</div>
    </div>`);

  const btn = document.getElementById("btn-launch");
  const running = STATE.valorantRunning;
  btn.className = "btn btn-launch" + (running ? "" : " primary");
  btn.disabled = running || UI.launching;
  patchHtml(btn, running ? `${icon("check")}${t("launch.open")}` : `${icon("play")}${UI.launching ? t("launch.starting") : t("launch.start")}`);
}

function renderBackdrop() {
  const el = document.getElementById("backdrop-img");
  const splash = SETTINGS.mapBackground && STATE.match && STATE.match.map && STATE.match.map.splash;
  if (splash) {
    el.style.backgroundImage = `url("${splash}")`;
    el.classList.add("on");
  } else {
    el.classList.remove("on");
  }
}

const STAGGER = ".card, .player, .friend, .news-item, .recent-row, .stat";
let enterUntil = 0;
let deferredRender = 0;

// animate = true: new page slides in and its items appear one after another.
// animate = false: the current page is patched in place (state updates, setting changes).
function renderPage(animate = true) {
  const page = PAGES.find((p) => p.id === UI.page) || PAGES[0];
  const html = page.render(STATE, SETTINGS, page.id === "home" ? NEWS : UI);

  if (animate) {
    $content.innerHTML = html;
    const root = $content.firstElementChild;
    if (root) {
      root.classList.add("enter");
      root.querySelectorAll(STAGGER).forEach((el, i) => {
        el.classList.add("stagger");
        el.style.setProperty("--i", Math.min(i, 14));
      });
    }
    enterUntil = performance.now() + 600;
  } else if (performance.now() < enterUntil) {
    // Don't cut the entrance animation short; apply the update right after it.
    clearTimeout(deferredRender);
    deferredRender = setTimeout(() => renderPage(false), enterUntil - performance.now());
    return;
  } else {
    const next = $content.cloneNode(false);
    next.innerHTML = html;
    morph($content, next);
  }

  UI.openPlayers.forEach((id) => {
    const el = $content.querySelector(`[data-player="${CSS.escape(id)}"]`);
    if (el) el.classList.add("open");
  });
  document.getElementById("titlebar-title").textContent = page.id === "home" ? "" : navLabel(page.id);
  layoutSegs($content);
}

function renderAll(animate) {
  renderNav();
  renderSidebarFoot();
  renderBackdrop();
  renderPage(animate);
}

function go(page) {
  if (UI.page === page) return;
  UI.page = page;
  renderNav();
  renderPage(true);
  $content.scrollTop = 0;
}

// ---------- Settings ----------

function applySettings() {
  LANG = SETTINGS.lang;
  applyTheme(SETTINGS);
}

let saveTimer = 0;
function updateSetting(path, value, rerender = true) {
  if (path.startsWith("ui.")) {
    UI[path.slice(3)] = value;
  } else {
    setPath(SETTINGS, path, value);
    applySettings();
    clearTimeout(saveTimer);
    saveTimer = setTimeout(() => send("settings", { data: SETTINGS }), 250);
    if (!host) try { localStorage.setItem("exora.settings", JSON.stringify(SETTINGS)); } catch {}
  }
  if (rerender) {
    if (path === "lang") renderAll(false);
    else renderPage(false);
    if (path === "mapBackground") renderBackdrop();
  }
}

function mergeDefaults(target, defaults) {
  for (const k in defaults) {
    if (target[k] === undefined) target[k] = structuredClone(defaults[k]);
    else if (typeof defaults[k] === "object" && defaults[k] && !Array.isArray(defaults[k])) mergeDefaults(target[k], defaults[k]);
  }
  return target;
}

// ---------- Events ----------

document.addEventListener("click", (e) => {
  const nav = e.target.closest("[data-page]");
  if (nav) return go(nav.dataset.page);

  const win = e.target.closest("[data-win]");
  if (win) return send("win", { action: win.dataset.win });

  const segBtn = e.target.closest(".seg button");
  if (segBtn) {
    const segEl = segBtn.parentElement;
    let v = segBtn.dataset.value;
    const cur = getPath(SETTINGS, segEl.dataset.seg);
    if (typeof cur === "number") v = Number(v);
    segEl.querySelectorAll("button").forEach((b) => b.classList.toggle("on", b === segBtn));
    layoutSegs(segEl.parentElement);
    updateSetting(segEl.dataset.seg, v);
    return;
  }

  const selBtn = e.target.closest(".select-btn");
  const openSel = document.querySelector(".select.open");
  if (selBtn) {
    const sel = selBtn.parentElement;
    if (openSel && openSel !== sel) openSel.classList.remove("open");
    sel.classList.toggle("open");
    return;
  }
  const opt = e.target.closest(".select-opt");
  if (opt) {
    const sel = opt.closest(".select");
    sel.classList.remove("open");
    return updateSetting(sel.dataset.select, opt.dataset.value);
  }
  if (openSel) openSel.classList.remove("open");

  const sw = e.target.closest("[data-accent]");
  if (sw) return updateSetting("accent", sw.dataset.accent);

  const player = e.target.closest("[data-player]");
  if (player) {
    const id = player.dataset.player;
    player.classList.toggle("open");
    if (player.classList.contains("open")) UI.openPlayers.add(id); else UI.openPlayers.delete(id);
    return;
  }

  const link = e.target.closest("[data-open]");
  if (link) {
    if (host) send("openUrl", { url: link.dataset.open });
    else window.open(link.dataset.open, "_blank");
    return;
  }

  const cmd = e.target.closest("[data-cmd]");
  if (cmd) return send(cmd.dataset.cmd, {});

  if (e.target.closest("#btn-launch")) {
    UI.launching = true;
    renderSidebarFoot();
    send("launch", {});
    setTimeout(() => { UI.launching = false; renderSidebarFoot(); }, 20000);
  }
});

document.addEventListener("change", (e) => {
  const el = e.target;
  if (el.matches(".toggle input[data-set]")) return updateSetting(el.dataset.set, el.checked);
  if (el.matches(".slider[data-set]")) return updateSetting(el.dataset.set, Number(el.value), false);
  if (el.matches("[data-text]")) return updateSetting(el.dataset.text, el.value.trim());
  if (el.id === "accent-custom") return updateSetting("accent", el.value.toUpperCase());
});

document.addEventListener("input", (e) => {
  const el = e.target;
  if (el.matches(".slider")) {
    const p = ((el.value - el.min) / (el.max - el.min)) * 100;
    el.style.setProperty("--p", p + "%");
    if (el.dataset.set === "tint") {
      document.getElementById("tint-val").textContent = el.value + "%";
      applyTheme({ ...SETTINGS, tint: Number(el.value) });
    }
  }
  if (el.id === "friend-search") {
    UI.friendQuery = el.value;
    const list = document.createElement("div");
    list.innerHTML = renderFriends(STATE, SETTINGS, UI);
    morph(document.getElementById("friend-list"), list.querySelector("#friend-list"));
  }
  if (el.matches("[data-text]") && UI.page === "rpc") {
    setPath(SETTINGS, el.dataset.text, el.value);
    document.getElementById("rpc-card").innerHTML = discordCard(STATE, SETTINGS, UI.rpcPreview);
  }
});

// Double-click a slider to type an exact value.
document.addEventListener("dblclick", (e) => {
  const el = e.target.closest(".slider[data-set]");
  if (!el) return;
  const v = prompt("", el.value);
  if (v === null || isNaN(Number(v))) return;
  const n = Math.max(Number(el.min), Math.min(Number(el.max), Number(v)));
  updateSetting(el.dataset.set, n);
});

// Window dragging and resizing on a frameless window are handled by the host.
// Dragging starts only once the mouse actually moves: the host's move loop captures the mouse,
// so starting it on mousedown would swallow the second click of a double-click.
document.addEventListener("mousedown", (e) => {
  if (e.button !== 0) return;
  const r = e.target.closest("[data-resize]");
  if (r) { e.preventDefault(); return send("win", { action: "resize", dir: r.dataset.resize }); }
  if (!e.target.closest(".drag") || e.target.closest("button, input, select, a")) return;

  if (e.detail === 2) return send("win", { action: "max" });
  const x = e.screenX, y = e.screenY;
  const move = (ev) => {
    if (Math.abs(ev.screenX - x) + Math.abs(ev.screenY - y) < 4) return;
    stop();
    send("win", { action: "drag" });
  };
  const stop = () => {
    window.removeEventListener("mousemove", move);
    window.removeEventListener("mouseup", stop);
  };
  window.addEventListener("mousemove", move);
  window.addEventListener("mouseup", stop);
});

window.addEventListener("resize", () => {
  layoutSegs($content);
  layoutNav();
});

// Keep the RPC preview timer ticking.
setInterval(() => {
  document.querySelectorAll(".discord .time[data-since]").forEach((el) => {
    el.textContent = t("rpc.elapsed", { t: formatElapsed(Date.now() - Number(el.dataset.since)) });
  });
}, 1000);

// ---------- Host messages ----------

let lastMatchId = null;
let lastDiscord = null;

function onState(next) {
  const prevPage = UI.page;
  const matchId = next.match ? next.match.id || next.match.state : null;
  if (matchId && matchId !== lastMatchId && next.match) {
    toast(t("toast.matchFound"), next.match.map ? next.match.map.name : "");
    if (prevPage === "home") UI.page = "match";
  }
  lastMatchId = matchId;
  const connected = next.discord && next.discord.connected;
  if (lastDiscord !== null && connected !== lastDiscord && SETTINGS.rpc.enabled) {
    toast(connected ? t("toast.discordOn") : t("toast.discordOff"), "", connected ? "success" : "error");
  }
  lastDiscord = connected;

  STATE = next;
  renderNav();
  renderSidebarFoot();
  renderBackdrop();
  // morph() leaves the focused field alone, so updates don't disturb typing.
  renderPage(UI.page !== prevPage);
}

if (host) {
  host.addEventListener("message", (e) => {
    const msg = e.data;
    switch (msg.type) {
      case "settings":
        SETTINGS = mergeDefaults(msg.data || {}, DEFAULT_SETTINGS);
        applySettings();
        renderAll(false);
        break;
      case "state":
        onState(msg.data);
        break;
      case "news":
        NEWS = msg.data || [];
        if (UI.page === "home") renderPage(false);
        break;
      case "window":
        document.body.classList.toggle("maximized", !!msg.maximized);
        break;
      case "toast":
        toast(msg.title, msg.text, msg.kind);
        break;
    }
  });
} else {
  try {
    const saved = JSON.parse(localStorage.getItem("exora.settings") || "null");
    if (saved) SETTINGS = mergeDefaults(saved, DEFAULT_SETTINGS);
  } catch {}
  const params = new URLSearchParams(location.search);
  if (params.get("page")) UI.page = params.get("page");
  if (params.get("theme")) SETTINGS.theme = params.get("theme");
  if (params.get("lang")) SETTINGS.lang = params.get("lang");
  if (params.get("accent")) SETTINGS.accent = "#" + params.get("accent");
  if (params.get("offline")) STATE = { connection: "none", friends: [] };
}

applySettings();
renderAll(true);
send("ready", {});
