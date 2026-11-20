(() => {
  "use strict";

  const host = window.chrome && window.chrome.webview;
  if (!host) return;

  const $ = (id) => document.getElementById(id);
  const PC = window.PulseCore || {};
  const log = (lvl, msg) => PC.addLog && PC.addLog(lvl, msg);
  const send = (type, payload) => host.postMessage({ type, payload });
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const esc = (s) => String(s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  const fmtMb = (mb) => {
    const v = Number(mb) || 0;
    if (v >= 1024) return `${(v / 1024).toFixed(2)} GB`;
    return `${v >= 100 ? v.toFixed(0) : v.toFixed(1)} MB`;
  };

  function countUp(el, from, to, dur, fmt = fmtMb) {
    const start = performance.now();
    const tick = (now) => {
      const t = Math.min(1, (now - start) / dur);
      const e = 1 - Math.pow(1 - t, 3);
      el.textContent = fmt(from + (to - from) * e);
      if (t < 1) requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  }

  // ==================== TOASTS ====================
  function toast({ title, parts = [], tone = "ok", ms = 6500 }) {
    const host_ = $("toastHost");
    const el = document.createElement("div");
    el.className = `toast ${tone}`;
    el.innerHTML =
      `<div class="toast-icon">${tone === "err" ? "!" : tone === "info" ? "i" : "✓"}</div>` +
      `<div class="toast-body"><div class="toast-title">${esc(title)}</div>` +
      (parts.length ? `<div class="toast-text mono">${parts.map(esc).join(" · ")}</div>` : "") +
      `</div>`;
    host_.appendChild(el);
    setTimeout(() => {
      el.classList.add("leaving");
      setTimeout(() => el.remove(), 380);
    }, ms);
    while (host_.children.length > 4) host_.firstChild.remove();
  }

  function pulseFrame(kind) {
    const f = $("boostFrame");
    f.className = "boost-frame";
    void f.offsetWidth;
    f.classList.add("active", kind);
  }

  const S = {
    sessionFreedMb: 0,
    boosting: false,
    boostStart: 0,
    ramBusy: false,
    ramTimer: null,
    scanning: false,
    scanStart: 0,
    cleaning: false,
    scan: {},
    drain: Promise.resolve(),
    cleanItems: [],
  };

  // ==================== OPTIMIZADOR: PLANES DE ENERGÍA ====================
  const PLAN_DESC = {
    balanced: "Equilibrio entre rendimiento y consumo.",
    high: "Máxima respuesta de CPU, ideal para jugar.",
    ultimate: "Mínima latencia, sin ahorro de energía.",
    saver: "Reduce consumo y temperatura.",
    custom: "Plan personalizado del sistema.",
  };

  function renderPlans(list) {
    const grid = $("planGrid");
    if (!list || !list.length) {
      grid.innerHTML = '<div class="empty-inline mono">No se pudieron leer los planes de energía.</div>';
      $("powerActiveTag").textContent = "N/D";
      return;
    }
    grid.innerHTML = list.map((p) =>
      `<button class="plan-card ${p.isActive ? "active" : ""}" data-guid="${esc(p.guid)}">` +
        `<div class="plan-top"><span class="plan-led"></span>` +
        `<span class="plan-state mono">${p.isActive ? "ACTIVO" : "DISPONIBLE"}</span></div>` +
        `<div class="plan-name">${esc(p.name)}</div>` +
        `<div class="plan-desc">${PLAN_DESC[p.kind] || PLAN_DESC.custom}</div>` +
      `</button>`).join("");
    const act = list.find((p) => p.isActive);
    $("powerActiveTag").textContent = act ? act.name : "N/D";
  }

  $("planGrid").addEventListener("click", (e) => {
    const card = e.target.closest(".plan-card");
    if (!card || card.classList.contains("active") || card.classList.contains("pending")) return;
    card.classList.add("pending");
    card.querySelector(".plan-state").textContent = "APLICANDO…";
    log("POWER", `Solicitando cambio de plan de energía…`);
    send("set_power_plan", { guid: card.dataset.guid });
  });

  function onPlanResult(r) {
    if (r.plans) renderPlans(r.plans);
    if (r.success) {
      log("POWER", r.message);
      toast({ title: "Plan de energía actualizado", parts: [r.activeName || "", r.previousName ? `Anterior: ${r.previousName}` : ""].filter(Boolean) });
    } else {
      log("POWER", `Error: ${r.message}`);
      toast({ title: "No se pudo cambiar el plan", parts: [r.message], tone: "err" });
    }
  }

  // ==================== OPTIMIZADOR: PROCESOS ====================
  function renderProcs(list) {
    const box = $("procList");
    if (!list || !list.length) {
      box.innerHTML = '<div class="empty-inline mono">Sin datos de procesos.</div>';
      return;
    }
    const max = Math.max(...list.map((p) => p.memoryMb), 1);
    box.innerHTML = list.map((p, i) =>
      `<div class="proc-row">` +
        `<span class="proc-rank mono">${i + 1}</span>` +
        `<div class="proc-main">` +
          `<div class="proc-top"><span class="proc-name">${esc(p.name)}</span>` +
          `<span class="proc-pid mono">PID ${p.pid}</span>` +
          `<span class="proc-mb mono">${fmtMb(p.memoryMb)}</span></div>` +
          `<div class="proc-bar"><div class="proc-fill" style="width:0%" data-w="${((p.memoryMb / max) * 100).toFixed(1)}"></div></div>` +
        `</div>` +
      `</div>`).join("");
    requestAnimationFrame(() => requestAnimationFrame(() => {
      box.querySelectorAll(".proc-fill").forEach((f) => { f.style.width = `${f.dataset.w}%`; });
    }));
  }

  function onOptimizerState(s) {
    renderPlans(s.plans);
    renderProcs(s.topProcesses);
  }

  $("btnRefreshProcs").addEventListener("click", () => send("get_optimizer_state"));

  // ==================== OPTIMIZADOR: LIBERADOR DE RAM ====================
  function ramProgressStart() {
    const fill = $("ramProgFill");
    clearInterval(S.ramTimer);
    fill.classList.remove("done");
    let w = 6;
    fill.style.width = `${w}%`;
    S.ramTimer = setInterval(() => {
      w += (92 - w) * 0.08;
      fill.style.width = `${w.toFixed(1)}%`;
    }, 120);
  }

  function ramProgressEnd() {
    clearInterval(S.ramTimer);
    const fill = $("ramProgFill");
    fill.style.width = "100%";
    fill.classList.add("done");
    setTimeout(() => { fill.style.width = "0%"; fill.classList.remove("done"); }, 1100);
  }

  function setRamBusy(on) {
    S.ramBusy = on;
    $("btnFreeRam").classList.toggle("loading", on);
    $("btnFreeRam").disabled = on;
    $("btnFreeRamLabel").textContent = on ? "Liberando…" : "Liberar RAM";
  }

  $("btnFreeRam").addEventListener("click", () => {
    if (S.ramBusy) return;
    setRamBusy(true);
    ramProgressStart();
    log("BOOST", "Liberación de RAM iniciada…");
    send("optimize_ram");
  });

  function applyRamResult(r) {
    const from = S.sessionFreedMb;
    S.sessionFreedMb += r.freedMb || 0;
    countUp($("ramSessionVal"), from, S.sessionFreedMb, 900);
    $("ramLast").textContent =
      `${fmtMb(r.usedBeforeMb)} → ${fmtMb(r.usedAfterMb)} · ${r.processesTrimmed} procesos · ${r.durationMs} ms`;
  }

  function onRamResult(r) {
    ramProgressEnd();
    setRamBusy(false);
    applyRamResult(r);
    log("BOOST", `RAM liberada: ${fmtMb(r.freedMb)} · ${r.processesTrimmed} procesos optimizados · ${r.durationMs} ms`);
    toast({
      title: r.freedMb > 0 ? "RAM liberada" : "RAM ya optimizada",
      parts: [`+${fmtMb(r.freedMb)}`, `${r.processesTrimmed} procesos`, `${r.durationMs} ms`],
    });
    send("get_optimizer_state");
  }

  // ==================== BOOST RÁPIDO (DASHBOARD) ====================
  const boostBtn = $("btnQuickBoost");
  const boostLabel = $("boostBtnLabel");

  function resetBoostUi(delay = 0) {
    setTimeout(() => {
      boostBtn.classList.remove("boosting", "boosted");
      boostBtn.disabled = false;
      boostLabel.textContent = "Activar Boost Rápido";
      S.boosting = false;
    }, delay);
  }

  boostBtn.addEventListener("click", () => {
    if (S.boosting) return;
    S.boosting = true;
    S.boostStart = performance.now();
    boostBtn.disabled = true;
    boostBtn.classList.remove("boosted");
    boostBtn.classList.add("boosting");
    boostLabel.textContent = "Optimizando sistema...";
    pulseFrame("go");
    log("BOOST", "Boost Rápido iniciado: plan de energía + RAM + DNS…");
    send("run_quick_boost");
  });

  async function onBoostResult(r) {
    const wait = Math.max(0, 1500 - (performance.now() - S.boostStart));
    if (wait) await sleep(wait);

    const ram = r.ram || {};
    applyRamResult(ram);

    boostBtn.classList.remove("boosting");
    boostBtn.classList.add("boosted");
    boostLabel.textContent = "Boost Aplicado ✓";
    pulseFrame("ok");

    const planPart = r.planChanged ? `${r.planName} activo`
      : r.planAvailable ? `${r.planName} ya activo` : "Plan de energía sin cambios";
    const ramPart = (ram.freedMb || 0) > 0 ? `+${fmtMb(ram.freedMb)} RAM liberados` : "RAM ya optimizada";
    const dnsPart = r.dnsFlushed ? "DNS limpio" : "DNS no vaciado";

    toast({ title: "Boost aplicado", parts: [ramPart, planPart, dnsPart], ms: 7500 });

    log("BOOST", `Boost completado en ${r.durationMs} ms · ${ramPart} · ${planPart} · ${dnsPart}`);
    log("POWER", r.planMessage);
    log("BOOST", `RAM: ${fmtMb(ram.usedBeforeMb)} → ${fmtMb(ram.usedAfterMb)} · ${ram.processesTrimmed} procesos optimizados`);
    log("CLEAN", r.dnsMessage);
    if (r.planChanged && r.previousPlanName) {
      log("INFO", `Para revertir: Optimizador → Plan de Energía → “${r.previousPlanName}”.`);
    }

    send("get_optimizer_state");
    resetBoostUi(2600);
  }

  // ==================== LIMPIEZA ====================
  const CATS = [
    { id: "windows_temp", name: "Temporales de Windows", desc: "%TEMP% y C:\\Windows\\Temp", tone: "cyan" },
    { id: "dx_shaders", name: "Shaders DirectX", desc: "%LOCALAPPDATA%\\D3DSCache", tone: "emerald" },
    { id: "gpu_cache", name: "Caché GPU", desc: "NVIDIA DXCache / GLCache · AMD DxCache", tone: "violet" },
    { id: "dns_cache", name: "Caché DNS", desc: "Vaciado con ipconfig /flushdns", tone: "amber" },
  ];

  function buildCleanGrid() {
    $("cleanGrid").innerHTML = CATS.map((c) =>
      `<article class="card glow-card clean-cat" data-cat="${c.id}">` +
        `<div class="cc-top">` +
          `<label class="chk"><input type="checkbox" checked disabled /><span class="chk-box"></span></label>` +
          `<span class="card-tag ${c.tone}">${esc(c.name.toUpperCase())}</span>` +
          `<span class="cc-status mono" data-tone="">Sin analizar</span>` +
        `</div>` +
        `<div class="cc-path mono">${esc(c.desc)}</div>` +
        `<div class="cc-size mono">—</div>` +
        `<div class="cc-meter"><div class="cc-fill ${c.tone}"></div></div>` +
        `<div class="cc-files mono">—</div>` +
        `<div class="cc-scan" aria-hidden="true"></div>` +
      `</article>`).join("");
  }
  buildCleanGrid();

  const cardOf = (id) => document.querySelector(`.clean-cat[data-cat="${id}"]`);
  function setStatus(id, text, tone = "") {
    const el = cardOf(id)?.querySelector(".cc-status");
    if (el) { el.textContent = text; el.dataset.tone = tone; }
  }

  function setBtnBusy(btn, labelEl, busy, busyText, idleText) {
    btn.classList.toggle("loading", busy);
    labelEl.textContent = busy ? busyText : idleText;
  }

  $("btnScan").addEventListener("click", () => {
    if (S.scanning || S.cleaning) return;
    S.scanning = true;
    S.scanStart = performance.now();
    $("cleanGrid").classList.add("scanning");
    $("scanTrack").classList.add("on");
    $("btnScan").disabled = true;
    $("btnClean").disabled = true;
    setBtnBusy($("btnScan"), $("btnScanLabel"), true, "Analizando…", "Analizar sistema");
    $("cleanBanner").hidden = true;
    $("cleanTotal").textContent = "Analizando (modo Dry Run: no se elimina nada)…";
    CATS.forEach((c) => {
      const el = cardOf(c.id);
      el.classList.remove("done", "draining");
      el.querySelector(".cc-fill").style.width = "0%";
      el.querySelector("input").disabled = true;
      setStatus(c.id, "Analizando…", "busy");
    });
    log("CLEAN", "Análisis iniciado (Dry Run): no se borra ningún archivo.");
    send("scan_cleanup");
  });

  async function onScanResult(r) {
    const wait = Math.max(0, 1400 - (performance.now() - S.scanStart));
    if (wait) await sleep(wait);

    const cats = r.categories || [];
    const maxSize = Math.max(...cats.map((c) => c.sizeMb), 0.01);
    S.scan = {};

    for (const c of cats) {
      S.scan[c.id] = c;
      const el = cardOf(c.id);
      if (!el) continue;
      const sizeEl = el.querySelector(".cc-size");
      const input = el.querySelector("input");
      const isDns = c.id === "dns_cache";

      if (isDns) {
        sizeEl.textContent = "Listo para vaciar";
        el.querySelector(".cc-files").textContent = "ipconfig /flushdns";
        el.querySelector(".cc-fill").style.width = "100%";
        input.disabled = false;
        setStatus(c.id, "Listo", "ok");
      } else if (c.files === 0) {
        sizeEl.textContent = "Vacío";
        el.querySelector(".cc-files").textContent = c.exists ? "0 archivos" : "Carpeta no encontrada";
        input.checked = false;
        input.disabled = true;
        setStatus(c.id, "Nada que limpiar", "muted");
      } else {
        countUp(sizeEl, 0, c.sizeMb, 750);
        el.querySelector(".cc-files").textContent = `${c.files.toLocaleString("es-AR")} archivos`;
        el.querySelector(".cc-fill").style.width = `${Math.max(8, (c.sizeMb / maxSize) * 100).toFixed(1)}%`;
        input.checked = true;
        input.disabled = false;
        setStatus(c.id, "Listo", "ok");
      }
      await sleep(170);
    }

    $("cleanGrid").classList.remove("scanning");
    $("scanTrack").classList.remove("on");
    S.scanning = false;
    $("btnScan").disabled = false;
    setBtnBusy($("btnScan"), $("btnScanLabel"), false, "", "Analizar sistema");
    $("btnClean").disabled = false;
    $("cleanTotal").textContent =
      `Recuperable: ${fmtMb(r.totalMb)} en ${r.totalFiles.toLocaleString("es-AR")} archivos · análisis en ${r.durationMs} ms`;
    cats.forEach((c) => log("CLEAN", `Análisis ${c.name}: ${c.id === "dns_cache" ? "caché DNS lista para vaciar" : `${fmtMb(c.sizeMb)} · ${c.files} archivos`}`));
    log("CLEAN", `Análisis completo: ${fmtMb(r.totalMb)} recuperables (${r.totalFiles} archivos).`);
    toast({ title: "Análisis completado", parts: [`${fmtMb(r.totalMb)} recuperables`, `${r.totalFiles.toLocaleString("es-AR")} archivos`], tone: "info" });
  }

  $("btnClean").addEventListener("click", () => {
    if (S.cleaning || S.scanning) return;
    const ids = [...document.querySelectorAll(".clean-cat input:checked:not(:disabled)")]
      .map((i) => i.closest(".clean-cat").dataset.cat);
    if (!ids.length) {
      toast({ title: "Nada seleccionado", parts: ["Elegí al menos una categoría"], tone: "info" });
      return;
    }
    S.cleaning = true;
    S.drain = Promise.resolve();
    S.cleanItems = [];
    $("btnClean").disabled = true;
    $("btnScan").disabled = true;
    setBtnBusy($("btnClean"), $("btnCleanLabel"), true, "Limpiando…", "Limpiar seleccionados");
    $("cleanBanner").hidden = true;
    ids.forEach((id) => setStatus(id, "En cola", "queued"));
    log("CLEAN", `Limpieza iniciada: ${ids.join(", ")}`);
    send("run_cleanup", { categories: ids });
  });

  async function animateDrain(it) {
    const el = cardOf(it.id);
    if (!el) return;
    setStatus(it.id, "Limpiando…", "busy");
    el.classList.add("draining");
    const sizeEl = el.querySelector(".cc-size");
    const prev = S.scan[it.id]?.sizeMb ?? 0;
    const remain = Math.max(0, prev - it.freedMb);
    el.querySelector(".cc-fill").style.width = "0%";
    if (it.id !== "dns_cache") countUp(sizeEl, prev, remain, 900);
    await sleep(1000);
    el.classList.remove("draining");
    el.classList.add("done");

    if (it.id === "dns_cache") {
      sizeEl.textContent = it.success ? "Vaciada" : "Error";
      setStatus(it.id, it.success ? "Vaciada ✓" : "Falló", it.success ? "ok" : "err");
      log("CLEAN", `DNS: ${it.message}`);
    } else {
      S.scan[it.id] = { ...S.scan[it.id], sizeMb: remain };
      el.querySelector(".cc-files").textContent = it.filesSkipped > 0
        ? `${it.filesDeleted.toLocaleString("es-AR")} eliminados · ${it.filesSkipped} en uso`
        : `${it.filesDeleted.toLocaleString("es-AR")} eliminados`;
      setStatus(it.id, `Liberado ${fmtMb(it.freedMb)} ✓`, "ok");
      log("CLEAN", `${it.name}: ${fmtMb(it.freedMb)} liberados · ${it.message}`);
    }
    el.querySelector("input").checked = false;
    el.querySelector("input").disabled = true;
  }

  function onCleanProgress(it) {
    S.cleanItems.push(it);
    S.drain = S.drain.then(() => animateDrain(it));
  }

  async function onCleanResult(sum) {
    await S.drain;
    S.cleaning = false;
    setBtnBusy($("btnClean"), $("btnCleanLabel"), false, "", "Limpiar seleccionados");
    $("btnScan").disabled = false;
    $("btnClean").disabled = true;

    const banner = $("cleanBanner");
    banner.hidden = false;
    banner.classList.remove("show");
    void banner.offsetWidth;
    banner.classList.add("show");
    countUp($("cbTotal"), 0, sum.totalFreedMb, 1100);
    $("cbSub").textContent =
      `${sum.totalFiles.toLocaleString("es-AR")} archivos eliminados · ${sum.durationMs} ms · volvé a analizar para actualizar`;
    $("cleanTotal").textContent = `Última limpieza: ${fmtMb(sum.totalFreedMb)} liberados.`;

    log("CLEAN", `Limpieza completa: ${fmtMb(sum.totalFreedMb)} liberados · ${sum.totalFiles} archivos · ${sum.durationMs} ms`);
    toast({ title: "Limpieza completada", parts: [`${fmtMb(sum.totalFreedMb)} liberados`, `${sum.totalFiles.toLocaleString("es-AR")} archivos`] });
  }

  // ==================== ERRORES DEL BACKEND ====================
  const OWN_ACTIONS = ["run_quick_boost", "optimize_ram", "set_power_plan", "scan_cleanup", "run_cleanup", "get_optimizer_state"];
  function onActionError(e) {
    const a = e.action;
    if (!OWN_ACTIONS.includes(a)) return;
    if (a === "run_quick_boost") resetBoostUi(0);
    if (a === "optimize_ram") { ramProgressEnd(); setRamBusy(false); }
    if (a === "set_power_plan") send("get_optimizer_state");
    if (a === "scan_cleanup") {
      S.scanning = false;
      $("cleanGrid").classList.remove("scanning");
      $("scanTrack").classList.remove("on");
      $("btnScan").disabled = false;
      setBtnBusy($("btnScan"), $("btnScanLabel"), false, "", "Analizar sistema");
      CATS.forEach((c) => setStatus(c.id, "Sin analizar", ""));
    }
    if (a === "run_cleanup") {
      S.cleaning = false;
      setBtnBusy($("btnClean"), $("btnCleanLabel"), false, "", "Limpiar seleccionados");
      $("btnScan").disabled = false;
      $("btnClean").disabled = false;
    }
    log("ERR", `${a}: ${e.message}`);
    toast({ title: "Acción no completada", parts: [e.message], tone: "err" });
  }

  // ==================== ROUTER DE MENSAJES ====================
  const routes = {
    optimizer_state: onOptimizerState,
    power_plan_result: onPlanResult,
    ram_result: onRamResult,
    scan_result: onScanResult,
    cleanup_progress: onCleanProgress,
    cleanup_result: onCleanResult,
    boost_result: onBoostResult,
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

  // Refrescar estado al entrar al Optimizador
  document.querySelectorAll('[data-view="optimizer"], [data-jump="optimizer"]').forEach((b) =>
    b.addEventListener("click", () => send("get_optimizer_state")));

  // Utilidades compartidas con otros módulos (gamemode.js)
  Object.assign(window.PulseCore = window.PulseCore || {}, { toast, fmtMb, countUp, pulseFrame, sleep, esc });

  // Carga inicial
  send("get_optimizer_state");
})();
