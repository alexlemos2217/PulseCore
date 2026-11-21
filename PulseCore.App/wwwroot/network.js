(() => {
  "use strict";

  const host = window.chrome && window.chrome.webview;
  if (!host) return;

  const $ = (id) => document.getElementById(id);
  const PC = window.PulseCore || {};
  const { toast, esc, pulseFrame } = PC;
  const log = (lvl, msg) => PC.addLog && PC.addLog(lvl, msg);
  const send = (type, payload) => host.postMessage({ type, payload });

  let plHost = "1.1.1.1";
  let plBusy = false;
  let benchBusy = false;

  // ==================== 1. DNS SWITCHER & BENCHMARK ====================
  function renderDns(st) {
    if (!st) return;
    benchBusy = false;
    const b = $("btnBenchDns");
    if (b) { b.disabled = false; b.classList.remove("loading"); }
    if ($("btnBenchDnsLabel")) $("btnBenchDnsLabel").textContent = "⚡ Medir mejor DNS";

    $("dnsIfaceInfo").textContent = st.interfaceName
      ? `${st.interfaceName} · DNS actual: ${st.currentDns || "DHCP"}`
      : "No se detectó interfaz activa";

    const list = st.providers || [];
    $("dnsGrid").innerHTML = list.map((p) => {
      const ms = p.latencyMs != null ? `${p.latencyMs.toFixed(1)} ms` : (p.id === "auto" ? "DHCP" : "—");
      const badge = p.isActive
        ? `<span class="dns-pill active mono">ACTIVO</span>`
        : p.isRecommended
          ? `<span class="dns-pill best mono">MÁS RÁPIDO</span>`
          : "";
      return `<div class="dns-card ${p.isActive ? "active" : ""} ${p.isRecommended ? "best" : ""}">` +
        `<div class="dns-top">` +
          `<span class="dns-name">${esc(p.name)}</span>` +
          badge +
        `</div>` +
        `<div class="dns-ips mono">${esc(p.primary)}${p.secondary && p.secondary !== "—" ? " · " + esc(p.secondary) : ""}</div>` +
        `<div class="dns-foot">` +
          `<span class="dns-ms mono">${ms}</span>` +
          `<button class="action-btn small dns-apply" data-id="${esc(p.id)}" ${p.isActive ? "disabled" : ""}>${p.isActive ? "En uso" : "Aplicar"}</button>` +
        `</div>` +
      `</div>`;
    }).join("");
  }

  $("btnBenchDns")?.addEventListener("click", () => {
    if (benchBusy) return;
    benchBusy = true;
    $("btnBenchDns").disabled = true;
    $("btnBenchDns").classList.add("loading");
    $("btnBenchDnsLabel").textContent = "Midiendo…";
    log("RED", "Ejecutando benchmark de DNS (Cloudflare, Google, Quad9)…");
    send("benchmark_dns");
  });

  $("dnsGrid")?.addEventListener("click", (e) => {
    const btn = e.target.closest(".dns-apply");
    if (!btn || btn.disabled) return;
    btn.disabled = true;
    btn.textContent = "Aplicando…";
    send("apply_dns", { providerId: btn.dataset.id });
  });

  function onDnsApply(r) {
    renderDns({ interfaceName: r.interfaceName, currentDns: r.currentDns, providers: r.providers });
    if (r.success) {
      pulseFrame && pulseFrame("ok");
      toast({ title: "DNS actualizado", parts: [r.message], ms: 6500 });
      log("RED", r.message);
    } else {
      toast({ title: "No se pudo cambiar el DNS", parts: [r.message], tone: "err" });
      log("ERR", r.message);
    }
  }

  // ==================== 2. TEST DE PACKET LOSS (20 PINGS) ====================
  $("plHostSwitch")?.addEventListener("click", (e) => {
    const btn = e.target.closest(".mode-btn");
    if (!btn || plBusy) return;
    plHost = btn.dataset.host;
    $("plHostSwitch").querySelectorAll(".mode-btn").forEach((b) => b.classList.toggle("active", b === btn));
  });

  $("btnRunPl")?.addEventListener("click", () => {
    if (plBusy) return;
    plBusy = true;
    $("btnRunPl").disabled = true;
    $("btnRunPl").classList.add("loading");
    $("btnRunPlLabel").textContent = "Mid…";
    $("plBar").style.width = "0%";
    $("plProgressLbl").textContent = `Enviando 0 / 20 paquetes a ${plHost}…`;
    $("plStabBadge").textContent = "Midiendo…";
    $("plStabBadge").dataset.tone = "";
    log("RED", `Test de Packet Loss iniciado (20 pings → ${plHost}).`);
    send("run_packet_loss", { host: plHost });
  });

  function onPlProgress(ev) {
    const pct = ev.total > 0 ? Math.round((ev.sent / ev.total) * 100) : 0;
    $("plBar").style.width = `${pct}%`;
    $("plProgressLbl").textContent =
      `Enviando ${ev.sent} / ${ev.total} paquetes · perdidos: ${ev.lost}` +
      (ev.lastMs != null ? ` · último: ${Math.round(ev.lastMs)} ms` : " · timeout");
    $("plLoss").textContent = `${ev.lossPercent}%`;
  }

  function onPlResult(r) {
    plBusy = false;
    $("btnRunPl").disabled = false;
    $("btnRunPl").classList.remove("loading");
    $("btnRunPlLabel").textContent = "Probar";
    $("plBar").style.width = "100%";

    $("plProgressLbl").textContent = `${r.received}/${r.sent} recibidos en ${r.host} · ${r.lost} perdidos`;
    const badge = $("plStabBadge");
    badge.textContent = r.stability;
    const tone = r.stability === "Excelente" || r.stability === "Buena" ? "ok"
      : r.stability === "Inestable" ? "warn" : "err";
    badge.dataset.tone = tone;

    const lossEl = $("plLoss");
    lossEl.textContent = `${r.lossPercent}%`;
    lossEl.style.color = r.lossPercent === 0 ? "var(--emerald)" : r.lossPercent <= 5 ? "var(--amber)" : "var(--red)";

    $("plAvg").textContent = r.avgMs != null ? `${r.avgMs} ms` : "N/D";
    $("plMinMax").textContent = r.minMs != null && r.maxMs != null ? `${r.minMs} / ${r.maxMs}` : "N/D";
    $("plJitter").textContent = r.jitterMs != null ? `${r.jitterMs} ms` : "N/D";

    log("RED", `Packet Loss (${r.host}): ${r.lossPercent}% pérdida · prom ${r.avgMs ?? "N/D"} ms · jitter ${r.jitterMs ?? "N/D"} ms · estabilidad: ${r.stability}`);
    toast({
      title: `Estabilidad de red: ${r.stability}`,
      parts: [`Loss ${r.lossPercent}%`, r.avgMs != null ? `Prom ${r.avgMs} ms` : "Sin respuesta", r.jitterMs != null ? `Jitter ${r.jitterMs} ms` : null].filter(Boolean),
      tone: tone === "ok" ? "ok" : tone === "warn" ? "info" : "err",
    });
  }

  // ==================== ROUTER ====================
  const OWN = ["get_dns_state", "benchmark_dns", "apply_dns", "run_packet_loss"];
  const routes = {
    dns_state: renderDns,
    dns_apply_result: onDnsApply,
    packet_loss_progress: onPlProgress,
    packet_loss_result: onPlResult,
    action_error: (e) => {
      if (!OWN.includes(e.action)) return;
      plBusy = false; benchBusy = false;
      $("btnRunPl").disabled = false; $("btnRunPl").classList.remove("loading"); $("btnRunPlLabel").textContent = "Probar";
      $("btnBenchDns").disabled = false; $("btnBenchDns").classList.remove("loading"); $("btnBenchDnsLabel").textContent = "⚡ Medir mejor DNS";
      log("ERR", `${e.action}: ${e.message}`);
      toast({ title: "Error de red", parts: [e.message], tone: "err" });
    },
  };

  host.addEventListener("message", (e) => {
    const m = e.data;
    const fn = m && routes[m.type];
    if (!fn) return;
    try { fn(m.payload || {}); }
    catch (err) { log("ERR", `Error UI (${m.type}): ${err && err.message || err}`); }
  });

  document.querySelectorAll('[data-view="gamemode"], [data-jump="gamemode"]').forEach((b) =>
    b.addEventListener("click", () => send("get_dns_state")));

  send("get_dns_state");
})();
