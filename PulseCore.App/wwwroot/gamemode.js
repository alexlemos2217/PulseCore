(() => {
  "use strict";

  const host = window.chrome && window.chrome.webview;
  if (!host) return;

  const $ = (id) => document.getElementById(id);
  const PC = window.PulseCore || {};
  const { toast, fmtMb, countUp, pulseFrame, sleep, esc } = PC;
  const log = (lvl, msg) => PC.addLog && PC.addLog(lvl, msg);
  const send = (type, payload) => host.postMessage({ type, payload });

  const gm = { isEnabled: false, games: [], active: null, timer: null, pingStart: 0, pinging: false, exporting: false };

  const fmtDur = (sec) => {
    sec = Math.max(0, Math.floor(sec));
    const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = sec % 60;
    if (h > 0) return `${h}h ${m}m ${s}s`;
    if (m > 0) return `${m}m ${s}s`;
    return `${s}s`;
  };

  // ==================== MODO JUEGO ====================
  function renderGameMode(st) {
    if (!st) return;
    gm.isEnabled = st.isEnabled;
    gm.games = st.games || [];
    gm.active = st.active || null;

    $("gmToggle").checked = gm.isEnabled;

    const badge = $("gmBadge");
    badge.classList.remove("off", "waiting", "playing");
    if (gm.active) {
      badge.classList.add("playing");
      $("gmBadgeText").textContent = `EN PARTIDA · ${gm.active.name}`;
    } else if (gm.isEnabled) {
      badge.classList.add("waiting");
      $("gmBadgeText").textContent = "ESPERANDO JUEGO";
    } else {
      badge.classList.add("off");
      $("gmBadgeText").textContent = "VIGILANCIA APAGADA";
    }

    // Tarjeta de sesión
    const card = $("gmSession");
    if (gm.active) {
      card.hidden = false;
      $("gmSessionGame").textContent = gm.active.name;
      $("gmSessionMeta").textContent =
        `${gm.active.process} · PID ${gm.active.pid} · Plan: ${gm.active.planName || "—"}` +
        (gm.active.planChanged && gm.active.previousPlanName ? ` (antes: ${gm.active.previousPlanName})` : "");
      tickTimer();
      if (!gm.timer) gm.timer = setInterval(tickTimer, 1000);
    } else {
      card.hidden = true;
      clearInterval(gm.timer);
      gm.timer = null;
    }

    // Lista de juegos
    $("gmCount").textContent = `${gm.games.length} ${gm.games.length === 1 ? "juego" : "juegos"}`;
    const list = $("gmGames");
    if (!gm.games.length) {
      list.innerHTML = '<div class="empty-inline mono">No hay juegos configurados. Agregá uno abajo.</div>';
      return;
    }
    list.innerHTML = gm.games.map((g) => {
      const live = gm.active && gm.active.process.toLowerCase() === g.process.toLowerCase();
      return `<div class="game-card ${live ? "live" : ""}">` +
        `<div class="gc-avatar">${esc((g.name[0] || "?").toUpperCase())}</div>` +
        `<div class="gc-info"><div class="gc-name">${esc(g.name)}</div><div class="gc-proc mono">${esc(g.process)}</div></div>` +
        (live ? `<span class="gc-live mono">EN PARTIDA</span>` : "") +
        `<button class="gc-del" data-proc="${esc(g.process)}" title="Eliminar de la lista" aria-label="Eliminar ${esc(g.name)}">✕</button>` +
      `</div>`;
    }).join("");
  }

  function tickTimer() {
    if (!gm.active) return;
    $("gmSessionTimer").textContent = fmtDur((Date.now() - Date.parse(gm.active.startedAt)) / 1000);
  }

  function onGameModeState(r) {
    renderGameMode(r.state);
    if (r.message) {
      if (r.success) log("GAME", r.message);
      else {
        log("WARN", r.message);
        toast({ title: "No se pudo completar", parts: [r.message], tone: "err" });
      }
    }
  }

  $("gmToggle").addEventListener("change", (e) => {
    send("toggle_game_mode", { enabled: e.target.checked });
  });

  $("gmAddForm").addEventListener("submit", (e) => {
    e.preventDefault();
    const name = $("gmName").value.trim();
    const proc = $("gmProc").value.trim();
    if (!name || !proc) {
      toast({ title: "Faltan datos", parts: ["Completá el nombre y el ejecutable"], tone: "info" });
      return;
    }
    send("add_game_profile", { name, process: proc });
    $("gmName").value = "";
    $("gmProc").value = "";
  });

  $("gmGames").addEventListener("click", (e) => {
    const btn = e.target.closest(".gc-del");
    if (btn) send("remove_game_profile", { process: btn.dataset.proc });
  });

  function onGameEvent(p) {
    renderGameMode(p.state);
    pulseFrame && pulseFrame("ok");
    const parts = [
      p.planChanged ? `${p.planName} activo` : (p.planMessage || "Plan sin cambios"),
      p.priorityRaised ? "Prioridad Alta" : "Prioridad sin cambios",
      (p.ramFreedMb || 0) > 0 ? `+${fmtMb(p.ramFreedMb)} RAM` : "RAM optimizada",
    ];
    toast({ title: `Modo Juego activado · ${p.game}`, parts, ms: 8000 });
    log("GAME", `Juego detectado: ${p.game} (${p.process}, PID ${p.pid}). Sesión iniciada.`);
    log("POWER", p.planMessage);
    log("GAME", p.priorityMessage);
    log("BOOST", `RAM liberada al iniciar partida: ${fmtMb(p.ramFreedMb || 0)} · ${p.ramProcessesTrimmed || 0} procesos`);
  }

  function fmtDurShort(sec) {
    sec = Math.max(0, Math.floor(sec || 0));
    const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = sec % 60;
    if (h > 0) return `${h}h ${m}m`;
    if (m > 0) return `${m}m ${s}s`;
    return `${s}s`;
  }

  function renderSessions(list) {
    const arr = Array.isArray(list) ? list : [];
    const countEl = $("gmSessionsCount");
    const box = $("gmSessionsList");
    if (countEl) countEl.textContent = `${arr.length} ${arr.length === 1 ? "sesión" : "sesiones"}`;
    if (!box) return;
    if (!arr.length) {
      box.innerHTML = '<div class="empty-inline mono">Todavía no hay sesiones registradas. Activá Modo Juego y abrí un juego de tu lista.</div>';
      return;
    }
    const rows = arr.map((s) => {
      const dt = s.endedAt ? new Date(s.endedAt).toLocaleString("es-AR", { day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit", hour12: false }) : "—";
      const gpu = s.maxGpuTemp != null ? `${Math.round(s.maxGpuTemp)} °C` : "N/D";
      const gpuTone = s.maxGpuTemp == null ? "" : s.maxGpuTemp >= 80 ? "hot" : s.maxGpuTemp >= 70 ? "warm" : "cool";
      return `<div class="sess-row">` +
        `<div class="sess-game"><span class="sess-name">${esc(s.gameName)}</span><span class="sess-proc mono">${esc(s.processName)}</span></div>` +
        `<span class="sess-date mono">${esc(dt)}</span>` +
        `<span class="sess-dur mono">${fmtDurShort(s.durationSeconds)}</span>` +
        `<span class="sess-gpu mono ${gpuTone}">${gpu}</span>` +
        `<span class="sess-cpu mono">${Math.round(s.maxCpuLoad || 0)}%</span>` +
        `<span class="sess-ram mono">${Number(s.maxRamGb || 0).toFixed(1)} GB</span>` +
      `</div>`;
    }).join("");

    box.innerHTML =
      `<div class="sess-head mono">` +
        `<span>Juego</span><span>Fecha</span><span>Duración</span><span>GPU Máx</span><span>CPU Pico</span><span>RAM Pico</span>` +
      `</div>` + rows;
  }

  function openPostGameModal(p) {
    const modal = $("postGameModal");
    if (!modal) return;
    const dur = fmtDurShort(p.durationSeconds);
    const gpu = p.maxGpuTemp != null ? `${Math.round(p.maxGpuTemp)} °C` : "N/D";
    $("pgTitle").textContent = p.game || "Partida finalizada";
    $("pgSub").textContent = `${p.process || ""} · ${p.reason || "Sesión cerrada"}`;
    $("pgDur").textContent = dur;
    $("pgGpu").textContent = gpu;
    $("pgCpu").textContent = `${Math.round(p.maxCpuLoad || 0)}%`;
    $("pgRam").textContent = `${Number(p.maxRamGb || 0).toFixed(1)} GB`;
    $("pgPlan").textContent = p.planReverted
      ? `Plan restaurado: ${p.restoredPlanName}`
      : (p.planMessage || "Plan de energía sin cambios");
    modal.hidden = false;
  }

  function closePostGameModal() {
    const modal = $("postGameModal");
    if (modal) modal.hidden = true;
  }

  $("btnClosePostGame")?.addEventListener("click", closePostGameModal);
  $("btnOkPostGame")?.addEventListener("click", closePostGameModal);
  $("postGameModal")?.addEventListener("click", (e) => {
    if (e.target === $("postGameModal")) closePostGameModal();
  });
  $("btnClearSessions")?.addEventListener("click", () => send("clear_game_sessions"));

  function onGameSessions(r) {
    renderSessions(r.sessions);
    if (r.cleared) {
      log("GAME", "Historial de sesiones limpiado.");
      toast({ title: "Historial limpiado", parts: ["Se vació sessions.json"], tone: "info" });
    }
  }

  function onSessionEnded(p) {
    renderGameMode(p.state);
    if (p.sessions) renderSessions(p.sessions);
    const dur = fmtDur(p.durationSeconds);
    const gpuPart = p.maxGpuTemp != null ? `GPU máx ${Math.round(p.maxGpuTemp)}°C` : null;
    toast({
      title: `Sesión finalizada · ${p.game}`,
      parts: [`Duración ${dur}`, gpuPart, p.planReverted ? `Plan restaurado: ${p.restoredPlanName}` : null].filter(Boolean),
      tone: "info", ms: 8000,
    });
    log("GAME", `Sesión finalizada: ${p.game} · duración ${dur} · GPU máx: ${p.maxGpuTemp != null ? Math.round(p.maxGpuTemp) + "°C" : "N/D"} · CPU pico: ${Math.round(p.maxCpuLoad || 0)}% · RAM pico: ${Number(p.maxRamGb || 0).toFixed(1)} GB`);
    log("POWER", p.planMessage);
    openPostGameModal(p);
  }

  // ==================== PING & JITTER ====================
  const SERVERS = [
    { id: "cloudflare", label: "Cloudflare DNS", host: "1.1.1.1" },
    { id: "google", label: "Google DNS", host: "8.8.8.8" },
    { id: "aws_sa", label: "AWS Sudamérica", host: "s3.sa-east-1.amazonaws.com" },
  ];

  function buildPingGrid() {
    $("pingGrid").innerHTML = SERVERS.map((s) =>
      `<div class="ping-card" data-id="${s.id}" data-tone="idle">` +
        `<div class="pc-top"><span class="pc-dot"></span><span class="pc-label">${esc(s.label)}</span><span class="pc-method mono"></span></div>` +
        `<div class="pc-host mono">${esc(s.host)}</div>` +
        `<div class="pc-ms mono">—</div>` +
        `<div class="pc-bars"></div>` +
        `<div class="pc-stats mono"><span>Mín —</span><span>Máx —</span><span>Jitter —</span><span>Pérdida —</span></div>` +
        `<div class="ping-sweep" aria-hidden="true"></div>` +
      `</div>`).join("");
  }
  buildPingGrid();

  const toneOf = (ms) => (ms == null ? "bad" : ms < 45 ? "good" : ms <= 90 ? "warn" : "bad");

  $("btnPing").addEventListener("click", () => {
    if (gm.pinging) return;
    gm.pinging = true;
    gm.pingStart = performance.now();
    $("btnPing").disabled = true;
    $("btnPing").classList.add("loading");
    $("btnPingLabel").textContent = "Midiendo…";
    $("pingGrid").classList.add("scanning");
    log("INFO", "Medición de latencia iniciada (4 muestras por servidor).");
    send("run_ping_test");
  });

  function resetPingUi() {
    gm.pinging = false;
    $("btnPing").disabled = false;
    $("btnPing").classList.remove("loading");
    $("btnPingLabel").textContent = "⚡ Medir Latencia";
    $("pingGrid").classList.remove("scanning");
  }

  async function onPingResult(r) {
    const wait = Math.max(0, 1200 - (performance.now() - gm.pingStart));
    if (wait) await sleep(wait);
    resetPingUi();

    (r.targets || []).forEach((t) => {
      const el = document.querySelector(`.ping-card[data-id="${t.id}"]`);
      if (!el) return;
      const ok = t.status === "ok";
      el.dataset.tone = toneOf(ok ? t.avgMs : null);
      el.querySelector(".pc-method").textContent = ok ? t.method : "";
      const msEl = el.querySelector(".pc-ms");
      if (ok) countUp(msEl, 0, t.avgMs, 700, (v) => `${v.toFixed(0)} ms`);
      else msEl.textContent = "Sin respuesta";

      const bars = el.querySelector(".pc-bars");
      const samples = t.samplesMs || [];
      const maxS = Math.max(120, ...samples);
      bars.innerHTML = samples.map(() => '<span class="pc-bar" style="height:3px"></span>').join("");
      requestAnimationFrame(() => requestAnimationFrame(() => {
        [...bars.children].forEach((b, i) => { b.style.height = `${Math.max(12, (samples[i] / maxS) * 100)}%`; });
      }));

      const f = (v, u = " ms") => (v == null ? "N/D" : `${v}${u}`);
      el.querySelector(".pc-stats").innerHTML =
        `<span>Mín ${f(t.minMs)}</span><span>Máx ${f(t.maxMs)}</span>` +
        `<span>Jitter ${f(t.jitterMs)}</span><span>Pérdida ${t.lossPercent}%</span>`;

      log("INFO", ok
        ? `Ping ${t.label}: ${t.avgMs} ms (mín ${t.minMs} / máx ${t.maxMs}) · jitter ${f(t.jitterMs)} · pérdida ${t.lossPercent}% · ${t.method}`
        : `Ping ${t.label}: sin respuesta (ICMP y TCP 443 fallaron) · pérdida ${t.lossPercent}%`);
    });

    const okTargets = (r.targets || []).filter((t) => t.status === "ok");
    toast({
      title: "Medición de latencia completada",
      parts: okTargets.length
        ? okTargets.map((t) => `${t.label.split(" ")[0]} ${Math.round(t.avgMs)} ms`)
        : ["Sin respuesta de los servidores"],
      tone: okTargets.length ? "info" : "err",
    });
  }

  // ==================== EXPORTAR DIAGNÓSTICO ====================
  $("btnExportLogs").addEventListener("click", () => {
    if (gm.exporting) return;
    gm.exporting = true;
    $("btnExportLogs").disabled = true;
    $("btnExportLogs").classList.add("loading");
    $("btnExportLabel").textContent = "Exportando…";
    send("export_logs", { logs: PC.getLogs ? PC.getLogs() : [] });
  });

  function resetExportUi() {
    gm.exporting = false;
    $("btnExportLogs").disabled = false;
    $("btnExportLogs").classList.remove("loading");
    $("btnExportLabel").textContent = "Exportar Diagnóstico (.json)";
  }

  function onExportResult(r) {
    resetExportUi();
    toast({ title: "Diagnóstico exportado", parts: [r.fileName, "Guardado en el Escritorio", `${r.logCount} líneas de log`], ms: 8000 });
    log("INFO", `Diagnóstico exportado: ${r.path}`);
  }

  // ==================== ERRORES ====================
  const OWN = ["get_game_mode_state", "toggle_game_mode", "add_game_profile", "remove_game_profile", "get_game_sessions", "clear_game_sessions", "run_ping_test", "export_logs"];
  function onActionError(e) {
    if (!OWN.includes(e.action)) return;
    if (e.action === "run_ping_test") resetPingUi();
    if (e.action === "export_logs") resetExportUi();
    if (e.action === "toggle_game_mode") send("get_game_mode_state");
    log("ERR", `${e.action}: ${e.message}`);
    toast({ title: "Acción no completada", parts: [e.message], tone: "err" });
  }

  // ==================== ROUTER ====================
  const routes = {
    game_mode_state: onGameModeState,
    game_mode_event: onGameEvent,
    game_session_ended: onSessionEnded,
    game_sessions: onGameSessions,
    ping_result: onPingResult,
    export_result: onExportResult,
    action_error: onActionError,
  };

  host.addEventListener("message", (e) => {
    const m = e.data;
    const fn = m && routes[m.type];
    if (!fn) return;
    try {
      const r = fn(m.payload || {});
      if (r && typeof r.catch === "function") r.catch((err) => log("ERR", `Error UI (${m.type}): ${err && err.message || err}`));
    } catch (err) {
      log("ERR", `Error UI (${m.type}): ${err && err.message || err}`);
    }
  });

  document.querySelectorAll('[data-view="gamemode"], [data-jump="gamemode"]').forEach((b) =>
    b.addEventListener("click", () => {
      send("get_game_mode_state");
      send("get_game_sessions");
    }));

  send("get_game_mode_state");
  send("get_game_sessions");
})();
