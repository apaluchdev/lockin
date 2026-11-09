// LockIn phone page: live champ select over a WebSocket to the PC.
"use strict";

const KEY_STORAGE = "lockin.key";
const STALE_AFTER_MS = 3000;
const POSITIONS = { top: "Top", jungle: "Jungle", middle: "Mid", bottom: "Bot", utility: "Support" };
const ROLE_KEYS = { "Any role": "Any", Top: "Top", Jungle: "Jungle", Mid: "Middle", Bot: "Bottom", Support: "Support" };

const $ = (id) => document.getElementById(id);
const el = (tag, cls, text) => {
  const node = document.createElement(tag);
  if (cls) node.className = cls;
  if (text !== undefined) node.textContent = text;
  return node;
};

let key = null;
let socket = null;
let socketOpen = false;
let reconnectDelay = 1000;
let lastMessageAt = 0;
let clockOffset = 0;          // PC clock minus phone clock
let state = null;
let champions = new Map();    // id → { id, name, playable }
let lists = { picks: {}, bans: {} };
let sheetKind = null;
let loadoutMode = null;
let requestSeq = 0;

// ── Key handling ─────────────────────────────────────────────────────

function readKey() {
  const match = /[#&]k=([^&]+)/.exec(location.hash);
  if (match) {
    const fromLink = decodeURIComponent(match[1]);
    try { localStorage.setItem(KEY_STORAGE, fromLink); } catch { /* private mode */ }
    history.replaceState(null, "", location.pathname); // Don't leave the key in the address bar.
    return fromLink;
  }
  try { return localStorage.getItem(KEY_STORAGE); } catch { return null; }
}

function forgetKey() {
  key = null;
  try { localStorage.removeItem(KEY_STORAGE); } catch { /* ignore */ }
}

function showPairing(message) {
  $("live").hidden = true;
  $("pairing").hidden = false;
  $("pairError").textContent = message || "";
  setPulse("down", "Not paired");
  $("pairCode").focus();
}

$("pairForm").addEventListener("submit", async (event) => {
  event.preventDefault();
  $("pairError").textContent = "";
  try {
    const response = await fetch("/api/pair", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ code: $("pairCode").value }),
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) {
      $("pairError").textContent = body.message || "Couldn't pair.";
      return;
    }
    key = body.key;
    try { localStorage.setItem(KEY_STORAGE, key); } catch { /* ignore */ }
    $("pairing").hidden = true;
    start();
  } catch {
    $("pairError").textContent = "Can't reach your PC. Is LockIn running?";
  }
});

// ── Connection ───────────────────────────────────────────────────────

async function start() {
  key = key || readKey();
  if (!key) {
    showPairing();
    return;
  }

  // Check the key (and load champion names) before opening the socket: a WebSocket can't report a 401.
  try {
    const response = await fetch("/api/champions", { headers: { Authorization: "Bearer " + key } });
    if (response.status === 401) {
      forgetKey();
      showPairing("This phone isn't paired anymore. Enter the new code from your PC.");
      return;
    }
    const data = await response.json();
    champions = new Map(data.champions.map((c) => [c.id, c]));
    lists = { picks: data.picks || {}, bans: data.bans || {} };
  } catch {
    setPulse("down", "Can't reach PC");
    scheduleReconnect();
    return;
  }

  $("pairing").hidden = true;
  $("live").hidden = false;
  connect();
}

function connect() {
  setPulse("connecting", "Connecting…");
  const scheme = location.protocol === "https:" ? "wss" : "ws";
  socket = new WebSocket(`${scheme}://${location.host}/ws?k=${encodeURIComponent(key)}`);

  socket.addEventListener("open", () => {
    socketOpen = true;
    reconnectDelay = 1000;
  });

  socket.addEventListener("message", (event) => {
    lastMessageAt = Date.now();
    const message = JSON.parse(event.data);
    if (message.type === "state") {
      if (reloadIfOutdated(message.build)) return;
      clockOffset = message.state.serverTime - Date.now();
      state = message.state;
      render();
    } else if (message.type === "heartbeat") {
      clockOffset = message.serverTime - Date.now();
    } else if (message.type === "result") {
      toast(message.ok ? "Done" : message.message || "That didn't work", message.ok ? "good" : "bad");
    }
    beat();
  });

  socket.addEventListener("close", () => {
    socketOpen = false;
    setPulse("down", "Reconnecting…");
    scheduleReconnect();
  });
}

