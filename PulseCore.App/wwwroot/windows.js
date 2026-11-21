(() => {
  "use strict";

  const host = window.chrome && window.chrome.webview;
  if (!host) return;

  const $ = (id) => document.getElementById(id);
  const PC = window.PulseCore || {};
  const { toast, esc, pulseFrame } = PC;
  const log = (lvl, msg) => PC.addLog && PC.addLog(lvl, msg);
  const send = (type, payload) => host.postMessage({ type, payload });

  let repairBusy = false;

  // ==================== SUB-PESTAÑAS ====================
  const SUB_MAP = { debloat: "winSubDebloat", startup: "winSubStartup", repair: "winSubRepair" };
  $("winSubTabs")?.addEventListener("click", (e) => {
    const btn = e.target.closest(".mode-btn");
    if (!btn) return;
    const sub = btn.dataset.sub;
    $("winSubTabs").querySelectorAll(".mode-btn").forEach((b) => b.classList.toggle("active", b === btn));
    Object.entries(SUB_MAP).forEach(([k, id]) => $(id)?.classList.toggle("active", k === sub));
    if (sub === "debloat") send("get_debloat_items");
    if (sub === "startup") send("get_startup_apps");
  });

  // ==================== 1. DEBLOAT SEGURO ====================
  function renderDebloat(items) {
    const box = $("debloatList");
    if (!box) return;
    const arr = Array.isArray(items) ? items : [];
    box.innerHTML = arr.map((it) => {
      const cls = it.level === "Esencial" ? "cyan" : "violet";
      return `<article class="card glow-card win-deb-card ${it.isApplied ? "applied" : ""}">` +
        `<div class="win-deb-info">` +
          `<div class="win-deb-head">` +
            `<span class="card-tag ${cls}">${esc(it.level.toUpperCase())}</span>` +
            `<h3>${esc(it.name)}</h3>` +
          `</div>` +
          `<p>${esc(it.description)}</p>` +
        `</div>` +
        `<label class="switch"><input type="checkbox" class="deb-toggle" data-id="${esc(it.id)}" ${it.isApplied ? "checked" : ""} /><span></span></label>` +
      `</article>`;
    }).join("");
  }

  $("debloatList")?.addEventListener("change", (e) => {
    const inp = e.target.closest(".deb-toggle");
    if (!inp) return;
    inp.disabled = true;
    send("toggle_debloat_item", { id: inp.dataset.id, enable: inp.checked });
  });

  function onDebloatResult(r) {
    renderDebloat(r.items);
    if (r.success) {
      pulseFrame && pulseFrame("ok");
      toast({ title: "Debloat actualizado", parts: [r.message], ms: 6000 });
      log("WINDOWS", r.message);
    } else {
      toast({ title: "No se pudo aplicar Debloat", parts: [r.message], tone: "err" });
      log("ERR", r.message);
    }
    send("get_restore_history");
  }

  // ==================== 2. INICIO DE WINDOWS ====================
  function renderStartup(apps) {
    const arr = Array.isArray(apps) ? apps : [];
    $("startupCount").textContent = `${arr.length} ${arr.length === 1 ? "app" : "apps"}`;
    const box = $("startupList");
    if (!box) return;
    if (!arr.length) {
      box.innerHTML = '<div class="empty-inline mono">No se encontraron entradas de inicio en el Registro.</div>';
      return;
    }

    box.innerHTML = arr.map((a) => {
      const locCls = a.location === "Sistema" ? "violet" : "cyan";
      return `<div class="st-row ${a.isEnabled ? "" : "off"}">` +
        `<div class="st-info">` +
          `<div class="st-head">` +
            `<span class="card-tag ${locCls}">${esc(a.location.toUpperCase())}</span>` +
            `<span class="st-name">${esc(a.name)}</span>` +
          `</div>` +
          `<div class="st-cmd mono" title="${esc(a.command)}">${esc(a.command)}</div>` +
        `</div>` +
        `<label class="switch"><input type="checkbox" class="st-toggle" data-name="${esc(a.name)}" data-loc="${esc(a.location)}" ${a.isEnabled ? "checked" : ""} /><span></span></label>` +
      `</div>`;
    }).join("");
  }

  $("btnRefreshStartup")?.addEventListener("click", () => send("get_startup_apps"));

  $("startupList")?.addEventListener("change", (e) => {
    const inp = e.target.closest(".st-toggle");
    if (!inp) return;
    inp.disabled = true;
    send("toggle_startup_app", { name: inp.dataset.name, location: inp.dataset.loc, enable: inp.checked });
  });

  function onStartupResult(r) {
    renderStartup(r.apps);
    if (r.success) {
      toast({ title: "Inicio de Windows actualizado", parts: [r.message], ms: 5500 });
      log("WINDOWS", r.message);
    } else {
      toast({ title: "No se pudo cambiar el inicio", parts: [r.message], tone: "err" });
      log("ERR", r.message);
    }
  }

  // ==================== 3. REPARACIÓN SFC / DISM ====================
  function appendRepairLine(line, tone = "out") {
    const box = $("repairConsole");
    if (!box) return;
    const div = document.createElement("div");
    div.className = `wg-line ${tone}`;
    const ts = new Date().toLocaleTimeString("es-AR", { hour12: false });
    div.textContent = `[${ts}] ${line}`;
    box.appendChild(div);
    while (box.children.length > 400) box.firstChild.remove();
    box.scrollTop = box.scrollHeight;
  }

  function setRepairBusy(on, tool) {
    repairBusy = on;
    $("btnRunSfc").disabled = on;
    $("btnRunDism").disabled = on;
    $("btnRunSfc").classList.toggle("loading", on && tool === "sfc");
    $("btnRunDism").classList.toggle("loading", on && tool === "dism");
    $("btnRunSfcLabel").textContent = on && tool === "sfc" ? "Escaneando SFC…" : "Ejecutar SFC";
    $("btnRunDismLabel").textContent = on && tool === "dism" ? "Reparando DISM…" : "Ejecutar DISM";
    $("repairStatusHead").textContent = on
      ? `CONSOLA DE REPARACIÓN · ${tool.toUpperCase()} EN EJECUCIÓN`
      : "CONSOLA DE REPARACIÓN EN VIVO";
  }

  $("btnRunSfc")?.addEventListener("click", () => {
    if (repairBusy) return;
    setRepairBusy(true, "sfc");
    appendRepairLine("Iniciando sfc /scannow…", "info");
    log("WINDOWS", "Escaneo de integridad SFC (/scannow) iniciado.");
    send("run_windows_repair", { tool: "sfc" });
  });

  $("btnRunDism")?.addEventListener("click", () => {
    if (repairBusy) return;
    setRepairBusy(true, "dism");
    appendRepairLine("Iniciando DISM /Online /Cleanup-Image /RestoreHealth…", "info");
    log("WINDOWS", "Reparación de imagen DISM (/RestoreHealth) iniciada.");
    send("run_windows_repair", { tool: "dism" });
  });

  $("btnClearRepairConsole")?.addEventListener("click", () => {
    $("repairConsole").innerHTML = '<div class="wg-line muted">[limpio] Consola lista.</div>';
  });

  function onRepairCompleted(r) {
    setRepairBusy(false, "");
    if (r.success) {
      pulseFrame && pulseFrame("ok");
      toast({ title: "Reparación completada", parts: [r.message], ms: 8000 });
      log("WINDOWS", r.message);
    } else {
      toast({ title: "Reparación finalizada con avisos", parts: [r.message], tone: "info", ms: 8500 });
      log("WARN", r.message);
    }
  }

  // ==================== ROUTER ====================
  const OWN = ["get_debloat_items", "toggle_debloat_item", "get_startup_apps", "toggle_startup_app", "run_windows_repair"];
  const routes = {
    debloat_items: (p) => renderDebloat(p.items),
    debloat_result: onDebloatResult,
    startup_apps: (p) => renderStartup(p.apps),
    startup_result: onStartupResult,
    repair_output: (ev) => appendRepairLine(ev.line, ev.stream || "out"),
    repair_completed: onRepairCompleted,
    action_error: (e) => {
      if (!OWN.includes(e.action)) return;
      setRepairBusy(false, "");
      send("get_debloat_items");
      send("get_startup_apps");
      log("ERR", `${e.action}: ${e.message}`);
      toast({ title: "Acción no completada", parts: [e.message], tone: "err" });
    },
  };

  host.addEventListener("message", (e) => {
    const m = e.data;
    const fn = m && routes[m.type];
    if (!fn) return;
    try { fn(m.payload || {}); }
    catch (err) { log("ERR", `Error UI (${m.type}): ${err && err.message || err}`); }
  });

  document.querySelectorAll('[data-view="windows"]').forEach((b) =>
    b.addEventListener("click", () => {
      send("get_debloat_items");
      send("get_startup_apps");
    }));

  send("get_debloat_items");
})();
