(() => {
  "use strict";

  const host = window.chrome && window.chrome.webview;
  const $ = (id) => document.getElementById(id);

  // Tacómetro 240°: cx=66, cy=64, r=46, inicio=150°, barrido=240°
  const GAUGE_CX = 66;
  const GAUGE_CY = 64;
  const GAUGE_R = 46;
  const GAUGE_ARC_LEN = 2 * Math.PI * GAUGE_R * (240 / 360); // 192.684
  const TICK_COUNT = 25;
  const LED_COUNT = 20;
  const HISTORY_LEN = 60;

  const state = {
    cpuUsage: new Array(HISTORY_LEN).fill(null),
    gpuUsage: new Array(HISTORY_LEN).fill(null),
    cpuTemp: new Array(HISTORY_LEN).fill(null),
    gpuTemp: new Array(HISTORY_LEN).fill(null),
    ramPct: new Array(HISTORY_LEN).fill(null),
    ramGb: new Array(HISTORY_LEN).fill(null),
    chartMode: "usage", // "usage" | "temp" | "ram"
    showA: true,
    showB: true,
    hoverIdx: -1,
    peakCpu: 0,
    peakGpu: 0,
    peakCpuTemp: 0,
    peakGpuTemp: 0,
    peakRam: 0,
    firstTelemetry: false,
    lastHighTempLog: 0,
    logs: [],
    logFilter: "ALL",
  };

  // ==================== PUENTE JS -> C# ====================
  const send = (type, payload) => host && host.postMessage({ type, payload });

  // ==================== INICIALIZACIÓN DE INSTRUMENTOS ====================
  function buildGaugeTicks(groupId) {
    const g = $(groupId);
    if (!g) return;
    const ns = "http://www.w3.org/2000/svg";
    for (let i = 0; i < TICK_COUNT; i++) {
      const deg = 150 + (i / (TICK_COUNT - 1)) * 240;
      const rad = (deg * Math.PI) / 180;
      const major = i % 6 === 0;
      const rIn = major ? 53.5 : 55;
      const rOut = major ? 59.5 : 58;
      const line = document.createElementNS(ns, "line");
      line.setAttribute("x1", (GAUGE_CX + rIn * Math.cos(rad)).toFixed(2));
      line.setAttribute("y1", (GAUGE_CY + rIn * Math.sin(rad)).toFixed(2));
      line.setAttribute("x2", (GAUGE_CX + rOut * Math.cos(rad)).toFixed(2));
      line.setAttribute("y2", (GAUGE_CY + rOut * Math.sin(rad)).toFixed(2));
      line.setAttribute("stroke-width", major ? "1.6" : "1");
      g.appendChild(line);
    }
  }

  function buildLedBar() {
    const bar = $("ledBar");
    bar.innerHTML = "";
    for (let i = 0; i < LED_COUNT; i++) {
      const seg = document.createElement("span");
      seg.className = "led-seg";
      bar.appendChild(seg);
    }
  }

  ["cpuTicks", "gpuTicks", "ramTicks", "diskTicks"].forEach(buildGaugeTicks);
  buildLedBar();

  // ==================== LOGS EN VIVO ====================
  function addLog(level, message) {
    state.logs.push({
      ts: new Date().toLocaleTimeString("es-AR", { hour12: false }),
      level,
      message,
    });
    if (state.logs.length > 250) state.logs.shift();
    $("logCountBadge").textContent = state.logs.length;
    renderLogs();
  }

  function renderLogs() {
    const box = $("logConsole");
    const filtered = state.logFilter === "ALL"
      ? state.logs
      : state.logs.filter((l) => l.level === state.logFilter);

    box.innerHTML = filtered.map((l) =>
      `<div class="log-line">` +
        `<span class="log-ts">[${l.ts}]</span>` +
        `<span class="log-lvl ${l.level}">${l.level}</span>` +
        `<span class="log-msg">${escapeHtml(l.message)}</span>` +
      `</div>`
    ).join("");
    box.scrollTop = box.scrollHeight;
  }

  function escapeHtml(str) {
    return String(str).replace(/[&<>"']/g, (c) =>
      ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  }

  // ==================== HELPERS DE INSTRUMENTOS ====================
  const clamp = (v, min, max) => Math.max(min, Math.min(max, v));
  const fmt = (v, d = 0, unit = "") => (v == null || Number.isNaN(v) ? "N/D" : `${Number(v).toFixed(d)}${unit}`);

  function shortSource(src) {
    if (!src) return "Sensores";
    if (src.toLowerCase().includes("librehardware")) return "LHM Driver";
    if (src.toLowerCase().includes("fallback")) return "Win Native";
    return src;
  }

  function resolveGaugeTone(pct, baseClass, tempC) {
    if ((pct != null && pct >= 88) || (tempC != null && tempC >= 82)) return "hot";
    if ((pct != null && pct >= 70) || (tempC != null && tempC >= 70)) return "amber";
    return baseClass;
  }

  function setGauge(prefix, pct, baseClass, tempC = null) {
    const arc = $(`${prefix}Arc`);
    const tip = $(`${prefix}Tip`);
    const ticks = $(`${prefix}Ticks`);
    const p = pct == null ? 0 : clamp(pct, 0, 100);
    const tone = resolveGaugeTone(p, baseClass, tempC);

    arc.style.strokeDashoffset = (GAUGE_ARC_LEN * (1 - p / 100)).toFixed(2);
    arc.classList.remove("cyan", "emerald", "violet", "amber", "hot");
    arc.classList.add(tone);

    // Posición exacta de la punta luminosa del arco 240°
    const deg = 150 + (p / 100) * 240;
    const rad = (deg * Math.PI) / 180;
    tip.setAttribute("cx", (GAUGE_CX + GAUGE_R * Math.cos(rad)).toFixed(2));
    tip.setAttribute("cy", (GAUGE_CY + GAUGE_R * Math.sin(rad)).toFixed(2));
    tip.style.opacity = pct == null ? "0.25" : "1";
    tip.classList.remove("cyan", "emerald", "violet", "amber", "hot");
    tip.classList.add(tone);

    // Iluminar marcas de escala alcanzadas
    const litCount = Math.round((p / 100) * TICK_COUNT);
    const lines = ticks.children;
    for (let i = 0; i < lines.length; i++) {
      lines[i].setAttribute("class", i < litCount ? `lit-${tone}` : "");
    }
  }

  function setTempPill(el, tempC) {
    el.classList.remove("cool", "warm", "hot", "muted");
    if (tempC == null || tempC <= 0) {
      el.textContent = "N/D";
      el.classList.add("muted");
      return;
    }
    el.textContent = `${Math.round(tempC)} °C`;
    if (tempC >= 80) el.classList.add("hot");
    else if (tempC >= 65) el.classList.add("warm");
    else el.classList.add("cool");
  }

  // ==================== RENDER TELEMETRÍA ====================
  function renderTelemetry(t) {
    if (!t || !t.cpu) return;
    const { cpu, gpu, ram, storage } = t;
    const rawSource = t.source || "Sensores";

    // Pill LIVE en la barra superior (una sola línea)
    const pill = $("livePill");
    pill.classList.remove("warn", "err");
    if (t.isElevated) {
      $("livePillText").textContent = "LIVE · ADMIN";
    } else {
      $("livePillText").textContent = "LIVE · LIMITADO";
      pill.classList.add("warn");
    }

    // Mini resumen en barra superior + footer sidebar
    const cpuTempShort = cpu.temperatureC != null ? `${Math.round(cpu.temperatureC)}°C` : "N/D";
    const gpuTempShort = gpu.temperatureC != null ? `${Math.round(gpu.temperatureC)}°C` : "N/D";
    $("tbCpu").textContent = `${fmt(cpu.usagePercent, 0, "%")} · ${cpuTempShort}`;
    $("tbGpu").textContent = `${fmt(gpu.usagePercent, 0, "%")} · ${gpuTempShort}`;
    const srcEl = $("sbSource");
    srcEl.textContent = `Driver: ${shortSource(rawSource)}`;
    srcEl.title = `Fuente de telemetría: ${rawSource}`;

    // 1. CPU
    setGauge("cpu", cpu.usagePercent, "cyan", cpu.temperatureC);
    $("cpuUsage").textContent = fmt(cpu.usagePercent, 0, "%");
    $("cpuName").textContent = cpu.name || "CPU";
    $("cpuName").title = cpu.name || "CPU";
    $("cpuFreq").textContent = cpu.avgFrequencyMhz != null ? `${Math.round(cpu.avgFrequencyMhz)} MHz` : "N/D";
    $("cpuTempText").textContent = cpu.temperatureC != null ? `${Math.round(cpu.temperatureC)} °C` : "N/D";
    setTempPill($("cpuTempPill"), cpu.temperatureC);

    // 2. GPU
    setGauge("gpu", gpu.usagePercent, "emerald", gpu.temperatureC);
    $("gpuUsage").textContent = fmt(gpu.usagePercent, 0, "%");
    $("gpuName").textContent = gpu.name || "GPU";
    $("gpuName").title = gpu.name || "GPU";
    setTempPill($("gpuTempPill"), gpu.temperatureC);
    if (gpu.vramUsedMb != null && gpu.vramTotalMb != null && gpu.vramTotalMb > 0) {
      const vramPct = clamp((gpu.vramUsedMb / gpu.vramTotalMb) * 100, 0, 100);
      $("gpuVramText").textContent = `${Math.round(gpu.vramUsedMb)} / ${Math.round(gpu.vramTotalMb)} MB`;
      $("gpuVramBar").style.width = `${vramPct.toFixed(1)}%`;
    } else {
      $("gpuVramText").textContent = "N/D";
      $("gpuVramBar").style.width = "0%";
    }

    // 3. RAM Física
    setGauge("ram", ram.percent, "violet");
    $("ramPct").textContent = fmt(ram.percent, 0, "%");
    $("ramText").textContent = `${fmt(ram.usedGb, 1)} / ${fmt(ram.totalGb, 1)} GB`;
    $("ramBar").style.width = `${clamp(ram.percent || 0, 0, 100).toFixed(1)}%`;
    const ramPill = $("ramPressurePill");
    ramPill.classList.remove("cool", "warm", "hot", "muted");
    if (ram.percent >= 85) { ramPill.textContent = "Alta"; ramPill.classList.add("hot"); }
    else if (ram.percent >= 65) { ramPill.textContent = "Moderada"; ramPill.classList.add("warm"); }
    else { ramPill.textContent = "Normal"; ramPill.classList.add("cool"); }

    // 4. Storage
    setGauge("disk", storage.percent, "amber");
    $("diskPct").textContent = fmt(storage.percent, 0, "%");
    $("diskDrivePill").textContent = storage.drive || "C:";
    $("diskTitle").textContent = `Unidad del Sistema (${storage.drive || "C:"})`;
    const freeGb = Math.max(0, (storage.totalGb || 0) - (storage.usedGb || 0));
    $("diskText").textContent = `${freeGb.toFixed(0)} GB libres / ${fmt(storage.totalGb, 0, " GB")}`;
    $("diskBar").style.width = `${clamp(storage.percent || 0, 0, 100).toFixed(1)}%`;

    // 5. Historiales 60s
    const cUsage = clamp(cpu.usagePercent ?? 0, 0, 100);
    const gUsage = gpu.usagePercent == null ? null : clamp(gpu.usagePercent, 0, 100);
    const cTemp = cpu.temperatureC != null && cpu.temperatureC > 0 ? cpu.temperatureC : null;
    const gTemp = gpu.temperatureC != null && gpu.temperatureC > 0 ? gpu.temperatureC : null;
    const rPct = clamp(ram.percent ?? 0, 0, 100);

    pushHist(state.cpuUsage, cUsage);
    pushHist(state.gpuUsage, gUsage);
    pushHist(state.cpuTemp, cTemp);
    pushHist(state.gpuTemp, gTemp);
    pushHist(state.ramPct, rPct);
    pushHist(state.ramGb, ram.usedGb ?? null);

    if (cUsage > state.peakCpu) state.peakCpu = cUsage;
    if (gUsage != null && gUsage > state.peakGpu) state.peakGpu = gUsage;
    if (cTemp != null && cTemp > state.peakCpuTemp) state.peakCpuTemp = cTemp;
    if (gTemp != null && gTemp > state.peakGpuTemp) state.peakGpuTemp = gTemp;
    if (rPct > state.peakRam) state.peakRam = rPct;

    $("chartLastTick").textContent = `Último tick: ${new Date(t.timestamp).toLocaleTimeString("es-AR", { hour12: false })}`;
    syncChartLegend();
    drawChart();

    // 6. Gaming Readiness Hero
    updateReadiness(cpu, gpu, ram);

    // 7. Logs inteligentes
    if (!state.firstTelemetry) {
      state.firstTelemetry = true;
      addLog("TEL", `Telemetría activa (${rawSource}) · Elevado: ${t.isElevated ? "Sí" : "No"}`);
      addLog("SYS", `Hardware: ${cpu.name} | ${gpu.name} | RAM ${fmt(ram.totalGb, 1, " GB")}`);
      if (t.notice) addLog("WARN", t.notice);
    }
    const maxTemp = Math.max(cTemp || 0, gTemp || 0);
    if (maxTemp >= 82 && Date.now() - state.lastHighTempLog > 15000) {
      state.lastHighTempLog = Date.now();
      addLog("WARN", `Alerta térmica detectada (${Math.round(maxTemp)} °C). Revisá ventilación o perfil.`);
    }
  }

  function pushHist(arr, val) { arr.push(val); arr.shift(); }

  // ==================== GAMING READINESS (LED BAR + HALO) ====================
  function updateReadiness(cpu, gpu, ram) {
    let score = 100;
    const cpuLoad = cpu.usagePercent || 0;
    const gpuLoad = gpu.usagePercent || 0;
    const ramPct = ram.percent || 0;
    const maxTemp = Math.max(cpu.temperatureC || 0, gpu.temperatureC || 0);

    if (cpuLoad > 40) score -= (cpuLoad - 40) * 0.45;
    if (gpuLoad > 50) score -= (gpuLoad - 50) * 0.35;
    if (ramPct > 65) score -= (ramPct - 65) * 0.7;
    if (maxTemp > 70) score -= (maxTemp - 70) * 1.4;
    score = Math.round(clamp(score, 10, 100));

    const badge = $("readyBadge");
    const numEl = $("readyScore");
    const halo = $("scoreHalo");
    badge.classList.remove("warn", "hot");

    let ledMode = "cool";
    if (score >= 80) {
      badge.textContent = "Óptimo para jugar";
      numEl.style.color = "var(--emerald)";
      halo.style.background = "var(--emerald)";
      $("readyHint").textContent = "Temperaturas y recursos con amplio margen.";
    } else if (score >= 55) {
      badge.textContent = "Carga moderada";
      badge.classList.add("warn");
      numEl.style.color = "var(--amber)";
      halo.style.background = "var(--amber)";
      ledMode = "warm";
      $("readyHint").textContent = "Hay procesos activos consumiendo recursos.";
    } else {
      badge.textContent = maxTemp >= 80 ? "Térmica elevada" : "Carga alta";
      badge.classList.add("hot");
      numEl.style.color = "var(--red)";
      halo.style.background = "var(--red)";
      ledMode = "hot";
      $("readyHint").textContent = "Conviene cerrar apps o revisar refrigeración.";
    }

    numEl.textContent = score;

    // Actualizar 20 segmentos LED
    const lit = Math.round((score / 100) * LED_COUNT);
    const segs = $("ledBar").children;
    for (let i = 0; i < segs.length; i++) {
      if (i < lit) {
        const segClass = ledMode === "cool" ? (i < 8 ? "cyan" : "cool") : ledMode;
        segs[i].className = `led-seg on ${segClass}`;
      } else {
        segs[i].className = "led-seg";
      }
    }

    $("rmThermal").textContent = maxTemp > 0
      ? (maxTemp < 68 ? `Óptima (${Math.round(maxTemp)}°C)` : maxTemp < 80 ? `Tibia (${Math.round(maxTemp)}°C)` : `Alta (${Math.round(maxTemp)}°C)`)
      : "Sensores N/D";
    $("rmLoad").textContent = `${Math.round(100 - Math.max(cpuLoad, gpuLoad))}% libre`;
    $("rmMem").textContent = `${Math.round(100 - ramPct)}% disponible`;
  }

  // ==================== GRÁFICO CANVAS 2D PRO + CROSSHAIR ====================
  function getChartConfig() {
    if (state.chartMode === "temp") {
      return {
        min: 20, max: 100, unit: "°C",
        a: { label: "CPU", data: state.cpuTemp, color: "#38bdf8", top: "rgba(56,189,248,0.34)", bot: "rgba(56,189,248,0.01)", dot: "cyan", peak: state.peakCpuTemp },
        b: { label: "GPU", data: state.gpuTemp, color: "#10b981", top: "rgba(16,185,129,0.32)", bot: "rgba(16,185,129,0.01)", dot: "emerald", peak: state.peakGpuTemp },
      };
    }
    if (state.chartMode === "ram") {
      return {
        min: 0, max: 100, unit: "%",
        a: { label: "RAM", data: state.ramPct, color: "#818cf8", top: "rgba(129,140,248,0.38)", bot: "rgba(129,140,248,0.01)", dot: "violet", peak: state.peakRam },
        b: null,
      };
    }
    return {
      min: 0, max: 100, unit: "%",
      a: { label: "CPU", data: state.cpuUsage, color: "#38bdf8", top: "rgba(56,189,248,0.34)", bot: "rgba(56,189,248,0.01)", dot: "cyan", peak: state.peakCpu },
      b: { label: "GPU", data: state.gpuUsage, color: "#10b981", top: "rgba(16,185,129,0.30)", bot: "rgba(16,185,129,0.01)", dot: "emerald", peak: state.peakGpu },
    };
  }

  function syncChartLegend() {
    const cfg = getChartConfig();
    const lastA = cfg.a.data[HISTORY_LEN - 1];
    $("dotSeriesA").className = `mini-dot ${cfg.a.dot}`;
    $("labelSeriesA").textContent = cfg.a.label;
    $("valSeriesA").textContent = lastA == null ? "N/D" : `${Math.round(lastA)}${cfg.unit}`;
    $("chartPeakA").textContent = `Pico ${cfg.a.label}: ${cfg.a.peak > 0 ? Math.round(cfg.a.peak) + cfg.unit : "N/D"}`;

    const btnB = $("toggleSeriesB");
    if (cfg.b) {
      btnB.hidden = false;
      const lastB = cfg.b.data[HISTORY_LEN - 1];
      $("dotSeriesB").className = `mini-dot ${cfg.b.dot}`;
      $("labelSeriesB").textContent = cfg.b.label;
      $("valSeriesB").textContent = lastB == null ? "N/D" : `${Math.round(lastB)}${cfg.unit}`;
      $("chartPeakB").textContent = `Pico ${cfg.b.label}: ${cfg.b.peak > 0 ? Math.round(cfg.b.peak) + cfg.unit : "N/D"}`;
    } else {
      btnB.hidden = true;
      const lastGb = state.ramGb[HISTORY_LEN - 1];
      $("chartPeakB").textContent = lastGb != null ? `Actual: ${lastGb.toFixed(1)} GB` : "";
    }
  }

  let chartMetrics = { padL: 38, padR: 12, padT: 12, padB: 18, plotW: 1, plotH: 1 };

  function drawChart() {
    const canvas = $("telemetryChart");
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    if (rect.width < 10 || rect.height < 10) return;

    const dpr = window.devicePixelRatio || 1;
    const w = Math.round(rect.width);
    const h = Math.round(rect.height);
    if (canvas.width !== w * dpr || canvas.height !== h * dpr) {
      canvas.width = w * dpr;
      canvas.height = h * dpr;
    }

    const ctx = canvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);

    const padL = 38, padR = 12, padT = 12, padB = 18;
    const plotW = w - padL - padR;
    const plotH = h - padT - padB;
    chartMetrics = { padL, padR, padT, padB, plotW, plotH };

    const cfg = getChartConfig();
    const yFor = (val) => padT + plotH - (clamp((val - cfg.min) / (cfg.max - cfg.min), 0, 1)) * plotH;
    const xFor = (idx) => padL + (idx / (HISTORY_LEN - 1)) * plotW;

    // Grilla horizontal
    ctx.font = '10px "JetBrains Mono", monospace';
    ctx.textAlign = "right";
    ctx.textBaseline = "middle";
    for (let i = 0; i <= 4; i++) {
      const val = cfg.min + ((cfg.max - cfg.min) * i) / 4;
      const y = yFor(val);
      ctx.strokeStyle = i === 0 ? "rgba(255,255,255,0.12)" : "rgba(255,255,255,0.045)";
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(padL, y);
      ctx.lineTo(w - padR, y);
      ctx.stroke();

      ctx.fillStyle = "#64748b";
      ctx.fillText(`${Math.round(val)}${cfg.unit}`, padL - 6, y);
    }

    // Línea de referencia 80°C en modo temperatura
    if (state.chartMode === "temp") {
      const y80 = yFor(80);
      ctx.save();
      ctx.setLineDash([4, 4]);
      ctx.strokeStyle = "rgba(239, 68, 68, 0.35)";
      ctx.beginPath();
      ctx.moveTo(padL, y80);
      ctx.lineTo(w - padR, y80);
      ctx.stroke();
      ctx.restore();
    }

    const drawSeries = (s) => {
      const pts = [];
      for (let i = 0; i < s.data.length; i++) {
        if (s.data[i] == null) continue;
        pts.push({ x: xFor(i), y: yFor(s.data[i]) });
      }
      if (pts.length < 2) return;

      const buildPath = () => {
        ctx.beginPath();
        ctx.moveTo(pts[0].x, pts[0].y);
        for (let i = 1; i < pts.length; i++) {
          const xc = (pts[i - 1].x + pts[i].x) * 0.5;
          const yc = (pts[i - 1].y + pts[i].y) * 0.5;
          ctx.quadraticCurveTo(pts[i - 1].x, pts[i - 1].y, xc, yc);
        }
        const last = pts[pts.length - 1];
        ctx.lineTo(last.x, last.y);
      };

      // Relleno gradiente luminoso
      buildPath();
      const last = pts[pts.length - 1];
      ctx.lineTo(last.x, padT + plotH);
      ctx.lineTo(pts[0].x, padT + plotH);
      ctx.closePath();
      const grad = ctx.createLinearGradient(0, padT, 0, padT + plotH);
      grad.addColorStop(0, s.top);
      grad.addColorStop(0.75, s.bot);
      grad.addColorStop(1, "rgba(0,0,0,0)");
      ctx.fillStyle = grad;
      ctx.fill();

      // Trazo con brillo suave
      buildPath();
      ctx.strokeStyle = s.color;
      ctx.lineWidth = 2.1;
      ctx.lineJoin = "round";
      ctx.shadowColor = s.color;
      ctx.shadowBlur = 8;
      ctx.stroke();
      ctx.shadowBlur = 0;

      // Punto final vivo
      ctx.beginPath();
      ctx.arc(last.x, last.y, 3.6, 0, Math.PI * 2);
      ctx.fillStyle = "#ffffff";
      ctx.fill();
      ctx.lineWidth = 2;
      ctx.strokeStyle = s.color;
      ctx.shadowColor = s.color;
      ctx.shadowBlur = 10;
      ctx.stroke();
      ctx.shadowBlur = 0;
    };

    if (cfg.b && state.showB) drawSeries(cfg.b);
    if (state.showA) drawSeries(cfg.a);

    // Crosshair vertical interactivo
    if (state.hoverIdx >= 0 && state.hoverIdx < HISTORY_LEN) {
      const hx = xFor(state.hoverIdx);
      ctx.save();
      ctx.setLineDash([3, 3]);
      ctx.strokeStyle = "rgba(255, 255, 255, 0.28)";
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(hx, padT);
      ctx.lineTo(hx, padT + plotH);
      ctx.stroke();
      ctx.restore();

      const drawHoverDot = (s) => {
        const val = s.data[state.hoverIdx];
        if (val == null) return;
        const hy = yFor(val);
        ctx.beginPath();
        ctx.arc(hx, hy, 4.5, 0, Math.PI * 2);
        ctx.fillStyle = "#06080d";
        ctx.fill();
        ctx.lineWidth = 2.2;
        ctx.strokeStyle = s.color;
        ctx.shadowColor = s.color;
        ctx.shadowBlur = 10;
        ctx.stroke();
        ctx.shadowBlur = 0;
      };
      if (cfg.b && state.showB) drawHoverDot(cfg.b);
      if (state.showA) drawHoverDot(cfg.a);
      updateTooltip(hx, w, cfg);
    }
  }

  function updateTooltip(hx, canvasW, cfg) {
    const tip = $("chartTooltip");
    const idx = state.hoverIdx;
    const secsAgo = HISTORY_LEN - 1 - idx;
    const timeLabel = secsAgo === 0 ? "AHORA" : `HACE ${secsAgo}s`;

    let rows = `<div class="tt-time">${timeLabel}</div>`;
    if (state.showA) {
      const v = cfg.a.data[idx];
      const extra = state.chartMode === "ram" && state.ramGb[idx] != null ? ` (${state.ramGb[idx].toFixed(1)} GB)` : "";
      rows += `<div class="tt-row"><span class="tt-label"><span class="mini-dot ${cfg.a.dot}"></span>${cfg.a.label}</span><span class="tt-val">${v == null ? "N/D" : Math.round(v) + cfg.unit + extra}</span></div>`;
    }
    if (cfg.b && state.showB) {
      const v = cfg.b.data[idx];
      rows += `<div class="tt-row"><span class="tt-label"><span class="mini-dot ${cfg.b.dot}"></span>${cfg.b.label}</span><span class="tt-val">${v == null ? "N/D" : Math.round(v) + cfg.unit}</span></div>`;
    }
    tip.innerHTML = rows;
    tip.hidden = false;

    const tipW = tip.offsetWidth || 130;
    const left = hx + 14 + tipW > canvasW ? Math.max(8, hx - tipW - 14) : hx + 14;
    tip.style.left = `${left}px`;
  }

  // Interacción del crosshair en el canvas
  const canvasWrap = $("canvasWrap");
  canvasWrap.addEventListener("pointermove", (e) => {
    const r = canvasWrap.getBoundingClientRect();
    const x = e.clientX - r.left;
    const rel = clamp((x - chartMetrics.padL) / chartMetrics.plotW, 0, 1);
    const idx = Math.round(rel * (HISTORY_LEN - 1));
    if (idx !== state.hoverIdx) {
      state.hoverIdx = idx;
      drawChart();
    }
  });
  canvasWrap.addEventListener("pointerleave", () => {
    state.hoverIdx = -1;
    $("chartTooltip").hidden = true;
    drawChart();
  });

  // Selector Uso (%) / Temp (°C) / RAM (%)
  $("chartModeSwitch").addEventListener("click", (e) => {
    const btn = e.target.closest(".mode-btn");
    if (!btn) return;
    state.chartMode = btn.dataset.mode;
    $("chartModeSwitch").querySelectorAll(".mode-btn").forEach((b) => b.classList.toggle("active", b === btn));
    syncChartLegend();
    drawChart();
  });

  // Leyenda interactiva
  $("toggleSeriesA").addEventListener("click", (e) => {
    state.showA = !state.showA;
    e.currentTarget.classList.toggle("active", state.showA);
    drawChart();
  });
  $("toggleSeriesB").addEventListener("click", (e) => {
    state.showB = !state.showB;
    e.currentTarget.classList.toggle("active", state.showB);
    drawChart();
  });

  // ==================== PUENTE C# -> JS ====================
  const handlers = {
    "telemetry": renderTelemetry,
    "telemetry_error": (message) => {
      const pill = $("livePill");
      pill.classList.remove("warn");
      pill.classList.add("err");
      $("livePillText").textContent = "ERROR";
      addLog("ERR", String(message));
    },
    "system.info": (info) => {
      $("sbMachine").textContent = info.machineName || "PC Local";
      $("sbMeta").textContent = `${info.architecture || "x64"} · ${info.logicalCores || "?"} hilos`;
      addLog("SYS", `Sistema: ${info.machineName} · ${info.windowsVersion} (${info.logicalCores} hilos)`);
    },
    "window.state": (p) => {
      $("iconMax").style.display = p.maximized ? "none" : "";
      $("iconRestore").style.display = p.maximized ? "" : "none";
      $("btnMax").title = p.maximized ? "Restaurar" : "Maximizar";
    },
    "hud_state": (p) => {
      const btn = $("btnToggleHud");
      if (!btn) return;
      btn.classList.toggle("active", !!p.visible);
      btn.title = p.visible
        ? (p.clickThrough ? "Mini-HUD activo (Click-Through bloqueado)" : "Ocultar Mini-HUD flotante")
        : "Mostrar Mini-HUD flotante";
    },
    "update_available": (u) => {
      const banner = $("updateBanner");
      if (!banner || !u || !u.latestVersion) return;
      $("ubText").textContent = `Nueva versión v${u.latestVersion} disponible (actual: v${u.currentVersion})`;
      banner.dataset.url = u.downloadUrl || "";
      banner.hidden = false;
      addLog("SYS", `Actualización disponible: PulseCore v${u.latestVersion} (${u.downloadUrl})`);
    },
  };

  if (host) {
    host.addEventListener("message", (e) => {
      const msg = e.data;
      const fn = msg && handlers[msg.type];
      if (!fn) return;
      const arg = msg.type === "telemetry" ? msg.data
        : msg.type === "telemetry_error" ? msg.message
        : (msg.payload || {});
      try { fn(arg); }
      catch (err) { addLog("ERR", `Error UI (${msg.type}): ${err && err.message || err}`); }
    });
  } else {
    addLog("WARN", "WebView2 no detectado: ejecutando en modo vista previa.");
  }

  // ==================== NAVEGACIÓN & PROXIMITY GLOW ====================
  function switchView(viewName) {
    document.querySelectorAll(".nav-item").forEach((b) =>
      b.classList.toggle("active", b.dataset.view === viewName));
    document.querySelectorAll(".view").forEach((v) =>
      v.classList.toggle("active", v.id === `view-${viewName}`));
    if (viewName === "dashboard") requestAnimationFrame(drawChart);
  }

  document.querySelectorAll(".nav-item").forEach((btn) =>
    btn.addEventListener("click", () => switchView(btn.dataset.view)));
  document.querySelectorAll("[data-jump]").forEach((btn) =>
    btn.addEventListener("click", () => switchView(btn.dataset.jump)));

  // Mouse-tracking radial border glow por proximidad en todas las tarjetas visibles
  const viewport = $("viewport");
  let glowRaf = 0;
  let lastPointer = null;
  viewport.addEventListener("pointermove", (e) => {
    viewport.classList.add("pointer-active");
    lastPointer = { x: e.clientX, y: e.clientY };
    if (glowRaf) return;
    glowRaf = requestAnimationFrame(() => {
      glowRaf = 0;
      if (!lastPointer) return;
      const cards = document.querySelectorAll(".view.active .glow-card");
      for (const card of cards) {
        const r = card.getBoundingClientRect();
        card.style.setProperty("--mx", `${lastPointer.x - r.left}px`);
        card.style.setProperty("--my", `${lastPointer.y - r.top}px`);
      }
    });
  });
  viewport.addEventListener("pointerleave", () => viewport.classList.remove("pointer-active"));

  // Controles de ventana
  const wireDrag = (el) => {
    el.addEventListener("mousedown", (e) => {
      if (e.button === 0 && e.detail === 1) send("window.drag");
    });
    el.addEventListener("dblclick", () => send("window.toggleMaximize"));
  };
  wireDrag($("dragArea"));
  wireDrag($("dragLeft"));
  $("btnMin").addEventListener("click", () => send("window.minimize"));
  $("btnMax").addEventListener("click", () => send("window.toggleMaximize"));
  $("btnClose").addEventListener("click", () => send("window.close"));
  $("btnToggleHud")?.addEventListener("click", () => send("toggle_hud"));
  $("btnDownloadUpdate")?.addEventListener("click", () => {
    const url = $("updateBanner")?.dataset.url;
    if (url) send("open_external_url", { url });
  });
  $("btnDismissUpdate")?.addEventListener("click", () => {
    if ($("updateBanner")) $("updateBanner").hidden = true;
  });

  $("logFilters").addEventListener("click", (e) => {
    const btn = e.target.closest(".filter-btn");
    if (!btn) return;
    state.logFilter = btn.dataset.filter;
    $("logFilters").querySelectorAll(".filter-btn").forEach((b) => b.classList.toggle("active", b === btn));
    renderLogs();
  });
  $("btnClearLogs").addEventListener("click", () => {
    state.logs = [];
    $("logCountBadge").textContent = "0";
    renderLogs();
  });

  window.addEventListener("resize", () => requestAnimationFrame(drawChart));

  window.PulseCore = { addLog, send, getLogs: () => state.logs.map((l) => ({ ts: l.ts, level: l.level, message: l.message })) };

  // Boot
  syncChartLegend();
  addLog("SYS", "PulseCore UI v1.7.0 inicializada. Conectando puente WebView2…");
  send("app.ready");
})();
