// Shared helpers: host bridge, theme, small components, toasts.

const host = window.chrome && window.chrome.webview ? window.chrome.webview : null;

function send(cmd, data) {
  if (host) host.postMessage({ cmd, ...data });
}

function esc(v) {
  return String(v ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

function initials(name) {
  const s = String(name || "?").trim();
  return esc(s.slice(0, 2).toUpperCase());
}

// Image with a placeholder underneath; a failed image removes itself so the placeholder shows.
function img(url, cls, placeholder) {
  return `<div class="img ${cls || ""}"><span class="ph">${placeholder ?? ""}</span>${url ? `<img src="${esc(url)}" loading="lazy" alt="" onerror="this.remove()">` : ""}</div>`;
}

function rankImg(rank, cls) {
  return img(rank && rank.icon, "contain rank-img " + (cls || ""), "");
}

function nameHtml(p, streamer) {
  if (p.hidden || (streamer && !p.isMe)) {
    const label = p.agent ? p.agent.name : t("match.hiddenName");
    return `<div class="name${p.hidden ? "" : ""}">${esc(label)}</div>`;
  }
  return `<div class="name">${esc(p.name)}<span class="tag">#${esc(p.tag)}</span></div>`;
}

function toggle(path, checked) {
  return `<label class="toggle"><input type="checkbox" data-set="${path}" ${checked ? "checked" : ""}><span></span></label>`;
}

function seg(path, value, options, full) {
  return `<div class="seg${full ? " full" : ""}" data-seg="${path}"><span class="seg-pill"></span>${options
    .map(([v, label]) => `<button type="button" data-value="${esc(v)}" class="${String(v) === String(value) ? "on" : ""}">${esc(label)}</button>`)
    .join("")}</div>`;
}

function select(path, value, options) {
  const cur = options.find(([v]) => v === value) || options[0];
  return `<div class="select" data-select="${path}">
    <button type="button" class="select-btn">${esc(cur[1])}${icon("chevron", "chev")}</button>
    <div class="select-menu">${options
      .map(([v, label]) => `<button type="button" class="select-opt${v === value ? " on" : ""}" data-value="${esc(v)}">${esc(label)}</button>`)
      .join("")}</div>
  </div>`;
}

function slider(path, value, min, max, step) {
  const p = ((value - min) / (max - min)) * 100;
  return `<input class="slider" type="range" data-set="${path}" min="${min}" max="${max}" step="${step}" value="${value}" style="--p:${p}%">`;
}

// Moves the accent pill of every segmented control under root to its selected button.
function layoutSegs(root) {
  root.querySelectorAll(".seg").forEach((s) => {
    const on = s.querySelector("button.on");
    const pill = s.querySelector(".seg-pill");
    if (!on || !pill) { if (pill) pill.style.width = "0"; return; }
    pill.style.left = on.offsetLeft + "px";
    pill.style.width = on.offsetWidth + "px";
  });
}

function toast(title, text, kind) {
  const box = document.getElementById("toasts");
  const el = document.createElement("div");
  el.className = "toast " + (kind || "");
  el.innerHTML = `<b>${esc(title)}</b>${text ? `<p>${esc(text)}</p>` : ""}<div class="bar"></div>`;
  box.appendChild(el);
  setTimeout(() => {
    el.classList.add("out");
    el.addEventListener("animationend", () => el.remove(), { once: true });
  }, 3500);
}

function getPath(obj, path) {
  return path.split(".").reduce((o, k) => (o == null ? o : o[k]), obj);
}

function setPath(obj, path, value) {
  const keys = path.split(".");
  let o = obj;
  for (let i = 0; i < keys.length - 1; i++) o = o[keys[i]] ??= {};
  o[keys[keys.length - 1]] = value;
}

function formatElapsed(ms) {
  const s = Math.max(0, Math.floor(ms / 1000));
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
  const two = (n) => String(n).padStart(2, "0");
  return h ? `${h}:${two(m)}:${two(sec)}` : `${two(m)}:${two(sec)}`;
}

function formatDate(iso) {
  const d = new Date(iso);
  if (isNaN(d)) return "";
  return d.toLocaleDateString(LANG === "ru" ? "ru-RU" : "en-US", { day: "numeric", month: "long", year: "numeric" });
}

// ---------- Theme ----------

const ACCENTS = [
  ["#4F8CFF", "blue"], ["#3DD6A4", "mint"], ["#FF8A3D", "orange"], ["#FF5C8A", "pink"],
  ["#B6E83C", "lime"], ["#FF4D4D", "red"], ["mono", "mono"],
];

const FONTS = [
  ["Manrope", "Manrope"], ["Onest", "Onest"], ["Unbounded", "Unbounded"], ["Segoe UI Variable Text", "Segoe UI"],
];

function hexToRgb(hex) {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex);
  if (!m) return [79, 140, 255];
  const n = parseInt(m[1], 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

function mix(rgbA, rgbB, k) {
  return rgbA.map((v, i) => Math.round(v + (rgbB[i] - v) * k));
}

function applyTheme(s) {
  const root = document.documentElement;
  const light = s.theme === "light";
  root.dataset.theme = light ? "light" : "dark";

  const accent = s.accent === "mono" ? (light ? "#15171C" : "#F2F3F5") : s.accent;
  const [r, g, b] = hexToRgb(accent);
  const bright = (0.299 * r + 0.587 * g + 0.114 * b) / 255 > 0.6;
  root.style.setProperty("--accent", accent);
  root.style.setProperty("--on-accent", bright ? "#0A0B0E" : "#FFFFFF");

  // Background tint: blend neutral surfaces toward the accent (up to 30% dark, 18% light).
  const k = Math.max(0, Math.min(1, (s.tint || 0) / 100)) * (light ? 0.18 : 0.30);
  const base = light
    ? { bg: [243, 244, 247], panel: [255, 255, 255], card: [255, 255, 255], hover: [247, 248, 250] }
    : { bg: [10, 11, 14], panel: [17, 19, 24], card: [28, 31, 38], hover: [34, 38, 46] };
  const alpha = light ? { panel: 0.86, card: 0.95, hover: 0.97 } : { panel: 0.82, card: 0.9, hover: 0.92 };
  const tinted = (c) => mix(c, [r, g, b], k);
  if (k > 0) {
    root.style.setProperty("--bg", `rgb(${tinted(base.bg)})`);
    root.style.setProperty("--panel", `rgba(${tinted(base.panel)},${alpha.panel})`);
    root.style.setProperty("--card", `rgba(${tinted(base.card)},${alpha.card})`);
    root.style.setProperty("--card-hover", `rgba(${tinted(base.hover)},${alpha.hover})`);
  } else {
    ["--bg", "--panel", "--card", "--card-hover"].forEach((p) => root.style.removeProperty(p));
  }

  root.style.setProperty("--font", `"${s.font}", "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif`);
  root.style.setProperty("--anim", { full: 1, reduced: 0.5, off: 0.001 }[s.anim] ?? 1);
  root.lang = s.lang;
}
