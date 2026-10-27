// Page renderers. Each returns an HTML string for the content area.

const PARTY_COLORS = ["#FF8A3D", "#3DD6A4", "#FF5C8A", "#B6E83C", "#A78BFA", "#4FD1FF"];

function stateLabel(s) {
  if (!s || !s.state) return t("state.offline");
  return t("state." + s.state) + (s.mode ? " — " + s.mode : "");
}

function emptyState(iconName, title, text, extra) {
  return `<div class="empty page"><div><div class="empty-icon">${icon(iconName)}</div><h3>${esc(title)}</h3><p>${esc(text)}</p>${extra || ""}</div></div>`;
}

// ---------- Home ----------

function renderHome(state, settings, news) {
  const me = state.me;
  const left = me ? homeProfile(state, me) : `<div class="card">${emptyState("gamepad", t("home.offlineTitle"), t("home.offlineText"))}</div>`;
  const newsHtml = news && news.length
    ? news.map((n) => `<button class="news-item" data-open="${esc(n.url)}"><small>${esc(formatDate(n.date))}</small><b>${esc(n.title)}</b><p>${esc(n.text)}</p></button>`).join("")
    : `<p class="muted">${t("home.newsEmpty")}</p>`;

  return `<div class="page home">
    <div class="home-col">${left}</div>
    <div class="home-col">
      <div class="card">
        <div class="eyebrow">${t("home.news")}</div>
        <div class="title" style="font-size:19px;margin:2px 0 14px">${t("home.newsTitle")}</div>
        <div class="news">${newsHtml}</div>
      </div>
    </div>
  </div>`;
}

function homeProfile(state, me) {
  const live = state.status && state.status.state;
  const recent = me.recent || [];
  const wr = me.wr == null ? "—" : me.wr + "%";
  return `
    <div class="card hero">
      <div class="hero-art">
        ${img(me.card, "", "")}
        ${live ? `<span class="badge win hero-live">● ${t("home.live")}</span>` : ""}
        <div class="hero-id">
          ${rankImg(me.rank)}
          <div class="who">
            <div class="name">${esc(me.name)}<span class="tag">#${esc(me.tag)}</span></div>
            ${me.title ? `<div class="ptitle">${esc(me.title)}</div>` : ""}
          </div>
          ${me.agent ? `<div class="agent-chip">${img(me.agent.icon, "avatar", initials(me.agent.name))}<div><small>${t("home.lastAgent")}</small><b>${esc(me.agent.name)}</b></div></div>` : ""}
        </div>
      </div>
      <div class="hero-body">
        <div class="stats">
          <div class="stat"><small>${t("home.rank")}</small><b>${esc(me.rank ? me.rank.name : "—")}</b><span class="sub num">${t("match.rr", { n: me.rr ?? 0 })}</span></div>
          <div class="stat"><small>${t("home.peak")}</small><b>${esc(me.peak ? me.peak.name : "—")}</b><span class="sub">${esc(me.peak && me.peak.act || "")}</span></div>
          <div class="stat"><small>${t("home.winrate")}</small><b>${wr}</b><span class="sub">${t("home.games", { n: me.games ?? 0 })}</span></div>
          <div class="stat"><small>${t("home.level")}</small><b>${me.level || "—"}</b><span class="sub">&nbsp;</span></div>
        </div>
      </div>
    </div>
    <div class="card">
      <div class="card-head" style="margin-bottom:12px">
        <div class="grow title">${t("home.recent")}</div>
        <div class="wl">${recent.map((m) => `<span class="${m.result}">${t("result." + m.result)}</span>`).join("")}</div>
      </div>
      <div class="recent">${recent.length ? recent.map(recentRow).join("") : `<p class="muted">${t("home.recentEmpty")}</p>`}</div>
    </div>`;
}

