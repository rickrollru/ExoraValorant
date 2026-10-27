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

// URLs that failed once are not requested again, so periodic updates don't retry them.
const FAILED_IMAGES = new Set();

function imgFailed(el) {
  FAILED_IMAGES.add(el.getAttribute("src"));
  el.remove();
}

// Image with a placeholder underneath; a failed image removes itself so the placeholder shows.
// Loaded images fade in instead of popping.
function img(url, cls, placeholder) {
  const ok = url && !FAILED_IMAGES.has(url);
  return `<div class="img ${cls || ""}"><span class="ph">${placeholder ?? ""}</span>${ok ? `<img class="pic" src="${esc(url)}" alt="" onload="this.classList.add('ready')" onerror="imgFailed(this)">` : ""}</div>`;
}

// Patches `from` in place to match `to`, keeping nodes that did not change. Periodic state updates use this
// instead of innerHTML so images are not reloaded and hover, focus and running transitions survive.
const KEY_ATTRS = ["data-player", "data-page", "data-select", "data-seg", "data-key"];
const KEEP_CLASSES = ["open", "ready"];

function morph(from, to) {
  morphAttrs(from, to);
  if (from.tagName === "INPUT" || from.tagName === "TEXTAREA") {
    morphInput(from, to);
    return;
  }
  const a = Array.from(from.childNodes);
  const b = Array.from(to.childNodes);
  b.forEach((y, i) => {
    const x = a[i];
    if (!x) return from.appendChild(y);
    if (x.nodeType !== y.nodeType || x.nodeName !== y.nodeName || KEY_ATTRS.some((k) => x.nodeType === 1 && x.getAttribute(k) !== y.getAttribute(k))) {
      return from.replaceChild(y, x);
    }
    if (x.nodeType === 3 || x.nodeType === 8) {
      if (x.nodeValue !== y.nodeValue) x.nodeValue = y.nodeValue;
    } else if (x.nodeType === 1) {
      morph(x, y);
    }
  });
  for (let i = b.length; i < a.length; i++) a[i].remove();
}

// Sets an element's content through morph() so unchanged children stay untouched.
function patchHtml(el, html) {
  const next = el.cloneNode(false);
  next.innerHTML = html;
  morph(el, next);
}

function morphAttrs(from, to) {
  for (const { name, value } of Array.from(to.attributes)) {
    if (name === "class") {
      const keep = KEEP_CLASSES.filter((c) => from.classList.contains(c) && !to.classList.contains(c));
      const next = keep.length ? value + " " + keep.join(" ") : value;
      if (from.getAttribute("class") !== next) from.setAttribute("class", next);
    } else if (from.getAttribute(name) !== value) {
      from.setAttribute(name, value);
    }
  }
  for (const { name } of Array.from(from.attributes)) {
    if (to.hasAttribute(name)) continue;
    // Sliding pills are positioned from JS, not from the markup.
    if (from.classList.contains("seg-pill") || from.classList.contains("nav-pill")) continue;
    if (name === "class") {
      // Runtime classes such as "ready" (image loaded) must survive even when the markup has no class at all.
      const keep = KEEP_CLASSES.filter((c) => from.classList.contains(c));
      if (keep.length) {
        from.setAttribute("class", keep.join(" "));
        continue;
      }
    }
    from.removeAttribute(name);
  }
}

function morphInput(from, to) {
  if (from.type === "checkbox") {
    from.checked = to.hasAttribute("checked");
  } else if (from !== document.activeElement) {
    const v = to.getAttribute("value") ?? "";
    if (from.value !== v) from.value = v;
  }
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
    placePill(pill, { left: on.offsetLeft + "px", width: on.offsetWidth + "px" });
  });
}

// Moves a sliding pill; the very first placement happens without a transition so it doesn't fly in from 0.
function placePill(pill, props) {
  const first = !pill.dataset.placed;
  if (first) pill.style.transition = "none";
  Object.assign(pill.style, props);
  if (first) {
    pill.dataset.placed = "1";
    pill.offsetWidth;
    pill.style.transition = "";
  }
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

let themeKey = null;
let themeTimer = 0;

function applyTheme(s) {
  const root = document.documentElement;
  const light = s.theme === "light";

  // Cross-fade colours when the theme or accent changes (not on first paint, not while dragging the tint slider).
  const key = `${s.theme}|${s.accent}`;
  if (themeKey !== null && key !== themeKey && s.anim !== "off") {
    root.classList.add("theming");
    clearTimeout(themeTimer);
    themeTimer = setTimeout(() => root.classList.remove("theming"), 450);
  }
  themeKey = key;
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
