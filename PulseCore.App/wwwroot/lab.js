(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);
  const labView = $("view-lab");
  if (!labView) return;

  // ==================== 1. MOUSE POLLING RATE TEST ====================
  const pad = $("mousePad");
  const bigHz = $("pollBigHz");
  const curEl = $("pollCur");
  const peakEl = $("pollPeak");
  const avgEl = $("pollAvg");
  const tierEl = $("pollTier");
  const hintEl = $("mousePadHint");
  const pollCanvas = $("pollChart");

  const POLL_HIST = 50;
  const poll = {
    stamps: [],
    history: new Array(POLL_HIST).fill(0),
    peak: 0,
    samples: [],
    lastSampleMs: 0,
    active: false,
  };

  function classifyHz(hz) {
    if (hz >= 900) return { label: hz >= 1800 ? "2000+ Hz PRO" : "1000+ Hz GAMING", cls: "good" };
    if (hz >= 430) return { label: "500 Hz", cls: "cyan" };
    if (hz >= 210) return { label: "250 Hz", cls: "warn" };
    if (hz > 0) return { label: "125 Hz ESTÁNDAR", cls: "muted" };
    return { label: "Esperando…", cls: "muted" };
  }

  function updatePollUi(hz) {
    const rounded = Math.round(hz);
    bigHz.innerHTML = `${rounded}<span>Hz</span>`;
    curEl.textContent = `${rounded} Hz`;
    if (rounded > poll.peak) poll.peak = rounded;
    peakEl.textContent = `${poll.peak} Hz`;
    if (rounded > 10) {
      poll.samples.push(rounded);
      if (poll.samples.length > 80) poll.samples.shift();
      const avg = Math.round(poll.samples.reduce((a, b) => a + b, 0) / poll.samples.length);
      avgEl.textContent = `${avg} Hz`;
    }
    const t = classifyHz(poll.peak || rounded);
    tierEl.textContent = t.label;
    tierEl.dataset.tone = t.cls;
  }

  function drawPollChart() {
    if (!pollCanvas) return;
    const rect = pollCanvas.getBoundingClientRect();
    if (rect.width < 10 || rect.height < 10) return;
    const dpr = window.devicePixelRatio || 1;
    const w = Math.round(rect.width), h = Math.round(rect.height);
    if (pollCanvas.width !== w * dpr || pollCanvas.height !== h * dpr) {
      pollCanvas.width = w * dpr; pollCanvas.height = h * dpr;
    }
    const ctx = pollCanvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);

    const maxVal = Math.max(1000, poll.peak * 1.15);
    const padY = 8;
    const plotH = h - padY * 2;

    // Línea de referencia 1000 Hz
    const y1k = padY + plotH - (1000 / maxVal) * plotH;
    ctx.strokeStyle = "rgba(56,189,248,0.18)";
    ctx.setLineDash([3, 3]);
    ctx.beginPath(); ctx.moveTo(0, y1k); ctx.lineTo(w, y1k); ctx.stroke();
    ctx.setLineDash([]);

    ctx.beginPath();
    for (let i = 0; i < POLL_HIST; i++) {
      const x = (i / (POLL_HIST - 1)) * w;
      const y = padY + plotH - Math.min(1, poll.history[i] / maxVal) * plotH;
      if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    }
    ctx.strokeStyle = "#38bdf8";
    ctx.lineWidth = 2;
    ctx.stroke();

    ctx.lineTo(w, h); ctx.lineTo(0, h); ctx.closePath();
    const grad = ctx.createLinearGradient(0, 0, 0, h);
    grad.addColorStop(0, "rgba(56,189,248,0.3)");
    grad.addColorStop(1, "rgba(56,189,248,0)");
    ctx.fillStyle = grad;
    ctx.fill();
  }

  pad.addEventListener("pointermove", (e) => {
    const events = typeof e.getCoalescedEvents === "function" ? e.getCoalescedEvents() : [e];
    const now = performance.now();
    if (events.length === 0) {
      poll.stamps.push(now);
    } else {
      for (const ev of events) poll.stamps.push(ev.timeStamp || now);
    }

    // Ventana deslizante de 250ms para cálculo estable en tiempo real
    const cutoff = now - 250;
    while (poll.stamps.length && poll.stamps[0] < cutoff) poll.stamps.shift();

    const r = pad.getBoundingClientRect();
    pad.style.setProperty("--px", `${e.clientX - r.left}px`);
    pad.style.setProperty("--py", `${e.clientY - r.top}px`);
    pad.classList.add("tracking");
    if (hintEl) hintEl.style.opacity = "0";

    if (now - poll.lastSampleMs >= 60) {
      poll.lastSampleMs = now;
      const hz = poll.stamps.length * 4; // 250ms * 4 = 1s
      poll.history.push(hz);
      poll.history.shift();
      updatePollUi(hz);
      drawPollChart();
    }
  });

  pad.addEventListener("pointerleave", () => {
    pad.classList.remove("tracking");
  });

  $("btnResetPoll").addEventListener("click", () => {
    poll.stamps = [];
    poll.history.fill(0);
    poll.peak = 0;
    poll.samples = [];
    bigHz.innerHTML = `0<span>Hz</span>`;
    curEl.textContent = "0 Hz";
    peakEl.textContent = "0 Hz";
    avgEl.textContent = "0 Hz";
    tierEl.textContent = "Esperando…";
    tierEl.dataset.tone = "muted";
    if (hintEl) hintEl.style.opacity = "1";
    drawPollChart();
  });

  // ==================== 2. MONITOR REFRESH RATE & GHOSTING TEST ====================
  const ghostCanvas = $("ghostCanvas");
  const hzBadge = $("ghostHzBadge");
  const topHzPill = $("labRefreshPill");

  const SPEEDS = { slow: 480, medium: 960, fast: 1680 };
  const ghost = {
    running: true,
    speed: SPEEDS.medium,
    x: 0,
    lastTs: 0,
    fpsEma: 0,
    frames: 0,
    lastBadgeUpdate: 0,
    rafId: 0,
  };

  function drawLlamaEmblem(ctx, x, y) {
    ctx.save();
    ctx.translate(x, y);

    // Barras verticales de nitidez (para detectar ghosting / inverse ghosting)
    ctx.fillStyle = "rgba(255,255,255,0.85)";
    ctx.fillRect(-34, -18, 2, 36);
    ctx.fillStyle = "#38bdf8";
    ctx.fillRect(-29, -14, 2, 28);
    ctx.fillStyle = "#818cf8";
    ctx.fillRect(29, -14, 2, 28);
    ctx.fillStyle = "rgba(255,255,255,0.85)";
    ctx.fillRect(34, -18, 2, 36);

    // Silueta geométrica Cyber-Llama
    ctx.fillStyle = "#111827";
    ctx.strokeStyle = "#94a3b8";
    ctx.lineWidth = 1.4;
    ctx.beginPath();
    // Cabeza + orejas
    ctx.moveTo(-16, -8);
    ctx.lineTo(-21, -24);
    ctx.lineTo(-10, -16);
    ctx.lineTo(10, -16);
    ctx.lineTo(21, -24);
    ctx.lineTo(16, -8);
    ctx.lineTo(19, 8);
    ctx.lineTo(10, 18);
    ctx.lineTo(-10, 18);
    ctx.lineTo(-19, 8);
    ctx.closePath();
    ctx.fill();
    ctx.stroke();

    // Visor digital neón
    ctx.fillStyle = "#050811";
    ctx.strokeStyle = "#38bdf8";
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.roundRect(-14, -7, 28, 13, 4);
    ctx.fill();
    ctx.stroke();

    // Ojos cian brillantes
    ctx.fillStyle = "#38bdf8";
    ctx.shadowColor = "#38bdf8";
    ctx.shadowBlur = 8;
    ctx.fillRect(-9, -3, 6, 4.5);
    ctx.fillRect(3, -3, 6, 4.5);
    ctx.shadowBlur = 0;

    ctx.restore();
  }

  function renderGhostFrame(ts) {
    ghost.rafId = requestAnimationFrame(renderGhostFrame);
    if (!labView.classList.contains("active")) {
      ghost.lastTs = 0;
      return;
    }

    if (ghost.lastTs > 0) {
      const dt = (ts - ghost.lastTs) / 1000;
      if (dt > 0.001 && dt < 0.25) {
        const instHz = 1 / dt;
        ghost.fpsEma = ghost.fpsEma === 0 ? instHz : ghost.fpsEma * 0.92 + instHz * 0.08;
        ghost.frames++;
        if (ts - ghost.lastBadgeUpdate > 400 && ghost.frames > 10) {
          ghost.lastBadgeUpdate = ts;
          const hz = Math.round(ghost.fpsEma);
          hzBadge.textContent = `${hz} Hz detectados`;
          topHzPill.textContent = `Monitor: ${hz} Hz`;
        }
        if (ghost.running) ghost.x += ghost.speed * dt;
      }
    }
    ghost.lastTs = ts;

    const rect = ghostCanvas.getBoundingClientRect();
    if (rect.width < 20 || rect.height < 20) return;
    const dpr = window.devicePixelRatio || 1;
    const w = Math.round(rect.width), h = Math.round(rect.height);
    if (ghostCanvas.width !== w * dpr || ghostCanvas.height !== h * dpr) {
      ghostCanvas.width = w * dpr; ghostCanvas.height = h * dpr;
    }

    const ctx = ghostCanvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

    // 3 carriles con distintos contrastes de fondo (oscuro, medio, slate) para evaluar ghosting
    const laneH = h / 3;
    const lanes = ["#06080d", "#0e1525", "#182235"];
    for (let i = 0; i < 3; i++) {
      ctx.fillStyle = lanes[i];
      ctx.fillRect(0, i * laneH, w, laneH);
      if (i > 0) {
        ctx.strokeStyle = "rgba(255,255,255,0.06)";
        ctx.beginPath(); ctx.moveTo(0, i * laneH); ctx.lineTo(w, i * laneH); ctx.stroke();
      }
    }

    // Grilla vertical sutil de referencia
    ctx.strokeStyle = "rgba(255,255,255,0.035)";
    ctx.lineWidth = 1;
    for (let gx = 0; gx < w; gx += 48) {
      ctx.beginPath(); ctx.moveTo(gx, 0); ctx.lineTo(gx, h); ctx.stroke();
    }

    // Dibujar objetos en bucle continuo en los 3 carriles
    const span = w + 120;
    const pos = ((ghost.x % span) + span) % span - 60;
    for (let i = 0; i < 3; i++) {
      drawLlamaEmblem(ctx, pos, laneH * i + laneH * 0.5);
    }
  }

  $("ghostSpeedSwitch").addEventListener("click", (e) => {
    const btn = e.target.closest(".mode-btn");
    if (!btn) return;
    ghost.speed = SPEEDS[btn.dataset.speed] || SPEEDS.medium;
    $("ghostSpeedSwitch").querySelectorAll(".mode-btn").forEach((b) => b.classList.toggle("active", b === btn));
  });

  $("btnToggleGhost").addEventListener("click", () => {
    ghost.running = !ghost.running;
    $("btnToggleGhost").textContent = ghost.running ? "Pausar" : "Reanudar";
  });

  window.addEventListener("resize", () => requestAnimationFrame(drawPollChart));
  document.querySelectorAll('[data-view="lab"]').forEach((b) =>
    b.addEventListener("click", () => requestAnimationFrame(drawPollChart)));

  requestAnimationFrame(renderGhostFrame);
})();