function recentRow(m) {
  return `<div class="recent-row ${m.result}">
    ${img(m.agent && m.agent.icon, "avatar", initials(m.agent && m.agent.name))}
    <div class="what"><b>${esc(m.map)}</b><small>${esc(m.mode)}${m.kda ? " · " + esc(m.kda) : ""}</small></div>
    <div class="score">${esc(m.score)}</div>
  </div>`;
}

// ---------- Match ----------

function renderMatch(state, settings) {
  const m = state.match;
  if (!m) return emptyState("crosshair", t("match.noneTitle"), t("match.noneText"));

  const parties = partyColors(m);
  // Free-for-all modes have no round score.
  const showScore = m.state === "INGAME" && m.score && !m.single;
  const teams = m.single
    ? `<div class="teams single">${teamCard("ally", t("match.players"), m.allies, parties, settings)}</div>`
    : `<div class="teams">${teamCard("ally", t("match.ally"), m.allies, parties, settings)}${teamCard("enemy", t("match.enemy"), m.enemies, parties, settings)}</div>`;

  return `<div class="page">
    <div class="match-head">
      <div class="match-map">${esc(m.map ? m.map.name : "")}</div>
      ${showScore ? `<div class="match-score"><span class="a">${m.score.ally}</span> – <span class="e">${m.score.enemy}</span></div>` : ""}
      <div class="match-meta"><span class="badge warn">${esc(stateLabel({ state: m.state, mode: m.mode }))}</span></div>
    </div>
    ${teams}
  </div>`;
}

// Assigns a colour to every party that has more than one player in this match.
function partyColors(m) {
  const counts = {};
  [...(m.allies || []), ...(m.enemies || [])].forEach((p) => {
    if (p.party != null) counts[p.party] = (counts[p.party] || 0) + 1;
  });
  const colors = {};
  let i = 0;
  Object.keys(counts).forEach((k) => {
    if (counts[k] > 1) colors[k] = PARTY_COLORS[i++ % PARTY_COLORS.length];
  });
  return colors;
}

function teamCard(side, title, players, parties, settings) {
  players = players || [];
  const ranked = players.filter((p) => p.rank && p.rank.tier > 2);
  const avg = ranked.length ? Math.round(ranked.reduce((a, p) => a + p.rank.tier, 0) / ranked.length) : 0;
  const avgRank = avg ? ranked.reduce((best, p) => (Math.abs(p.rank.tier - avg) < Math.abs(best.rank.tier - avg) ? p : best)).rank : null;
  return `<div class="card team ${side}">
    <div class="team-title"><span class="dot"></span>${esc(title)}
      ${avgRank ? `<span class="avg badge" title="${t("match.avg")}">${esc(avgRank.name)}</span>` : ""}
    </div>
    <div class="players">${groupByParty(players, parties).map((g) => g.color
      ? `<div class="party-group" data-key="party-${g.id}" style="--party:${g.color}">
           <span class="party-label">${icon("link")}${t("match.partyOf", { n: g.players.length })}</span>
           ${g.players.map((p) => playerRow(p, parties, settings)).join("")}
         </div>`
      : playerRow(g.players[0], parties, settings)).join("")}</div>
  </div>`;
}

// Keeps the original order but pulls party members together at the position of the first one,
// so a party is drawn as one framed block.
function groupByParty(players, parties) {
  const groups = [];
  const byParty = {};
  players.forEach((p) => {
    const color = p.party != null ? parties[p.party] : null;
    if (!color) return groups.push({ players: [p] });
    if (!byParty[p.party]) groups.push((byParty[p.party] = { id: p.party, color, players: [] }));
    byParty[p.party].players.push(p);
  });
  return groups;
}