// A tab left open across a LockIn update reconnects but keeps running the old page, so load the new one.
// Remembers the attempt so a stale cached page can't cause a reload loop.
function reloadIfOutdated(build) {
  const mine = document.querySelector('meta[name="lockin-build"]')?.content;
  if (!build || !mine || build === mine) return false;
  try {
    if (sessionStorage.getItem("lockin.reloadedFor") === build) return false;
    sessionStorage.setItem("lockin.reloadedFor", build);
  } catch {
    return false;
  }
  location.reload();
  return true;
}

function scheduleReconnect() {
  setTimeout(() => (champions.size ? connect() : start()), reconnectDelay);
  reconnectDelay = Math.min(reconnectDelay * 2, 5000);
}

// Reconnect promptly when the phone wakes up or comes back to the tab.
document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible" && socket && socket.readyState > 1) {
    reconnectDelay = 250;
  }
});

function send(message) {
  if (!socketOpen) {
    toast("Not connected to your PC", "bad");
    return;
  }
  socket.send(JSON.stringify({ ...message, id: ++requestSeq }));
}

// ── Heartbeat indicator ──────────────────────────────────────────────

function setPulse(mode, text) {
  const pulse = $("pulse");
  pulse.classList.remove("is-live", "is-stale", "is-down", "is-connecting");
  pulse.classList.add("is-" + mode);
  $("pulseText").textContent = text;
}

function beat() {
  const pulse = $("pulse");
  pulse.classList.remove("beat");
  void pulse.offsetWidth; // Restart the animation.
  pulse.classList.add("beat");
}

setInterval(() => {
  if (!socketOpen) return;
  const age = Date.now() - lastMessageAt;
  if (age < STALE_AFTER_MS) {
    setPulse("live", "Live");
  } else {
    setPulse("stale", `No update for ${Math.round(age / 1000)} s`);
  }
  tickTimer();
}, 250);

// ── Rendering ────────────────────────────────────────────────────────

function nameOf(id) {
  return champions.get(id)?.name || (id ? `#${id}` : "");
}

function icon(id, size) {
  const box = el("span", "icon" + (size ? " " + size : ""));
  if (!id) {
    box.classList.add("empty");
    return box;
  }
  const img = el("img");
  img.alt = "";
  img.loading = "lazy";
  img.src = `/icons/${id}.png`;
  img.addEventListener("error", () => {
    img.remove();
    box.textContent = nameOf(id).slice(0, 2);
  });
  box.append(img);
  return box;
}

function render() {
  if (!state) return;
  const cs = state.champSelect;

  $("client").textContent = state.connection === "connected"
    ? `League: ${state.summoner || "connected"} · auto-accept ${state.autoAccept ? "on" : "off"} · automation ${state.automation ? "on" : "off"}`
    : "League client isn't running on your PC";

  renderStatus(cs);
  renderReadyCheck();
  renderMine(cs);
  renderLoadout();
  renderTeams(cs);
  renderDecisions();
}

