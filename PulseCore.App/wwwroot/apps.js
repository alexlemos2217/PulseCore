(() => {
  "use strict";

  const host = window.chrome && window.chrome.webview;
  if (!host) return;

  const $ = (id) => document.getElementById(id);
  const PC = window.PulseCore || {};
  const { toast, esc, pulseFrame } = PC;
  const log = (lvl, msg) => PC.addLog && PC.addLog(lvl, msg);
  const send = (type, payload) => host.postMessage({ type, payload });

  const GAMER_PACK = [
    "Discord.Discord",
    "Valve.Steam",
    "EpicGames.EpicGamesLauncher",
    "OBSProject.OBSStudio",
    "Brave.Brave",
    "7zip.7zip",
  ];

  const wg = {
    available: false,
    busy: false,
    apps: [],
    selected: new Set(),
  };

  function appendConsole(line, tone = "out") {
    const box = $("wgConsole");
    if (!box) return;
    const div = document.createElement("div");
    div.className = `wg-line ${tone}`;
    const ts = new Date().toLocaleTimeString("es-AR", { hour12: false });
    div.textContent = `[${ts}] ${line}`;
    box.appendChild(div);
    while (box.children.length > 350) box.firstChild.remove();
    box.scrollTop = box.scrollHeight;
  }

  function syncButtons() {
    const count = wg.selected.size;
    const installBtn = $("btnWgInstall");
    const upgradeBtn = $("btnWgUpgradeAll");
    $("btnWgInstallLabel").textContent = wg.busy
      ? "Instalando…"
      : `⬇ Instalar seleccionadas (${count})`;
    installBtn.disabled = !wg.available || wg.busy || count === 0;
    upgradeBtn.disabled = !wg.available || wg.busy;
    installBtn.classList.toggle("loading", wg.busy && wg.activeAction === "install");
    upgradeBtn.classList.toggle("loading", wg.busy && wg.activeAction === "upgrade");
  }

  function renderCatalog(cat) {
    wg.available = !!cat.isAvailable;
    wg.busy = !!cat.isBusy;
    wg.apps = cat.apps || [];

    const pill = $("wgStatusPill");
    if (wg.available) {
      pill.textContent = `winget ${cat.version || "OK"}`;
      pill.style.color = "var(--emerald)";
      pill.style.borderColor = "rgba(16, 185, 129, 0.35)";
    } else {
      pill.textContent = "winget no disponible";
      pill.style.color = "var(--red)";
      pill.style.borderColor = "rgba(239, 68, 68, 0.4)";
      $("wgProgressText").textContent = "Instalá 'Instalador de aplicaciones' desde Microsoft Store para usar este módulo.";
    }

    const categories = ["Gaming", "Navegadores y Multimedia", "Utilidades"];
    const root = $("wgCatalog");
    root.innerHTML = categories.map((catName) => {
      const items = wg.apps.filter((a) => a.category === catName);
      if (!items.length) return "";
      const cards = items.map((a) => {
        const checked = wg.selected.has(a.id);
        return `<label class="card glow-card wg-app-card ${checked ? "selected" : ""}" data-id="${esc(a.id)}">` +
          `<div class="wg-app-top">` +
            `<span class="chk"><input type="checkbox" ${checked ? "checked" : ""} /><span class="chk-box"></span></span>` +
            `<div class="wg-app-title">${esc(a.name)}</div>` +
          `</div>` +
          `<div class="wg-app-id mono">${esc(a.id)}</div>` +
          `<p class="wg-app-desc">${esc(a.description)}</p>` +
        `</label>`;
      }).join("");

      return `<div class="wg-group">` +
        `<h3 class="wg-group-title">${esc(catName)}</h3>` +
        `<div class="wg-group-grid">${cards}</div>` +
      `</div>`;
    }).join("");

    syncButtons();
  }

  // Selección de tarjetas
  $("wgCatalog")?.addEventListener("change", (e) => {
    const card = e.target.closest(".wg-app-card");
    if (!card) return;
    const id = card.dataset.id;
    if (e.target.checked) wg.selected.add(id);
    else wg.selected.delete(id);
    card.classList.toggle("selected", e.target.checked);
    syncButtons();
  });

  $("btnWgPackGamer")?.addEventListener("click", () => {
    if (wg.busy) return;
    wg.selected.clear();
    GAMER_PACK.forEach((id) => wg.selected.add(id));
    document.querySelectorAll(".wg-app-card").forEach((c) => {
      const on = wg.selected.has(c.dataset.id);
      c.classList.toggle("selected", on);
      const inp = c.querySelector("input");
      if (inp) inp.checked = on;
    });
    syncButtons();
  });

  $("btnWgClearSel")?.addEventListener("click", () => {
    if (wg.busy) return;
    wg.selected.clear();
    document.querySelectorAll(".wg-app-card").forEach((c) => {
      c.classList.remove("selected");
      const inp = c.querySelector("input");
      if (inp) inp.checked = false;
    });
    syncButtons();
  });

  $("btnWgClearConsole")?.addEventListener("click", () => {
    $("wgConsole").innerHTML = '<div class="wg-line muted">[limpio] Consola lista.</div>';
  });

  $("btnWgInstall")?.addEventListener("click", () => {
    if (wg.busy || !wg.available || wg.selected.size === 0) return;
    const ids = [...wg.selected];
    wg.busy = true;
    wg.activeAction = "install";
    syncButtons();
    $("wgProgressText").textContent = `Iniciando instalación de ${ids.length} aplicaciones…`;
    appendConsole(`Iniciando instalación silenciosa de ${ids.length} apps…`, "info");
    log("APPS", `Instalación winget iniciada (${ids.length} apps): ${ids.join(", ")}`);
    send("install_winget_apps", { packageIds: ids });
  });

  $("btnWgUpgradeAll")?.addEventListener("click", () => {
    if (wg.busy || !wg.available) return;
    wg.busy = true;
    wg.activeAction = "upgrade";
    $("btnWgUpgradeLabel").textContent = "Actualizando PC…";
    syncButtons();
    $("wgProgressText").textContent = "Ejecutando winget upgrade --all en segundo plano…";
    appendConsole("Ejecutando winget upgrade --all --silent…", "info");
    log("APPS", "Actualización global de apps (winget upgrade --all) iniciada.");
    send("upgrade_all_winget_apps");
  });

  // Respuestas C#
  function onOutput(ev) {
    appendConsole(ev.line, ev.stream || "out");
    if (ev.current && ev.total) {
      $("wgProgressText").textContent = `Instalando ${ev.current} de ${ev.total} (${ev.currentId || ""})…`;
    }
  }

  function onCompleted(res) {
    wg.busy = false;
    wg.activeAction = null;
    $("btnWgUpgradeLabel").textContent = "🔄 Actualizar todas las apps del PC";
    syncButtons();
    $("wgProgressText").textContent = res.message;

    if (res.success) {
      pulseFrame && pulseFrame("ok");
      toast({
        title: res.action === "upgrade" ? "Apps actualizadas" : "Instalación completada",
        parts: [res.message],
        ms: 7500,
      });
      log("APPS", res.message);
    } else {
      toast({ title: "Operación winget finalizada con avisos", parts: [res.message], tone: "info", ms: 8000 });
      log("WARN", `winget: ${res.message}`);
    }
  }

  const routes = {
    winget_catalog: renderCatalog,
    winget_output: onOutput,
    winget_completed: onCompleted,
  };

  host.addEventListener("message", (e) => {
    const m = e.data;
    const fn = m && routes[m.type];
    if (!fn) return;
    try { fn(m.payload || {}); }
    catch (err) { log("ERR", `Error UI (${m.type}): ${err && err.message || err}`); }
  });

  document.querySelectorAll('[data-view="apps"]').forEach((b) =>
    b.addEventListener("click", () => send("get_winget_catalog")));

  send("get_winget_catalog");
})();