function playerRow(p, parties, settings) {
  const party = p.party != null ? parties[p.party] : null;
  const level = p.levelHidden || !p.level
    ? `<span class="badge warn">${t("match.levelHidden")}</span>`
    : `<span class="badge num">${t("match.level", { n: p.level })}</span>`;
  const rr = p.rank && p.rank.tier > 2
    ? `<div class="rr" title="${esc(p.rank.name)}"><b class="fit-name">${esc(p.rank.name)}</b>${p.leaderboard ? t("match.lb", { n: p.leaderboard }) : t("match.rr", { n: p.rr ?? 0 })}</div>`
    : `<div class="rr" title="${esc(p.rank ? p.rank.name : "")}"><b class="fit-name">${esc(p.rank ? p.rank.name : "—")}</b>&nbsp;</div>`;
  const pct = (v) => (v == null ? "—" : v + "%");

  return `<div class="player${p.isMe ? " me" : ""}" data-player="${esc(p.puuid)}">
    ${img(p.card, "card-bg", "")}
    <div class="player-main">
      ${party ? `<span class="party" style="background:${party};--party:${party}" title="${t("match.party")}"></span>` : ""}
      ${img(p.agent && p.agent.icon, "avatar" + (p.locked ? " locked" : ""), initials(p.agent && p.agent.name))}
      <div class="who">
        ${nameHtml(p, settings.streamerMode)}
        ${p.title ? `<div class="ptitle">${esc(p.title)}</div>` : ""}
      </div>
      <div class="right">${level}${rr}${rankImg(p.rank)}</div>
    </div>
    <div class="player-extra"><div class="player-extra-in">
      <div class="stat wide"><small>${t("match.peak")}</small><b>${rankImg(p.peak)}<span class="fit-name" title="${esc(p.peak ? p.peak.name : "")}">${esc(p.peak ? p.peak.name : "—")}</span></b><span class="sub">${esc(p.peak && p.peak.act || "")}</span></div>
      <div class="stat wide"><small>${t("match.prev")}</small><b>${rankImg(p.prev)}<span class="fit-name" title="${esc(p.prev ? p.prev.name : "")}">${esc(p.prev ? p.prev.name : "—")}</span></b></div>
      <div class="stat"><small>${t("match.wr")}</small><b class="num">${pct(p.wr)}</b><span class="sub">${t("home.games", { n: p.games ?? 0 })}</span></div>
      <div class="stat"><small>${t("match.kd")}</small><b class="num">${p.kd ?? "—"}</b></div>
      <div class="stat"><small>${t("match.hs")}</small><b class="num">${pct(p.hs)}</b></div>
    </div></div>
  </div>`;
}

// ---------- Friends ----------

function renderFriends(state, settings, ui) {
  const q = (ui.friendQuery || "").toLowerCase();
  const list = (state.friends || []).filter((f) => !q || (f.name + "#" + f.tag).toLowerCase().includes(q));
  const online = list.filter((f) => f.status !== "offline");
  const offline = list.filter((f) => f.status === "offline");
  const row = (f) => `<div class="friend ${f.status}">
      ${img(f.card, "card-bg", "")}
      ${img(f.avatar, "avatar", initials(f.name))}
      <div class="who">
        <div class="name">${esc(f.name)}<span class="tag">#${esc(f.tag)}</span></div>
        <div class="status ${f.status}">${t("friends.status." + f.status)}${f.mode && f.status !== "offline" ? " — " + esc(f.mode) : ""}</div>
      </div>
      ${f.rank ? rankImg(f.rank) : ""}
    </div>`;

  return `<div class="page friends">
    <div class="page-head"><div><h1 class="page-title">${t("friends.title")}</h1><div class="page-sub">${t("friends.sub")}</div></div></div>
    <div class="friends-tools">
      <div class="search">${icon("search")}<input class="input" id="friend-search" placeholder="${t("friends.search")}" value="${esc(ui.friendQuery || "")}"></div>
    </div>
    <div id="friend-list">
      ${online.length ? `<div class="friend-group">${t("friends.online")}<span class="badge accent">${online.length}</span></div>${online.map(row).join("")}` : ""}
      ${offline.length ? `<div class="friend-group">${t("friends.offline")}<span class="badge">${offline.length}</span></div>${offline.map(row).join("")}` : ""}
      ${!list.length ? `<p class="muted" style="text-align:center;margin-top:30px">${t("friends.empty")}</p>` : ""}
    </div>
  </div>`;
}