function renderStatus(cs) {
  const card = $("phase").closest(".card");
  card.classList.toggle("my-turn", !!cs?.myTurn);

  if (!cs) {
    $("phase").textContent = state.phaseLabel;
    $("phaseSub").textContent = state.phase === "Matchmaking" ? "Waiting for a match…" : "";
    $("timer").hidden = true;
    return;
  }

  if (cs.myTurn === "ban") {
    $("phase").textContent = "Your ban!";
  } else if (cs.myTurn === "pick") {
    $("phase").textContent = "Your pick!";
  } else {
    $("phase").textContent = cs.stageLabel;
  }

  const acting = cs.myTeam.concat(cs.theirTeam).filter((p) => (p.isActing || p.isBanning) && !p.isMe);
  const parts = [];
  if (!cs.myTurn && acting.length) {
    const allies = cs.myTeam.filter((p) => p.isActing && !p.isMe).map((p) => POSITIONS[p.position] || "Teammate");
    const enemies = cs.theirTeam.filter((p) => p.isActing).length;
    if (cs.stage === "bans") parts.push("Everyone is banning");
    else if (allies.length) parts.push(`${allies.join(", ")} picking`);
    else if (enemies) parts.push("Enemy team picking");
  }
  parts.push(cs.role + (cs.queue ? " · " + cs.queue : ""));
  $("phaseSub").textContent = parts.join(" — ");
  $("timer").hidden = cs.remainingMs == null;
  tickTimer();
}

function tickTimer() {
  const cs = state?.champSelect;
  if (!cs || cs.remainingMs == null) return;
  const now = Date.now() + clockOffset;
  const left = Math.max(0, cs.remainingMs - (now - cs.asOf));
  const total = Math.max(cs.totalMs || 1, 1);
  $("timerBar").style.width = `${Math.min(100, (left / total) * 100)}%`;
  $("timerText").textContent = `${Math.ceil(left / 1000)} s`;
  $("timer").classList.toggle("low", left <= 5000);
}

function renderReadyCheck() {
  const rc = state.readyCheck;
  $("readyCard").hidden = !rc;
  if (!rc) return;
  $("readyText").textContent =
    rc.playerResponse === "Accepted" ? "You accepted ✓ — waiting for everyone"
    : rc.playerResponse === "Declined" ? "Declined"
    : state.autoAccept ? "Accepting…" : "Auto-accept is off — accept it on your PC";
}

function renderMine(cs) {
  const show = !!cs && (cs.automated || !!cs.myTurn);
  $("mineCard").hidden = !show;
  if (!show) return;

  renderPlan("ban", cs.plannedBan, cs.banOverride, cs.canLockBan, cs.automated && (cs.plannedBan || cs.myTurn === "ban"));
  renderPlan("pick", cs.plannedPick, cs.pickOverride, cs.canLockPick, cs.automated && !!(cs.plannedPick || cs.myTurn === "pick"));

  const note = $("autoNote");
  note.hidden = cs.automated || !cs.automationNote;
  note.textContent = cs.automationNote || "";

  // Nothing left to decide (e.g. finalization): hide the card instead of showing it empty.
  $("mineCard").hidden = $("banPlan").hidden && $("pickPlan").hidden && note.hidden;
}

function renderPlan(kind, plan, override, canLock, visible) {
  const box = $(kind + "Plan");
  const myTurn = state.champSelect.myTurn === kind;
  box.hidden = !visible && !myTurn;
  if (box.hidden) return;

  const chip = $(kind + "Champ");
  chip.replaceChildren();
  const id = plan?.championId;
  chip.append(icon(id, "big"));
  const name = el("div", "name", id ? nameOf(id) : "Nothing available");
  if (plan?.source === "Override") name.append(el("span", "badge override", "Override"));
  if (plan?.source === "Manual") name.append(el("span", "badge manual", "Your hover"));
  chip.append(name);

  $(kind + "Why").textContent = plan?.explanation || "";
  box.querySelector(`[data-change="${kind}"]`).hidden = !state.champSelect.automated;
  box.querySelector(`[data-lock="${kind}"]`).hidden = !canLock;
}

