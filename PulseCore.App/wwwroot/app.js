(() => {
  "use strict";

  const host = window.chrome && window.chrome.webview;
  const $ = (id) => document.getElementById(id);

  // ---------- Puente JS -> C# ----------
  const send = (type, payload) => host && host.postMessage({ type, payload });

  // ---------- Telemetría ----------
  const num = (v, d = 0, unit = "") => (v == null ? "N/D" : v.toFixed(d) + unit);

  function renderTelemetry(t) {
    const { cpu, gpu, ram, storage } = t;
    $("cpuName").textContent = cpu.name;
    $("cpuUsage").textContent = num(cpu.usagePercent, 0, "%");
    $("cpuMeta").textContent = `${num(cpu.temperatureC, 0, " °C")} · ${num(cpu.avgFrequencyMhz, 0, " MHz")}`;

    $("gpuName").textContent = gpu.name;
    $("gpuUsage").textContent = num(gpu.usagePercent, 0, "%");
    const vram = gpu.vramUsedMb == null || gpu.vramTotalMb == null
      ? "VRAM N/D"
      : `VRAM ${Math.round(gpu.vramUsedMb)} / ${Math.round(gpu.vramTotalMb)} MB`;
    $("gpuMeta").textContent = `${num(gpu.temperatureC, 0, " °C")} · ${vram}`;

    $("ramPct").textContent = num(ram.percent, 0, "%");
    $("ramMeta").textContent = `${num(ram.usedGb, 1)} / ${num(ram.totalGb, 1)} GB`;

    $("diskName").textContent = `Disco ${storage.drive}`;
    $("diskPct").textContent = num(storage.percent, 0, "%");
    $("diskMeta").textContent = `${num(storage.usedGb, 0)} / ${num(storage.totalGb, 0)} GB`;

    const badge = $("elevBadge");
    badge.textContent = t.isElevated ? "Administrador" : "Sin admin: temperaturas limitadas";
    badge.classList.toggle("warn", !t.isElevated);
    const el = $("telStatus");
    el.classList.remove("error");
    el.textContent = `Fuente: ${t.source} · ${new Date(t.timestamp).toLocaleTimeString()}` +
      (t.notice ? " · Aviso: " + t.notice : "");
  }

  // ---------- Puente C# -> JS ----------
  const handlers = {
    "backend.pong": (p) => {
      $("infoMachine").textContent = p.machineName;
      $("infoOs").textContent = p.windowsVersion;
      $("infoArch").textContent = p.architecture;
      $("infoCores").textContent = p.logicalCores;
      $("info").hidden = false;
      $("status").textContent = "Respuesta recibida de C# a las " + new Date().toLocaleTimeString();
      $("btnPing").disabled = false;
    },
    "telemetry": renderTelemetry,
    "telemetry_error": (m) => {
      const el = $("telStatus");
      el.textContent = "ERROR de telemetría: " + m;
      el.classList.add("error");
    },
    "window.state": (p) => {
      $("iconMax").style.display = p.maximized ? "none" : "";
      $("iconRestore").style.display = p.maximized ? "" : "none";
      $("btnMax").title = p.maximized ? "Restaurar" : "Maximizar";
    },
  };

  if (host) {
    host.addEventListener("message", (e) => {
      const msg = e.data;
      const fn = msg && handlers[msg.type];
      if (!fn) return;
      // Contrato: telemetry -> { data }, telemetry_error -> { message }, resto -> { payload }.
      const arg = msg.type === "telemetry" ? msg.data
        : msg.type === "telemetry_error" ? msg.message
        : (msg.payload || {});
      try { fn(arg); }
      catch (err) {
        const el = $("telStatus");
        el.textContent = `ERROR en JS (${msg.type}): ${err && err.stack || err}`;
        el.classList.add("error");
      }
    });
  } else {
    $("status").textContent = "Ejecutando fuera de WebView2: el puente no está disponible.";
  }

  // ---------- Barra de título ----------
  $("dragArea").addEventListener("mousedown", (e) => {
    if (e.button === 0 && e.detail === 1) send("window.drag");
  });
  $("dragArea").addEventListener("dblclick", () => send("window.toggleMaximize"));
  $("btnMin").addEventListener("click", () => send("window.minimize"));
  $("btnMax").addEventListener("click", () => send("window.toggleMaximize"));
  $("btnClose").addEventListener("click", () => send("window.close"));

  // ---------- Ping ----------
  $("btnPing").addEventListener("click", () => {
    $("btnPing").disabled = true;
    $("status").textContent = "Enviando ping…";
    send("backend.ping");
  });

  send("app.ready");
})();
