(() => {
  "use strict";

  const host = window.chrome && window.chrome.webview;
  if (!host) return;

  const $ = (id) => document.getElementById(id);
  const PC = window.PulseCore || {};
  const { toast, esc, pulseFrame } = PC;
  const log = (lvl, msg) => PC.addLog && PC.addLog(lvl, msg);
  const send = (type, payload) => host.postMessage({ type, payload });

  let busy = false;

  function renderReport(report) {
    if (!report) return;
    const score = Math.max(0, Math.min(100, Math.round(report.score || 0)));
    const scoreEl = $("ckScore");
    const bar = $("ckBar");
    const badge = $("ckBadge");

    scoreEl.textContent = score;
    bar.style.width = `${score}%`;
    badge.classList.remove("warn", "hot");

    if (score >= 80) {
      badge.textContent = score === 100 ? "Sistema 100% Óptimo" : "Muy buen estado";
      scoreEl.style.color = "var(--emerald)";
      bar.style.background = "linear-gradient(90deg, var(--cyan), var(--emerald))";
    } else if (score >= 50) {
      badge.textContent = "Optimizable";
      badge.classList.add("warn");
      scoreEl.style.color = "var(--amber)";
      bar.style.background = "linear-gradient(90deg, var(--amber), #fbbf24)";
    } else {
      badge.textContent = "Requiere atención";
      badge.classList.add("hot");
      scoreEl.style.color = "var(--red)";
      bar.style.background = "linear-gradient(90deg, var(--amber), var(--red))";
    }

    const items = report.items || [];
    const optCount = items.filter((i) => i.status === "Optimal").length;
    const ts = report.timestamp ? new Date(report.timestamp).toLocaleTimeString("es-AR", { hour12: false }) : "";
    $("ckSummary").textContent = `${optCount} de ${items.length} ajustes en estado óptimo · última auditoría ${ts}`;

    const list = $("checkupList");
    list.innerHTML = items.map((it) => {
      const ok = it.status === "Optimal";
      return `<article class="card glow-card ck-item ${ok ? "ok" : "warn"}">` +
        `<div class="ck-item-main">` +
          `<div class="ck-item-head">` +
            `<span class="ck-pill ${ok ? "ok" : "warn"} mono">${ok ? "ÓPTIMO" : "MEJORABLE"}</span>` +
            `<h3 class="ck-item-title">${esc(it.title)}</h3>` +
          `</div>` +
          `<p class="ck-item-desc">${esc(it.description)}</p>` +
          `<div class="ck-item-val mono">Actual: ${esc(it.currentValue)}</div>` +
        `</div>` +
        `<div class="ck-item-act">` +
          (it.canAutoFix
            ? `<button class="action-btn primary ck-fix" data-fix="${esc(it.id)}">Corregir</button>`
            : `<span class="ck-diag mono">${ok ? "✓ Listo" : "Diagnóstico"}</span>`) +
        `</div>` +
      `</article>`;
    }).join("");
  }

  function prettyValue(itemId, raw) {
    if (!raw) return "—";
    if (itemId && itemId.startsWith("debloat:")) return raw.startsWith("{") ? "Estado previo" : raw;
    if (itemId === "power_plan") return raw.includes("|") ? raw.split("|")[1] : raw;
    if (itemId === "mouse_accel") return raw.startsWith("0") ? "Desactivada (0)" : `Activada (${raw.split("|")[0]})`;
    if (itemId === "windows_game_mode") return raw === "1" ? "Activado (1)" : "Desactivado (0)";
    return raw;
  }

  function renderHistory(history) {
    const arr = Array.isArray(history) ? history : [];
    $("ckHistCount").textContent = `${arr.length} ${arr.length === 1 ? "cambio" : "cambios"}`;
    $("btnRevertAll").disabled = arr.length === 0 || busy;

    const box = $("restoreList");
    if (!arr.length) {
      box.innerHTML = '<div class="empty-inline mono">Todavía no hay cambios registrados para revertir.</div>';
      return;
    }

    box.innerHTML = arr.map((h) => {
      const dt = h.appliedAt ? new Date(h.appliedAt).toLocaleString("es-AR", { day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit", hour12: false }) : "";
      const prev = prettyValue(h.itemId, h.previousValue);
      const next = prettyValue(h.itemId, h.newValue);
      return `<div class="ck-hist-row">` +
        `<div class="ck-hist-info">` +
          `<div class="ck-hist-name">${esc(h.settingName)}</div>` +
          `<div class="ck-hist-diff mono">${esc(prev)} → ${esc(next)} · ${esc(dt)}</div>` +
        `</div>` +
        `<button class="action-btn small ck-undo" data-cid="${esc(h.changeId)}">Deshacer</button>` +
      `</div>`;
    }).join("");
  }

  function setRpBusy(on) {
    busy = on;
    const btn = $("btnCreateRestorePoint");
    btn.disabled = on;
    btn.classList.toggle("loading", on);
    $("btnRpLabel").textContent = on ? "Creando punto…" : "🛡️ Crear Punto de Restauración";
  }

  function setScanBusy(on) {
    const btn = $("btnRescanCheckup");
    btn.disabled = on;
    btn.classList.toggle("loading", on);
    $("btnRescanLabel").textContent = on ? "Escaneando…" : "Volver a escanear";
  }

  // ---------- Eventos UI ----------
  $("btnRescanCheckup")?.addEventListener("click", () => {
    setScanBusy(true);
    send("run_system_checkup");
  });

  $("btnCreateRestorePoint")?.addEventListener("click", () => {
    if (busy) return;
    setRpBusy(true);
    log("CHECKUP", "Creando punto de restauración de Windows (“PulseCore Backup”)…");
    send("create_restore_point");
  });

  $("checkupList")?.addEventListener("click", (e) => {
    const btn = e.target.closest(".ck-fix");
    if (!btn || btn.disabled) return;
    btn.disabled = true;
    btn.textContent = "Aplicando…";
    send("fix_checkup_item", { id: btn.dataset.fix });
  });

  $("restoreList")?.addEventListener("click", (e) => {
    const btn = e.target.closest(".ck-undo");
    if (!btn || btn.disabled) return;
    btn.disabled = true;
    btn.textContent = "Revirtiendo…";
    send("revert_change", { changeId: btn.dataset.cid });
  });

  $("btnRevertAll")?.addEventListener("click", () => {
    $("btnRevertAll").disabled = true;
    send("revert_all_changes");
  });

  // ---------- Respuestas C# ----------
  function onReport(p) {
    setScanBusy(false);
    renderReport(p.report);
    if (p.history) renderHistory(p.history);
  }

  function onActionResult(r) {
    if (r.report) renderReport(r.report);
    if (r.history) renderHistory(r.history);
    if (r.success) {
      pulseFrame && pulseFrame("ok");
      toast({ title: "Check-up actualizado", parts: [r.message], ms: 6500 });
      log("CHECKUP", r.message);
    } else {
      toast({ title: "No se completó el ajuste", parts: [r.message], tone: "err" });
      log("CHECKUP", `Aviso: ${r.message}`);
    }
    send("get_optimizer_state");
  }

  function onRestorePoint(r) {
    setRpBusy(false);
    if (r.success) {
      pulseFrame && pulseFrame("ok");
      toast({ title: "Punto de restauración creado", parts: [r.message], ms: 7500 });
      log("CHECKUP", `${r.message} (${r.durationMs} ms)`);
    } else {
      toast({ title: "Punto de restauración no creado", parts: [r.message], tone: "info", ms: 8500 });
      log("WARN", r.message);
    }
  }

  const OWN = ["run_system_checkup", "fix_checkup_item", "create_restore_point", "get_restore_history", "revert_change", "revert_all_changes"];
  const routes = {
    checkup_report: onReport,
    checkup_action_result: onActionResult,
    restore_point_result: onRestorePoint,
    restore_history: (p) => renderHistory(p.history),
    action_error: (e) => {
      if (!OWN.includes(e.action)) return;
      setScanBusy(false);
      setRpBusy(false);
      log("ERR", `${e.action}: ${e.message}`);
      toast({ title: "Error en Check-up", parts: [e.message], tone: "err" });
    },
  };

  host.addEventListener("message", (e) => {
    const m = e.data;
    const fn = m && routes[m.type];
    if (!fn) return;
    try { fn(m.payload || {}); }
    catch (err) { log("ERR", `Error UI (${m.type}): ${err && err.message || err}`); }
  });

  document.querySelectorAll('[data-view="checkup"]').forEach((b) =>
    b.addEventListener("click", () => send("run_system_checkup")));

  send("run_system_checkup");
})();