function renderLoadout() {
  const loadout = state.loadout;
  $("loadoutCard").hidden = !loadout;
  if (!loadout) return;

  const rune = $("runeName");
  rune.textContent = loadout.runePage || "Your current page";
  if (loadout.runeOverride) rune.append(el("span", "badge override", "Override"));

  const spells = $("spellNames");
  spells.textContent = `${loadout.spell1 || "—"} (D) · ${loadout.spell2 || "—"} (F)`;
  if (loadout.spellOverride) spells.append(el("span", "badge override", "Override"));
}

function renderTeams(cs) {
  $("teamsCard").hidden = !cs;
  if (!cs) return;
  fillTeam($("myTeam"), cs.myTeam, true);
  fillTeam($("theirTeam"), cs.theirTeam, false);
  fillBans($("myBans"), cs.myTeamBans);
  fillBans($("theirBans"), cs.theirTeamBans);
}

function fillTeam(list, players, ally) {
  list.replaceChildren(...players.map((p) => {
    const li = el("li", p.status);
    if (p.isMe) li.classList.add("me");
    if (p.isActing || p.isBanning) li.classList.add("acting");
    li.append(icon(p.championId));
    const who = el("div", "who");
    const champName = p.championId ? nameOf(p.championId) : (p.isActing ? "Picking…" : "—");
    who.append(el("div", "champ", champName));
    const pos = [p.isMe ? "You" : (POSITIONS[p.position] || (ally ? "Teammate" : "Enemy"))];
    if (p.isBanning) pos.push("banning…");
    else if (p.isActing) pos.push("picking…");
    else if (p.status === "intent") pos.push("intent");
    who.append(el("div", "pos", pos.join(" · ")));
    li.append(who);
    return li;
  }));
}

function fillBans(box, ids) {
  box.replaceChildren(...ids.map((id) => {
    const i = icon(id, "small");
    i.title = nameOf(id);
    return i;
  }));
}

