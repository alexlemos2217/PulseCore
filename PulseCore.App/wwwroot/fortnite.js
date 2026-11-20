(() => {
  "use strict";

  const host = window.chrome && window.chrome.webview;
  if (!host) return;

  const $ = (id) => document.getElementById(id);
  const PC = window.PulseCore || {};
  const { toast, fmtMb, countUp } = PC;
  const log = (msg) => PC.addLog && PC.addLog("FORTNITE", msg);
  const logErr = (msg) => PC.addLog && PC.addLog("ERR", msg);
  const send = (type, payload) => host.postMessage({ type, payload });

  const fn = { status: null, fps: 240, readOnly: false, init: false, busy: false, lastDemos: 0, lastLogs: 0 };

  const QUALITY_NAMES = ["Bajo", "Medio", "Alto", "Épico"];
  const q = (v) => (v == null ? "N/D" : `${QUALITY_NAMES[v] || v} (${v})`);

  function setChip(id, text, tone = "") {
    const el = $(id);
    el.textContent = text;
    el.className = `fn-chip-val mono ${tone}`.trim();
  }

  function matches(s, preset) {
    if (!s) return false;
    const want = preset === "competitive"
      ? { v: false, b: false, g: false, sh: 0, fx: 0, pp: 0, vd: 2, aa: 0 }
      : { v: false, b: false, g: true, sh: 2, fx: 2, pp: 2, vd: 3, aa: 2 };
    const eq = (a, b) => a == null ? false : a === b;
    return eq(s.useVSync, want.v) && eq(s.motionBlur, want.b) && eq(s.showGrass, want.g) &&
      eq(s.shadowQuality, want.sh) && eq(s.effectsQuality, want.fx) &&
      eq(s.postProcessQuality, want.pp) && eq(s.viewDistanceQuality, want.vd) &&
      (s.antiAliasingQuality == null || s.antiAliasingQuality === want.aa);
  }

  function renderStatus(st) {
    if (!st) return;
    fn.status = st;
    const s = st.settings;

    const badge = $("fnBadge");
    badge.classList.remove("off", "waiting", "playing");
    if (st.isDetected) { badge.classList.add(st.gameRunning ? "playing" : "waiting"); $("fnBadgeText").textContent = st.gameRunning ? "FORTNITE ABIERTO" : "DETECTADO"; }
    else { badge.classList.add("off"); $("fnBadgeText").textContent = "NO DETECTADO"; }

    $("fnWarn").hidden = !(st.isDetected && st.gameRunning);

    setChip("fnChipIni", st.isDetected ? "Detectado ✓" : "No encontrado", st.isDetected ? "ok" : "bad");
    setChip("fnChipBackup", st.hasBackup
      ? `Guardado${st.backupCreatedAt ? " · " + new Date(st.backupCreatedAt).toLocaleDateString("es-AR") : ""}`
      : "Aún no", st.hasBackup ? "ok" : "warn");
    const fps = s && s.frameRateLimit != null ? (s.frameRateLimit === 0 ? "Ilimitado" : `${Math.round(s.frameRateLimit)}`) : "N/D";
    setChip("fnChipFps", fps);
    setChip("fnChipVsync", s && s.useVSync != null ? (s.useVSync ? "Activado" : "Apagado") : "N/D", s && s.useVSync ? "warn" : "");
    setChip("fnChipShadow", s ? q(s.shadowQuality) : "N/D");
    setChip("fnChipRo", st.isDetected ? (st.isReadOnly ? "Sí" : "No") : "N/D", st.isReadOnly ? "warn" : "");

    $("fnPath").textContent = st.message || st.configPath || "—";

    // Controles
    $("fnControls").classList.toggle("disabled", !st.isDetected);
    document.querySelectorAll("[data-preset]").forEach((b) => { b.disabled = !st.isDetected || fn.busy; });
    $("btnFnRestore").disabled = !st.hasBackup || !st.isDetected || fn.busy;

    if (!fn.init && st.isDetected) {
      fn.init = true;
      if (s && s.frameRateLimit != null) {
        const v = Math.round(s.frameRateLimit);
        if ([144, 165, 200, 240, 0].includes(v)) setFps(v);
      }
      $("fnReadOnly").checked = !!st.isReadOnly;
      fn.readOnly = !!st.isReadOnly;
    }

    // Preset activo
    document.querySelectorAll(".fn-preset").forEach((card) => {
      const on = st.isDetected && matches(s, card.dataset.accent);
      card.classList.toggle("active", on);
      card.querySelector(".fn-active-tag").hidden = !on;
    });

    // Limpiador
    const setRow = (sizeId, filesId, boxId, stats, key) => {
      const sizeEl = $(sizeId);
      const prev = fn[key];
      if (typeof countUp === "function" && Math.abs(prev - stats.sizeMb) > 0.01) countUp(sizeEl, prev, stats.sizeMb, 700, fmtMb);
      else sizeEl.textContent = fmtMb(stats.sizeMb);
      fn[key] = stats.sizeMb;
      $(filesId).textContent = stats.exists ? `${stats.files.toLocaleString("es-AR")} archivos` : "Carpeta no encontrada";
      const box = $(boxId);
      box.disabled = stats.files === 0;
      if (stats.files === 0) box.checked = false;
    };
    setRow("fnDemosSize", "fnDemosFiles", "fnCleanDemos", st.demos, "lastDemos");
    setRow("fnLogsSize", "fnLogsFiles", "fnCleanLogs", st.logs, "lastLogs");
    $("btnFnClean").disabled = !st.isDetected && !st.demos.exists && !st.logs.exists || fn.busy;
  }

  function setFps(v) {
    fn.fps = v;
    document.querySelectorAll("#fnFps .mode-btn").forEach((b) => b.classList.toggle("active", Number(b.dataset.fps) === v));
  }

  function setBusy(on, btn, labelEl, busyText, idleText) {
    fn.busy = on;
    document.querySelectorAll("#view-fortnite .action-btn").forEach((b) => { b.disabled = on; });
    if (btn) btn.classList.toggle("loading", on);
    if (labelEl) labelEl.textContent = on ? busyText : idleText;
    if (!on && fn.status) renderStatus(fn.status);
  }

  let activeBtn = null, activeLabel = null, activeIdle = "";
  function begin(btn, labelEl, busyText) {
    activeBtn = btn; activeLabel = labelEl; activeIdle = labelEl.textContent;
    setBusy(true, btn, labelEl, busyText, activeIdle);
  }
  function end() {
    if (activeBtn) setBusy(false, activeBtn, activeLabel, "", activeIdle);
    else setBusy(false);
    activeBtn = null;
  }

  // ---------- Eventos UI ----------
  $("fnFps").addEventListener("click", (e) => {
    const b = e.target.closest(".mode-btn");
    if (b) setFps(Number(b.dataset.fps));
  });
  $("fnReadOnly").addEventListener("change", (e) => { fn.readOnly = e.target.checked; });

  document.querySelectorAll("[data-preset]").forEach((btn) =>
    btn.addEventListener("click", () => {
      if (fn.busy) return;
      begin(btn, btn.querySelector(".fn-btn-label"), "Aplicando…");
      log(`Aplicando preset ${btn.dataset.preset} (${fn.fps === 0 ? "FPS ilimitados" : fn.fps + " FPS"}, solo lectura: ${fn.readOnly ? "sí" : "no"})…`);
      send("apply_fortnite_preset", { preset: btn.dataset.preset, fpsLimit: fn.fps, setReadOnly: fn.readOnly });
    }));

  $("btnFnRestore").addEventListener("click", () => {
    if (fn.busy) return;
    begin($("btnFnRestore"), $("btnFnRestoreLabel"), "Restaurando…");
    log("Restaurando backup original de GameUserSettings.ini…");
    send("restore_fortnite_backup");
  });

  $("btnFnClean").addEventListener("click", () => {
    if (fn.busy) return;
    const demos = $("fnCleanDemos").checked && !$("fnCleanDemos").disabled;
    const logs = $("fnCleanLogs").checked && !$("fnCleanLogs").disabled;
    if (!demos && !logs) {
      toast && toast({ title: "Nada seleccionado", parts: ["Elegí Replays y/o Logs (con archivos)"], tone: "info" });
      return;
    }
    begin($("btnFnClean"), $("btnFnCleanLabel"), "Limpiando…");
    log(`Limpieza iniciada: ${[demos ? "Replays" : null, logs ? "Crash Logs y Logs" : null].filter(Boolean).join(" + ")}`);
    send("clean_fortnite_files", { cleanDemos: demos, cleanLogs: logs });
  });

  // ---------- Respuestas ----------
  function onPresetResult(r) {
    end();
    renderStatus(r.status);
    const name = r.preset === "competitive" ? "Competitivo FPS" : "Calidad Visual";
    if (r.success) {
      toast({
        title: `Preset ${name} aplicado`,
        parts: [r.fpsLimit === 0 ? "FPS ilimitados" : `Límite ${r.fpsLimit} FPS`, r.readOnly ? "Solo lectura" : "Editable",
          r.backupCreated ? "Backup original creado" : "Backup ya existente"],
        ms: 7500,
      });
      log(r.message);
      if (r.backupCreated) log("Backup original guardado en %APPDATA%\\PulseCore\\Backups\\GameUserSettings.original.ini");
      if (r.gameRunning) toast({ title: "Fortnite está abierto", parts: ["Reinicialo para que tome los cambios"], tone: "info" });
    } else {
      toast({ title: "No se aplicó el preset", parts: [r.message], tone: "err" });
      log(`Error: ${r.message}`);
    }
  }

  function onRestoreResult(r) {
    end();
    renderStatus(r.status);
    if (r.success) {
      fn.init = false; renderStatus(r.status);
      toast({ title: "Backup restaurado", parts: ["GameUserSettings.ini volvió a su estado original"], ms: 7000 });
      log(r.message);
    } else {
      toast({ title: "No se pudo restaurar", parts: [r.message], tone: "err" });
      log(`Error: ${r.message}`);
    }
  }

  function onCleanResult(r) {
    end();
    renderStatus(r.status);
    if (r.success) {
      toast({ title: "Fortnite limpio", parts: [`${fmtMb(r.freedMb)} liberados`, `${r.filesDeleted.toLocaleString("es-AR")} archivos`], ms: 7000 });
      log(`${r.message} (${r.durationMs} ms)`);
    } else {
      toast({ title: "Limpieza no realizada", parts: [r.message], tone: "info" });
      log(r.message);
    }
  }

  const OWN = ["get_fortnite_status", "apply_fortnite_preset", "restore_fortnite_backup", "clean_fortnite_files"];
  function onActionError(e) {
    if (!OWN.includes(e.action)) return;
    end();
    logErr(`${e.action}: ${e.message}`);
    toast({ title: "Acción no completada", parts: [e.message], tone: "err" });
  }

  const routes = {
    fortnite_status: renderStatus,
    fortnite_preset_result: onPresetResult,
    fortnite_restore_result: onRestoreResult,
    fortnite_clean_result: onCleanResult,
    action_error: onActionError,
  };

  host.addEventListener("message", (e) => {
    const m = e.data;
    const f = m && routes[m.type];
    if (!f) return;
    try { f(m.payload || {}); }
    catch (err) { logErr(`Error UI (${m.type}): ${err && err.message || err}`); }
  });

  document.querySelectorAll('[data-view="fortnite"]').forEach((b) =>
    b.addEventListener("click", () => send("get_fortnite_status")));

  send("get_fortnite_status");
})();