// ---------- Discord RPC ----------

const RPC_GAME_IMAGES = ["map", "agent", "rank", "logo", "card", "none"];
const RPC_LOBBY_IMAGES = ["logo", "rank", "agent", "card", "none"];

function rpcOptions(list) {
  return list.map((v) => [v, t("rpc.img." + v)]);
}

function renderRpc(state, settings, ui) {
  const r = settings.rpc;
  const mode = ui.rpcPreview || "game";
  return `<div class="page">
    <div class="page-head"><div><h1 class="page-title">${t("rpc.title")}</h1><div class="page-sub">${t("rpc.sub")}</div></div></div>
    <div class="rpc">
      <div class="rpc-col">
        <div class="card">
          <div class="row"><div class="label"><span class="title" style="font-size:15px">${t("rpc.enabled")}</span><small>${t("rpc.enabledDesc")}</small></div>${toggle("rpc.enabled", r.enabled)}</div>
        </div>
        <div class="card">
          <div class="section-title">${t("rpc.inGame")}</div>
          <div class="field"><div class="field-label">${t("rpc.largeImage")}</div>${select("rpc.game.large", r.game.large, rpcOptions(RPC_GAME_IMAGES))}</div>
          <div class="field"><div class="field-label">${t("rpc.smallImage")}</div>${select("rpc.game.small", r.game.small, rpcOptions(RPC_GAME_IMAGES))}</div>
          <div class="sep"></div>
          <div class="section-title">${t("rpc.inLobby")}</div>
          <div class="field"><div class="field-label">${t("rpc.largeImage")}</div>${select("rpc.lobby.large", r.lobby.large, rpcOptions(RPC_LOBBY_IMAGES))}</div>
          <div class="field"><div class="field-label">${t("rpc.smallImage")}</div>${select("rpc.lobby.small", r.lobby.small, rpcOptions(RPC_LOBBY_IMAGES))}</div>
        </div>
        <div class="card">
          <div class="section-title">${t("rpc.general")}</div>
          <div class="row"><div class="label">${t("rpc.showName")}</div>${toggle("rpc.showName", r.showName)}</div>
          <div class="row"><div class="label">${t("rpc.showParty")}</div>${toggle("rpc.showParty", r.showParty)}</div>
          <div class="row"><div class="label">${t("rpc.showScore")}</div>${toggle("rpc.showScore", r.showScore)}</div>
          <div class="row"><div class="label">${t("rpc.showTimer")}</div>${toggle("rpc.showTimer", r.showTimer)}</div>
          <div class="sep"></div>
          <div class="row"><div class="label">${t("rpc.button")}</div>${toggle("rpc.button.enabled", r.button.enabled)}</div>
          <div style="${r.button.enabled ? "" : "opacity:.5;pointer-events:none"}">
            <div class="field" style="margin-top:10px"><div class="field-label">${t("rpc.buttonLabel")}</div><input class="input" data-text="rpc.button.label" maxlength="32" value="${esc(r.button.label)}"></div>
            <div class="field"><div class="field-label">${t("rpc.buttonUrl")}</div><input class="input" data-text="rpc.button.url" value="${esc(r.button.url)}" placeholder="https://"></div>
          </div>
        </div>
        <div class="card">
          <div class="section-title">${t("rpc.app")}</div>
          <div class="field-label">${t("rpc.clientId")}</div>
          <input class="input num" data-text="rpc.clientId" value="${esc(r.clientId)}" placeholder="000000000000000000">
          <div class="desc" style="margin-top:8px">${t("rpc.clientIdDesc")}</div>
        </div>
      </div>
      <div class="rpc-col rpc-preview">
        <div class="eyebrow" style="color:var(--text-muted)">${t("rpc.preview")}</div>
        ${seg("ui.rpcPreview", mode, [["game", t("rpc.inGame")], ["lobby", t("rpc.inLobby")]], true)}
        <div id="rpc-card">${discordCard(state, settings, mode)}</div>
        <div class="row" style="justify-content:center">
          <span class="badge ${state.discord && state.discord.connected ? "win" : ""}">${state.discord && state.discord.connected ? t("rpc.connected") : t("rpc.disconnected")}</span>
        </div>
      </div>
    </div>
  </div>`;
}