function renderDecisions() {
  const items = (state.decisions || []).slice().reverse();
  const list = $("decisions");
  if (!items.length) {
    list.replaceChildren(el("li", "empty", "Nothing yet — LockIn's decisions show up here."));
    return;
  }
  list.replaceChildren(...items.map((d) => {
    const li = el("li");
    const time = el("time", null, new Date(d.time - clockOffset).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" }));
    li.append(time, el("span", null, d.message));
    return li;
  }));
}

// ── Actions ──────────────────────────────────────────────────────────

document.addEventListener("click", (event) => {
  const change = event.target.closest("[data-change]");
  if (change) openSheet(change.dataset.change);
  const lock = event.target.closest("[data-lock]");
  if (lock) send({ type: "lockNow", kind: lock.dataset.lock });
  const loadout = event.target.closest("[data-loadout]");
  if (loadout) openLoadoutSheet(loadout.dataset.loadout);
  const page = event.target.closest("[data-page]");
  if (page) {
    send({ type: "runeOverride", pageId: Number(page.dataset.page) });
    closeSheet();
  }
  if (event.target.closest("[data-close]")) closeSheet();
});

async function openLoadoutSheet(mode) {
  let options;
  try {
    const response = await fetch("/api/loadout-options", { headers: { Authorization: "Bearer " + key } });
    if (!response.ok) throw new Error(String(response.status));
    options = await response.json();
  } catch {
    toast("Couldn't load your rune pages and spells", "bad");
    return;
  }

  loadoutMode = mode;
  const runes = mode === "runes";
  $("loadoutTitle").textContent = runes ? "Choose a rune page" : "Choose summoner spells";
  $("runeOptions").hidden = !runes;
  $("spellOptions").hidden = runes;
  $("clearLoadout").hidden = !(runes ? state?.loadout?.runeOverride : state?.loadout?.spellOverride);

  if (runes) {
    const buttons = options.pages.map((p) => {
      const button = el("button", p.isActive ? "selected" : "", p.name);
      button.type = "button";
      button.dataset.page = p.id;
      if (p.isActive) button.append(el("span", "badge", "Current"));
      return button;
    });
    $("runeOptions").replaceChildren(...(buttons.length ? buttons : [el("p", "option-empty", "No saved rune pages found.")]));
  } else {
    fillSpellSelect($("spell1"), options.spells, state?.loadout?.spell1Id);
    fillSpellSelect($("spell2"), options.spells, state?.loadout?.spell2Id);
  }

  $("loadoutSheet").hidden = false;
}

function fillSpellSelect(select, spells, current) {
  const placeholder = el("option", null, "Choose…");
  placeholder.value = "";
  select.replaceChildren(placeholder, ...spells.map((s) => {
    const option = el("option", null, s.name);
    option.value = s.id;
    return option;
  }));
  select.value = current == null ? "" : String(current);
}

$("spellOptions").addEventListener("submit", (event) => {
  event.preventDefault();
  const d = Number($("spell1").value);
  const f = Number($("spell2").value);
  if (!d || !f) {
    toast("Choose a spell for both D and F", "bad");
    return;
  }
  if (d === f) {
    toast("Choose two different spells", "bad");
    return;
  }
  send({ type: "spellOverride", spell1Id: d, spell2Id: f });
  closeSheet();
});

$("clearLoadout").addEventListener("click", () => {
  send(loadoutMode === "runes"
    ? { type: "runeOverride", pageId: null }
    : { type: "spellOverride", spell1Id: null, spell2Id: null });
  closeSheet();
});

function openSheet(kind) {
  sheetKind = kind;
  const cs = state?.champSelect;
  $("sheetTitle").textContent = kind === "pick" ? "Choose your pick" : "Choose your ban";
  $("search").value = "";
  $("clearOverride").hidden = !(kind === "pick" ? cs?.pickOverride : cs?.banOverride);

  const roleKey = ROLE_KEYS[cs?.role] || "Any";
  const source = kind === "pick" ? lists.picks : lists.bans;
  const quick = [...new Set([...(source[roleKey] || []), ...(source.Any || [])])];
  $("quickTitle").textContent = kind === "pick" ? `Your ${cs?.role || ""} picks` : "Your bans";
  $("quickWrap").hidden = quick.length === 0;
  fillGrid($("quick"), quick);
  filterAll();
  $("sheet").hidden = false;
}

function closeSheet() {
  $("sheet").hidden = true;
  $("loadoutSheet").hidden = true;
  sheetKind = null;
  loadoutMode = null;
}

function filterAll() {
  const query = $("search").value.trim().toLowerCase();
  const ids = [...champions.values()]
    .filter((c) => !query || c.name.toLowerCase().includes(query))
    .map((c) => c.id);
  fillGrid($("all"), ids);
}

function fillGrid(grid, ids) {
  const cs = state?.champSelect;
  const current = sheetKind === "pick" ? cs?.plannedPick?.championId : cs?.plannedBan?.championId;
  grid.replaceChildren(...ids.filter((id) => champions.has(id)).map((id) => {
    const button = el("button");
    button.type = "button";
    button.dataset.champion = id;
    if (sheetKind === "pick" && champions.get(id).playable === false) button.classList.add("unavailable");
    if (id === current) button.classList.add("selected");
    button.append(icon(id), el("span", null, nameOf(id)));
    return button;
  }));
}

$("search").addEventListener("input", filterAll);

$("sheet").addEventListener("click", (event) => {
  const button = event.target.closest("[data-champion]");
  if (!button || !sheetKind) return;
  send({ type: "override", kind: sheetKind, championId: Number(button.dataset.champion) });
  closeSheet();
});

$("clearOverride").addEventListener("click", () => {
  send({ type: "override", kind: sheetKind, championId: null });
  closeSheet();
});

document.addEventListener("keydown", (event) => {
  if (event.key === "Escape") closeSheet();
});

// ── Toast ────────────────────────────────────────────────────────────

let toastTimer = 0;
function toast(text, tone) {
  const t = $("toast");
  t.textContent = text;
  t.className = "toast " + (tone || "");
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (t.hidden = true), 2500);
}

start();
