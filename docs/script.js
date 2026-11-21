(() => {
  "use strict";

  const FALLBACK_URL = "https://github.com/alexlemos2217/PulseCore/releases/latest";
  const API_URL = "https://api.github.com/repos/alexlemos2217/PulseCore/releases/latest";
  const $ = (id) => document.getElementById(id);

  // ==================== 1. GITHUB RELEASES + CHANGELOG ====================
  const releaseState = {
    tag: "v1.8.0",
    body: "• Mini-HUD con FPS y 1% Low FPS (ETW pasivo)\n• Speedtest integrado (Cloudflare)\n• Perfiles 1-clic (Competitivo / Trabajo) + Exportar/Importar\n• Mantenimiento Semanal Automático\n• Buscador global de winget\n• Suite Windows (Debloat, Inicio, SFC/DISM)",
    htmlUrl: FALLBACK_URL,
  };

  async function loadLatestRelease() {
    try {
      const resp = await fetch(API_URL, { headers: { Accept: "application/vnd.github+json" } });
      if (!resp.ok) return;
      const data = await resp.json();

      const tag = data.tag_name || "v1.8.0";
      releaseState.tag = tag;
      releaseState.htmlUrl = data.html_url || FALLBACK_URL;
      if (data.body && data.body.trim()) releaseState.body = data.body.trim();

      let downloadUrl = releaseState.htmlUrl;
      let sizeTag = "";

      if (Array.isArray(data.assets)) {
        const exe = data.assets.find((a) => a.name && a.name.toLowerCase().endsWith(".exe"));
        if (exe && exe.browser_download_url) {
          downloadUrl = exe.browser_download_url;
          if (exe.size) sizeTag = ` (${(exe.size / (1024 * 1024)).toFixed(1)} MB)`;
        }
      }

      document.querySelectorAll("[data-version-badge]").forEach((el) => { el.textContent = tag; });
      document.querySelectorAll("[data-download-btn]").forEach((btn) => { btn.href = downloadUrl; });
      if ($("releasePillText")) $("releasePillText").textContent = `${tag} Disponible — Instalador Oficial Windows`;
      if ($("heroDownloadLabel")) $("heroDownloadLabel").textContent = `⬇ Descargar PulseCore ${tag}${sizeTag}`;
    } catch (_) {
      // Fallback silencioso
    }
  }

  // Modal Changelog
  const modal = $("changelogModal");
  $("btnOpenChangelog")?.addEventListener("click", () => {
    $("clTag").textContent = releaseState.tag;
    $("clTitle").textContent = `PulseCore ${releaseState.tag} — Changelog`;
    $("clBody").textContent = releaseState.body;
    $("clReleaseLink").href = releaseState.htmlUrl;
    modal.hidden = false;
  });
  $("btnCloseChangelog")?.addEventListener("click", () => { modal.hidden = true; });
  modal?.addEventListener("click", (e) => { if (e.target === modal) modal.hidden = true; });

  // ==================== 2. SIMULADOR INTERACTIVO EN VIVO ====================
  const ARC_LEN = 2 * Math.PI * 40 * (240 / 360); // 167.55
  const LED_COUNT = 16;

  const PRESETS = {
    normal: {
      fps: 116, low: 71, cpu: 48, cpuTemp: 66, cpuSub: "4.3 GHz · Equilibrado",
      gpu: 78, gpuTemp: 67, gpuSub: "VRAM 5.4 / 8 GB",
      ram: 71, ramPill: "Moderada", ramSub: "11.4 / 16 GB",
      disk: 62, diskSub: "184 GB libres",
      ping: 34, score: 72, badge: "Carga moderada",
      profile: "💼 Modo Normal",
      delta: "Modo Normal · 1% Low inestable",
      hint: "Activá ⚡ Simular PulseCore Boost para ver el efecto en vivo",
    },
    boost: {
      fps: 144, low: 118, cpu: 32, cpuTemp: 57, cpuSub: "4.7 GHz · Alto Rendimiento",
      gpu: 82, gpuTemp: 58, gpuSub: "VRAM 4.6 / 8 GB",
      ram: 44, ramPill: "Óptima", ramSub: "7.1 / 16 GB (+4.3 GB libres)",
      disk: 59, diskSub: "191 GB libres (+7 GB)",
      ping: 18, score: 97, badge: "Óptimo para jugar",
      profile: "⚡ Competitivo Activo",
      delta: "PulseCore Boost · +28 FPS · -16 ms ping · +4.3 GB RAM",
      hint: "Alto Rendimiento + EmptyWorkingSet + DNS Cloudflare 1.1.1.1",
    },
  };

  let simMode = "normal";
  const histFps = new Array(48).fill(114);
  const histLow = new Array(48).fill(70);

  // Construir barra LED
  const ledBox = $("simLeds");
  if (ledBox) {
    for (let i = 0; i < LED_COUNT; i++) ledBox.appendChild(document.createElement("i"));
  }

  function setArc(id, pct) {
    const el = $(id);
    if (!el) return;
    el.style.strokeDashoffset = (ARC_LEN * (1 - pct / 100)).toFixed(2);
  }

  function applySim(mode) {
    simMode = mode;
    const p = PRESETS[mode];
    const boosted = mode === "boost";

    $("btnSimNormal").classList.toggle("active", !boosted);
    $("btnSimBoost").classList.toggle("active", boosted);
    $("simHud").classList.toggle("boosted", boosted);
    $("simProfileTag").classList.toggle("boosted", boosted);
    $("simProfileTag").textContent = p.profile;

    $("simHudFps").textContent = `${p.fps} FPS`;
    $("simHudLow").textContent = `· 1% ${p.low}`;
    $("simHudCpu").textContent = `${p.cpu}%`;
    $("simHudGpu").textContent = `${p.gpuTemp}°C`;
    $("simHudPing").textContent = `${p.ping} ms`;

    setArc("sgCpuArc", p.cpu); $("sgCpuVal").textContent = `${p.cpu}%`; $("sgCpuTemp").textContent = `${p.cpuTemp} °C`; $("sgCpuSub").textContent = p.cpuSub;
    setArc("sgGpuArc", p.gpu); $("sgGpuVal").textContent = `${p.gpu}%`; $("sgGpuTemp").textContent = `${p.gpuTemp} °C`; $("sgGpuSub").textContent = p.gpuSub;
    setArc("sgRamArc", p.ram); $("sgRamVal").textContent = `${p.ram}%`; $("sgRamPill").textContent = p.ramPill; $("sgRamSub").textContent = p.ramSub;
    setArc("sgDiskArc", p.disk); $("sgDiskVal").textContent = `${p.disk}%`; $("sgDiskSub").textContent = p.diskSub;

    $("simScore").textContent = p.score;
    $("simScore").classList.toggle("boosted", boosted);
    $("simScoreBadge").textContent = p.badge;
    $("simScoreBadge").classList.toggle("boosted", boosted);
    $("simDelta").textContent = p.delta;
    $("simHint").textContent = p.hint;

    const lit = Math.round((p.score / 100) * LED_COUNT);
    [...ledBox.children].forEach((seg, i) => {
      seg.className = i < lit ? `on ${boosted ? "cool" : "warm"}` : "";
    });
  }

  $("btnSimNormal")?.addEventListener("click", () => applySim("normal"));
  $("btnSimBoost")?.addEventListener("click", () => applySim("boost"));

  // Sparkline Canvas 2D vivo
  function drawSimChart() {
    const canvas = $("simChart");
    if (!canvas) return;
    const r = canvas.getBoundingClientRect();
    if (r.width < 20) return;
    const dpr = window.devicePixelRatio || 1;
    const w = Math.round(r.width), h = Math.round(r.height);
    if (canvas.width !== w * dpr || canvas.height !== h * dpr) {
      canvas.width = w * dpr; canvas.height = h * dpr;
    }
    const ctx = canvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);

    const drawLine = (arr, color, fillTop) => {
      ctx.beginPath();
      for (let i = 0; i < arr.length; i++) {
        const x = (i / (arr.length - 1)) * w;
        const y = h - ((arr[i] - 40) / 130) * (h - 14) - 6;
        if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
      }
      ctx.strokeStyle = color;
      ctx.lineWidth = 2;
      ctx.stroke();

      if (fillTop) {
        ctx.lineTo(w, h); ctx.lineTo(0, h); ctx.closePath();
        const g = ctx.createLinearGradient(0, 0, 0, h);
        g.addColorStop(0, fillTop); g.addColorStop(1, "rgba(0,0,0,0)");
        ctx.fillStyle = g; ctx.fill();
      }
    };

    drawLine(histLow, "#38bdf8", "rgba(56,189,248,0.16)");
    drawLine(histFps, "#10b981", "rgba(16,185,129,0.22)");
  }

  setInterval(() => {
    const p = PRESETS[simMode];
    const jitterFps = simMode === "boost" ? (Math.random() - 0.5) * 4 : (Math.random() - 0.5) * 16;
    const jitterLow = simMode === "boost" ? (Math.random() - 0.5) * 5 : (Math.random() - 0.5) * 22;
    histFps.push(Math.max(50, Math.min(165, p.fps + jitterFps)));
    histFps.shift();
    histLow.push(Math.max(45, Math.min(140, p.low + jitterLow)));
    histLow.shift();
    drawSimChart();
  }, 240);

  // Detectar si existe assets/screenshot-dashboard.png para habilitar tab "Captura Real"
  const realShot = $("realScreenshot");
  if (realShot) {
    realShot.addEventListener("load", () => {
      if (realShot.naturalWidth > 0) $("viewModeTabs").hidden = false;
    });
  }
  $("viewModeTabs")?.addEventListener("click", (e) => {
    const btn = e.target.closest("[data-stage]");
    if (!btn) return;
    const isShot = btn.dataset.stage === "screenshot";
    $("viewModeTabs").querySelectorAll(".sim-btn").forEach((b) => b.classList.toggle("active", b === btn));
    $("interactiveStage").hidden = isShot;
    $("screenshotStage").hidden = !isShot;
  });

  // ==================== 3. SPOTLIGHT EN TARJETAS ====================
  let raf = 0, ptr = null;
  document.addEventListener("pointermove", (e) => {
    ptr = { x: e.clientX, y: e.clientY };
    if (raf) return;
    raf = requestAnimationFrame(() => {
      raf = 0;
      if (!ptr) return;
      document.querySelectorAll(".spotlight-card").forEach((c) => {
        const r = c.getBoundingClientRect();
        c.style.setProperty("--mouse-x", `${ptr.x - r.left}px`);
        c.style.setProperty("--mouse-y", `${ptr.y - r.top}px`);
      });
    });
  });

  // ==================== 4. SCROLL REVEAL + FAQ ====================
  const io = new IntersectionObserver((entries) => {
    entries.forEach((en) => {
      if (en.isIntersecting) {
        en.target.classList.add("in");
        io.unobserve(en.target);
      }
    });
  }, { threshold: 0.1 });
  document.querySelectorAll(".reveal").forEach((el) => io.observe(el));

  document.querySelectorAll(".faq-q").forEach((btn) => {
    btn.addEventListener("click", () => {
      const item = btn.closest(".faq-item");
      const open = item.classList.toggle("open");
      btn.setAttribute("aria-expanded", String(open));
    });
  });

  window.addEventListener("resize", () => requestAnimationFrame(drawSimChart));
  applySim("normal");
  drawSimChart();
  loadLatestRelease();
})();