// Mirrors the presence the host builds in DiscordPresence.cs so the preview matches Discord.
function rpcLines(state, settings, mode) {
  const r = settings.rpc;
  const st = state.status || {};
  const me = state.me || {};
  const m = state.match || {};
  const party = st.party && st.party.size > 1 && r.showParty ? t("rpc.party", { a: st.party.size, b: st.party.max }) : null;
  const name = r.showName && me.name ? `${me.name}#${me.tag}` : null;
  const modeName = st.mode || "Competitive";
  if (mode === "lobby") {
    return { details: `${t("rpc.lobby")} — ${modeName}`, state: [name, party].filter(Boolean).join(" • ") };
  }
  const agent = st.agent ? st.agent.name : null;
  const sc = st.score || m.score;
  const score = r.showScore && sc ? `${sc.ally} – ${sc.enemy}` : null;
  return {
    details: [modeName, score].filter(Boolean).join(" • "),
    state: [name, agent, party].filter(Boolean).join(" • "),
  };
}

function rpcImage(state, kind, mode) {
  const me = state.me || {};
  const m = state.match || {};
  // Same rule as DiscordPresence.cs: current agent in a match, last played agent in the lobby.
  const agent = mode === "lobby" ? me.agent : state.status && state.status.agent;
  switch (kind) {
    case "map": return { url: m.map && m.map.splash, ph: initials(m.map && m.map.name) };
    case "agent": return { url: agent && agent.icon, ph: initials(agent && agent.name) };
    case "rank": return { url: me.rank && me.rank.icon, ph: "R" };
    case "card": return { url: me.cardSmall || me.card, ph: "" };
    case "logo": return { url: null, ph: `<svg viewBox="0 0 24 24" width="60%" fill="#FF4655"><path d="M2 5l9.5 12h-5L2 11.3zm20 0v6.3L19.6 14h-5.1z"/></svg>`, logo: true };
    default: return null;
  }
}

function discordCard(state, settings, mode) {
  const r = settings.rpc;
  const conf = mode === "lobby" ? r.lobby : r.game;
  const large = rpcImage(state, conf.large, mode);
  const small = rpcImage(state, conf.small, mode);
  const lines = rpcLines(state, settings, mode);
  const since = state.status && state.status.since ? Date.now() - state.status.since : 3 * 60e3;
  const showTimer = r.showTimer && mode === "game";
  return `<div class="discord${r.enabled ? "" : " off"}">
    <div class="discord-user">
      <div class="discord-avatar">${DISCORD_LOGO}</div>
      <div><b>${esc(state.me ? state.me.name : "Exora")}</b><small>🎮 ${t("rpc.playingTitle")}</small></div>
    </div>
    <div class="discord-label">${t("rpc.playingTitle")}</div>
    <div class="discord-act">
      <div class="discord-imgs">
        ${large ? img(large.url, "large" + (large.logo ? " contain" : ""), large.ph) : `<div class="large img"></div>`}
        ${small ? img(small.url, "small contain", small.ph) : ""}
      </div>
      <div class="discord-text">
        <div><b>Valorant</b></div>
        ${lines.details ? `<div>${esc(lines.details)}</div>` : ""}
        ${lines.state ? `<div>${esc(lines.state)}</div>` : ""}
        ${showTimer ? `<div class="time" data-since="${Date.now() - since}">${t("rpc.elapsed", { t: formatElapsed(since) })}</div>` : ""}
      </div>
    </div>
    ${r.button.enabled && r.button.label ? `<div class="discord-btn">${esc(r.button.label)}</div>` : ""}
  </div>`;
}

