// Demo data for opening the UI in a plain browser (no desktop host attached).
const MOCK = (() => {
  const media = "https://media.valorant-api.com";
  const tiers = "03621f52-342b-cf4e-4f86-9350a49c6d04";
  const agent = (uuid, name) => ({ name, icon: `${media}/agents/${uuid}/displayicon.png` });
  const rank = (tier) => ({ tier, icon: tier > 2 ? `${media}/competitivetiers/${tiers}/${tier}/smallicon.png` : null, name: RANK_NAMES[tier] });
  const RANK_NAMES = ["Unranked", "Unused 1", "Unused 2", "Iron 1", "Iron 2", "Iron 3", "Bronze 1", "Bronze 2", "Bronze 3", "Silver 1", "Silver 2", "Silver 3",
    "Gold 1", "Gold 2", "Gold 3", "Platinum 1", "Platinum 2", "Platinum 3", "Diamond 1", "Diamond 2", "Diamond 3",
    "Ascendant 1", "Ascendant 2", "Ascendant 3", "Immortal 1", "Immortal 2", "Immortal 3", "Radiant"];
  const card = (uuid) => `${media}/playercards/${uuid}/wideart.png`;

  const A = {
    jett: agent("add6443a-41bd-e414-f6ad-e58d267f4e95", "Jett"),
    fade: agent("dade69b4-4f5a-8528-247b-219e5a1facd6", "Fade"),
    reyna: agent("a3bfb853-43b2-7238-a4f1-ad90e9e46bcc", "Reyna"),
    sova: agent("320b2a48-4d9b-a075-30f1-1f93a9b638fa", "Sova"),
    viper: agent("707eab51-4836-f488-046a-cda6bf494859", "Viper"),
    omen: agent("8e253930-4c05-31dd-1b6c-968525494517", "Omen"),
    sage: agent("569fdd95-4d10-43ab-ca70-79becc718b46", "Sage"),
    killjoy: agent("1e58de9c-4950-5125-93e9-a0aee9f98746", "Killjoy"),
    raze: agent("f94c3b30-42be-e959-889c-5aa313dba261", "Raze"),
    skye: agent("6f2a04ca-43e0-be17-7f36-b3908627744d", "Skye"),
  };

  const player = (o) => ({
    puuid: Math.random().toString(36).slice(2), hidden: false, level: 0, title: "", locked: true, party: null, isMe: false,
    rr: 0, wr: null, games: 0, kd: null, hs: null, leaderboard: 0, card: card("9fb348bc-41a0-91ad-8a3e-818035c4e561"), ...o,
    rank: rank(o.rankTier ?? 0), peak: { ...rank(o.peakTier ?? o.rankTier ?? 0), act: o.peakAct || "" }, prev: rank(o.prevTier ?? 0),
  });

  const allies = [
    player({ name: "RawenCat", tag: "rawr", isMe: true, agent: A.fade, title: "Aimbot", level: 226, rankTier: 21, rr: 47, peakTier: 22, peakAct: "E9 A2", prevTier: 20, wr: 56, games: 48, kd: 1.31, hs: 27, party: 0 }),
    player({ name: "AleSuw", tag: "Niza", agent: A.sage, title: ":3", level: 0, rankTier: 14, rr: 81, peakTier: 16, peakAct: "E8 A3", prevTier: 13, wr: 51, games: 31, kd: 0.94, hs: 19, party: 0 }),
    player({ name: "sauveur compulsif", tag: "EUW", agent: A.omen, title: "Aura", level: 0, rankTier: 18, rr: 12, peakTier: 19, prevTier: 17, wr: 49, games: 72, kd: 1.05, hs: 22 }),
    player({ name: "Omega Lux", tag: "8812", agent: A.killjoy, title: "NPC", level: 0, rankTier: 10, rr: 33, peakTier: 12, prevTier: 9, wr: 44, games: 18, kd: 0.82, hs: 14 }),
    player({ name: "ScorpiOn", tag: "MAIN", agent: A.raze, title: "Grrr", level: 0, rankTier: 16, rr: 64, peakTier: 17, prevTier: 15, wr: 53, games: 40, kd: 1.12, hs: 21 }),
  ];
  const enemies = [
    player({ name: "Reyna", hidden: true, agent: A.reyna, title: "Aimbot", level: 345, rankTier: 16, rr: 20, peakTier: 18, prevTier: 16, wr: 52, games: 61, kd: 1.22, hs: 25, party: 1 }),
    player({ name: "rudnez", tag: "9888", agent: A.jett, title: "Green Flag", level: 115, rankTier: 16, rr: 71, peakTier: 17, prevTier: 15, wr: 50, games: 22, kd: 1.01, hs: 20, party: 1 }),
    player({ name: "Saasil花Lu", tag: "月の花 鬼", agent: A.skye, title: "Wub Wub", level: 274, rankTier: 17, rr: 5, peakTier: 19, prevTier: 17, wr: 47, games: 54, kd: 0.97, hs: 18, party: 2 }),
    player({ name: "Sova", hidden: true, agent: A.sova, title: "", level: 0, levelHidden: true, rankTier: 18, rr: 90, peakTier: 20, prevTier: 18, wr: 58, games: 88, kd: 1.4, hs: 31 }),
    player({ name: "Viper", hidden: true, agent: A.viper, title: "Aura", level: 475, rankTier: 15, rr: 42, peakTier: 16, prevTier: 14, wr: 45, games: 26, kd: 0.88, hs: 16, party: 2 }),
  ];

  const map = { name: "Split", splash: `${media}/maps/d960549e-485c-e861-8d71-aa9d1aed12a2/splash.png`, icon: `${media}/maps/d960549e-485c-e861-8d71-aa9d1aed12a2/listviewicon.png` };

  const state = {
    connection: "ok",
    status: { state: "INGAME", mode: "Swiftplay", queue: "swiftplay", party: { size: 2, max: 5 }, score: { ally: 2, enemy: 1 }, since: Date.now() - 3 * 60e3, agent: A.fade },
    me: {
      name: "RawenCat", tag: "rawr", title: "Aimbot", level: 226, card: allies[0].card,
      rank: rank(21), rr: 47, peak: { ...rank(22), act: "E9 A2" }, wr: 56, games: 48,
      agent: A.jett,
      recent: [
        { result: "L", agent: A.jett, map: "Ascent", mode: "Competitive", score: "9–13", kda: "14/17/5" },
        { result: "W", agent: A.fade, map: "Lotus", mode: "Competitive", score: "13–7", kda: "21/12/9" },
        { result: "W", agent: A.jett, map: "Bind", mode: "Unrated", score: "13–10", kda: "19/15/4" },
        { result: "W", agent: A.sova, map: "Sunset", mode: "Competitive", score: "13–11", kda: "16/14/11" },
        { result: "L", agent: A.jett, map: "Haven", mode: "Swiftplay", score: "3–5", kda: "6/8/2" },
      ],
    },
    match: { map, mode: "Swiftplay", state: "INGAME", score: { ally: 2, enemy: 1 }, allies, enemies, single: false },
    friends: [
      { name: "TRAUMADAS LOVER", tag: "Cul0n", status: "ingame", mode: "Swiftplay", rank: rank(16), card: card("33c1f011-4eca-068c-9751-f68c788b2eee"), avatar: `${media}/playercards/33c1f011-4eca-068c-9751-f68c788b2eee/smallart.png` },
      { name: "TP Jayko シ", tag: "bot", status: "ingame", mode: "Competitive", rank: rank(19), card: card("bb6ae873-43ec-efb4-3ea6-93ac00a82d4e") },
      { name: "noteminimal66", tag: "2134q", status: "pregame", mode: "Swiftplay", rank: rank(9), card: card("7fdbe3ef-4fd8-a5a6-0ab2-0ab06da7caa9") },
      { name: "cosmo", tag: "김채원", status: "ingame", mode: "", rank: rank(21), card: card("30b64514-440d-1261-f863-6bbb180263f9") },
      { name: "Koke GS", tag: "KOKE", status: "menus", mode: "Competitive", rank: rank(19), card: card("bb6ae873-43ec-efb4-3ea6-93ac00a82d4e") },
      { name: "AleSuw", tag: "Niza", status: "ingame", mode: "Swiftplay", rank: rank(0), card: card("9fb348bc-41a0-91ad-8a3e-818035c4e561") },
      { name: "yepa2836", tag: "LAN", status: "menus", mode: "Competitive", rank: rank(21), card: card("33c1f011-4eca-068c-9751-f68c788b2eee") },
      { name: "midnight", tag: "0001", status: "offline", mode: "", rank: rank(12), card: null },
      { name: "kuro", tag: "neko", status: "offline", mode: "", rank: null, card: null },
    ],
    discord: { connected: true },
    valorantRunning: true,
  };

  const news = [
    { date: "2026-10-21", title: "Patch Notes 13.08", text: "Agent balance pass and Premier playoff fixes.", url: "https://playvalorant.com/news/tags/patch-notes/" },
    { date: "2026-10-07", title: "Patch Notes 13.07", text: "Map pool rotation and a new Swiftplay ruleset.", url: "https://playvalorant.com/news/tags/patch-notes/" },
    { date: "2026-09-23", title: "Patch Notes 13.06", text: "Small patch focused on bug fixes and performance.", url: "https://playvalorant.com/news/tags/patch-notes/" },
    { date: "2026-09-09", title: "Patch Notes 13.05", text: "Shotgun tuning and quality-of-life updates in the client.", url: "https://playvalorant.com/news/tags/patch-notes/" },
    { date: "2026-08-26", title: "Patch Notes 13.04", text: "New act, battle pass and competitive updates.", url: "https://playvalorant.com/news/tags/patch-notes/" },
  ];

  return { state, news };
})();