// ---------- Settings ----------

function renderSettings(state, settings) {
  const s = settings;
  const custom = !ACCENTS.some(([v]) => v === s.accent);
  return `<div class="page">
    <div class="page-head"><div><h1 class="page-title">${t("settings.title")}</h1><div class="page-sub">${t("settings.sub")}</div></div></div>
    <div class="settings">
      <div class="card">
        <div class="card-head"><div class="grow"><div class="title">${t("settings.look")}</div><div class="desc">${t("settings.lookDesc")}</div></div></div>
        <div class="sep"></div>
        <div class="row"><div class="label">${t("settings.theme")}</div>${seg("theme", s.theme, [["dark", t("settings.dark")], ["light", t("settings.light")]])}</div>
        <div class="field" style="margin-top:12px"><div class="field-label">${t("settings.accent")}</div>
          <div class="swatches">
            ${ACCENTS.map(([v, k]) => {
              const c = v === "mono" ? (s.theme === "light" ? "#15171C" : "#F2F3F5") : v;
              return `<button class="swatch${s.accent === v ? " on" : ""}" data-accent="${v}" title="${t("accent." + k)}" style="background:${c};color:${c}"></button>`;
            }).join("")}
            <label class="swatch swatch-custom${custom ? " on" : ""}" title="${t("accent.custom")}" style="color:${custom ? s.accent : "transparent"}"><input type="color" id="accent-custom" value="${custom ? s.accent : "#4F8CFF"}"></label>
          </div>
        </div>
        <div class="field"><div class="field-label">${t("settings.tint")}<b class="num" id="tint-val">${s.tint}%</b></div>${slider("tint", s.tint, 0, 100, 1)}</div>
        <div class="field"><div class="field-label">${t("settings.font")}</div>${select("font", s.font, FONTS)}</div>
        <div class="row" style="margin-top:12px"><div class="label">${t("settings.scale")}</div>${seg("scale", s.scale, [[0.9, "90%"], [1, "100%"], [1.1, "110%"], [1.25, "125%"]])}</div>
      </div>

      <div style="display:flex;flex-direction:column;gap:12px">
        <div class="card">
          <div class="card-head"><div class="grow"><div class="title">${t("settings.app")}</div><div class="desc">${t("settings.appDesc")}</div></div></div>
          <div class="sep"></div>
          <div class="row"><div class="label">${t("settings.lang")}</div>${seg("lang", s.lang, [["ru", "Русский"], ["en", "English"]])}</div>
          <div class="row"><div class="label">${t("settings.streamer")}<small>${t("settings.streamerDesc")}</small></div>${toggle("streamerMode", s.streamerMode)}</div>
          <div class="row"><div class="label">${t("settings.gpu")}<small>${t("settings.gpuDesc")}</small></div>${toggle("hardwareAcceleration", s.hardwareAcceleration)}</div>
          <div class="row"><div class="label">${t("settings.refresh")}</div>${seg("refreshSec", s.refreshSec, [[2, t("sec", { n: 2 })], [3, t("sec", { n: 3 })], [5, t("sec", { n: 5 })]])}</div>
        </div>
        <div class="card">
          <div class="card-head"><div class="grow"><div class="title">${t("settings.motion")}</div><div class="desc">${t("settings.motionDesc")}</div></div></div>
          <div class="sep"></div>
          <div class="row"><div class="label">${t("settings.anim")}</div>${seg("anim", s.anim, [["full", t("settings.anim.full")], ["reduced", t("settings.anim.reduced")], ["off", t("settings.anim.off")]])}</div>
        </div>
        <div class="card">
          <div class="title">${t("settings.about")}</div>
          <div class="desc" style="margin:4px 0 12px">${t("settings.aboutText")}${state.version ? " v" + esc(state.version) : ""}</div>
          <button class="btn" data-cmd="openData">${icon("folder")}${t("settings.openData")}</button>
        </div>
      </div>
    </div>
  </div>`;
}
