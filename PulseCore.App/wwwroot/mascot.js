(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);
  const STORAGE_KEY = "pulsecore.mascot.hidden";

  const wrap = $("mascotWrap");
  const card = $("mascotCard");
  const stage = $("mascotStage");
  const bubble = $("mascotBubble");
  const statusEl = $("mascotStatus");
  const btnHide = $("btnMascotToggle");
  const btnShow = $("btnMascotRestore");
  if (!wrap || !stage) return;

  const state = {
    hidden: false,
    mode: "idle", // "idle" | "gaming" | "alert"
    gameName: null,
    lastTel: null,
    bubbleTimer: null,
    actionTimer: null,
    lastAlertSpoke: 0,
    tipIndex: 0,
  };

  // ==================== VISIBILIDAD (localStorage) ====================
  function setHidden(hidden) {
    state.hidden = !!hidden;
    card.hidden = state.hidden;
    btnShow.hidden = !state.hidden;
    if (state.hidden) hideBubble();
    try { localStorage.setItem(STORAGE_KEY, state.hidden ? "1" : "0"); } catch (_) {}
  }

  try {
    setHidden(localStorage.getItem(STORAGE_KEY) === "1");
  } catch (_) {
    setHidden(false);
  }

  btnHide.addEventListener("click", () => setHidden(true));
  btnShow.addEventListener("click", () => {
    setHidden(false);
    triggerAction("wink", 650);
    say("¡Volví al puesto táctico!", 3200);
  });

  // ==================== ESTADOS Y ANIMACIONES ====================
  function setMode(mode) {
    if (state.mode === mode) return;
    state.mode = mode;
    wrap.classList.remove("state-idle", "state-gaming", "state-alert");
    wrap.classList.add(`state-${mode}`);

    if (mode === "gaming") {
      statusEl.textContent = state.gameName ? `EN PARTIDA` : "MODO JUEGO";
    } else if (mode === "alert") {
      statusEl.textContent = "CARGA ALTA";
    } else {
      statusEl.textContent = "ÓPTIMO";
    }
  }

  function triggerAction(cls, durationMs = 1100) {
    if (state.hidden) return;
    clearTimeout(state.actionTimer);
    wrap.classList.remove("act-celebrate", "act-wink", "act-hop");
    void wrap.offsetWidth;
    wrap.classList.add(`act-${cls}`);
    state.actionTimer = setTimeout(() => {
      wrap.classList.remove("act-celebrate", "act-wink", "act-hop");
    }, durationMs);
  }

  function say(text, durationMs = 4000) {
    if (state.hidden || !text) return;
    clearTimeout(state.bubbleTimer);
    bubble.textContent = text;
    bubble.hidden = false;
    bubble.classList.remove("show");
    void bubble.offsetWidth;
    bubble.classList.add("show");
    state.bubbleTimer = setTimeout(hideBubble, durationMs);
  }

  function hideBubble() {
    clearTimeout(state.bubbleTimer);
    bubble.classList.remove("show");
    bubble.hidden = true;
  }

  function celebrate(phrase) {
    triggerAction("celebrate", 1250);
    say(phrase, 4000);
  }

  // ==================== EVALUACIÓN DE TELEMETRÍA ====================
  function evaluateState() {
    const t = state.lastTel;
    const gpuHot = t && t.gpu && t.gpu.temperatureC != null && t.gpu.temperatureC > 75;
    const cpuBusy = t && t.cpu && t.cpu.usagePercent != null && t.cpu.usagePercent > 85;

    if (gpuHot || cpuBusy) {
      setMode("alert");
      const now = Date.now();
      if (now - state.lastAlertSpoke > 25000) {
        state.lastAlertSpoke = now;
        const msg = gpuHot
          ? `¡GPU a ${Math.round(t.gpu.temperatureC)} °C! Revisá los ventiladores.`
          : `CPU al ${Math.round(t.cpu.usagePercent)}%. Cerrá apps pesadas.`;
        say(msg, 4200);
      }
      return;
    }

    if (state.gameName) {
      setMode("gaming");
      return;
    }
    setMode("idle");
  }

  // ==================== INTERACCIÓN AL CLIC ====================
  function buildLiveComment() {
    const t = state.lastTel;
    const comments = [];

    if (state.gameName) {
      comments.push(`Enfoque total en ${state.gameName}. ¡A por la victoria!`);
    }
    if (t && t.gpu && t.gpu.temperatureC != null) {
      const g = Math.round(t.gpu.temperatureC);
      comments.push(g > 75
        ? `GPU caliente (${g} °C). Bajá un poco la carga si podés.`
        : `GPU fresquita a ${g} °C. Margen térmico ideal.`);
    }
    if (t && t.ram && t.ram.totalGb > 0) {
      const freeGb = Math.max(0, t.ram.totalGb - t.ram.usedGb);
      comments.push(`Tenés ${freeGb.toFixed(1)} GB de RAM libre (${Math.round(t.ram.percent)}% usada).`);
    }
    if (t && t.cpu && t.cpu.usagePercent != null) {
      comments.push(`CPU al ${Math.round(t.cpu.usagePercent)}%. ${t.cpu.usagePercent < 35 ? "Todo súper tranquilo." : "Trabajando firme."}`);
    }
    comments.push(
      "Tip: usá Boost Rápido antes de abrir tu juego para liberar RAM y DNS.",
      "Tip: activá Modo Juego para subir prioridad automáticamente.",
      "Tip: en Fortnite Pro tenés backup automático de tu .ini."
    );

    const msg = comments[state.tipIndex % comments.length];
    state.tipIndex++;
    return msg;
  }

  stage.addEventListener("click", () => {
    triggerAction(state.tipIndex % 2 === 0 ? "wink" : "hop", 750);
    say(buildLiveComment(), 4000);
  });

  // ==================== CONEXIÓN A EVENTOS REALES (WebView2) ====================
  const host = window.chrome && window.chrome.webview;
  if (host) {
    host.addEventListener("message", (e) => {
      const m = e.data;
      if (!m || !m.type) return;

      switch (m.type) {
        case "telemetry":
          state.lastTel = m.data;
          evaluateState();
          break;

        case "game_mode_state": {
          const active = m.payload && m.payload.state && m.payload.state.active;
          state.gameName = active ? active.name : null;
          evaluateState();
          break;
        }

        case "game_mode_event": {
          const p = m.payload || {};
          state.gameName = p.game || (p.state && p.state.active && p.state.active.name) || "Juego";
          evaluateState();
          celebrate(`¡Modo Juego activo en ${state.gameName}!`);
          break;
        }

        case "game_session_ended": {
          const p = m.payload || {};
          state.gameName = null;
          evaluateState();
          const mins = Math.max(1, Math.round((p.durationSeconds || 0) / 60));
          const gpuInfo = p.maxGpuTemp != null ? ` GPU máx ${Math.round(p.maxGpuTemp)}°C.` : "";
          celebrate(`¡GG en ${p.game || "la partida"} (${mins} min)!${gpuInfo} Plan restaurado.`);
          break;
        }

        case "boost_result": {
          const ram = (m.payload && m.payload.ram) || {};
          const mb = Math.round(ram.freedMb || 0);
          celebrate(mb > 0
            ? `¡${mb >= 1024 ? (mb / 1024).toFixed(1) + " GB" : mb + " MB"} de RAM liberados! Listo para caer en el mapa.`
            : "¡Boost aplicado! Sistema listo para caer en el mapa.");
          break;
        }

        case "ram_result": {
          const mb = Math.round((m.payload && m.payload.freedMb) || 0);
          celebrate(mb > 0 ? `¡+${mb} MB de RAM liberados! Todo fluido.` : "RAM optimizada al máximo.");
          break;
        }

        case "cleanup_result": {
          const mb = Math.round((m.payload && m.payload.totalFreedMb) || 0);
          celebrate(mb > 0 ? `Caché limpio (+${mb} MB), FPS estables.` : "Caché limpio, FPS estables.");
          break;
        }

        case "fortnite_preset_result": {
          const p = m.payload || {};
          if (p.success) {
            celebrate(p.preset === "competitive" ? "Modo competitivo aplicado. ¡A construir!" : "Calidad visual aplicada en Fortnite.");
          }
          break;
        }

        case "fortnite_clean_result": {
          if (m.payload && m.payload.success) {
            celebrate("Archivos de Fortnite limpios y listos.");
          }
          break;
        }

        case "launch_game_result": {
          const p = m.payload || {};
          if (p.success) {
            celebrate(`¡Boost listo! Lanzando ${p.name || "tu juego"} a máxima potencia.`);
          }
          break;
        }

        case "checkup_action_result": {
          const p = m.payload || {};
          if (p.success && p.report && p.report.score >= 90) {
            celebrate(`¡Check-up al ${p.report.score}/100! Tu PC está afinada al máximo.`);
          }
          break;
        }

        case "restore_point_result": {
          if (m.payload && m.payload.success) {
            celebrate("🛡️ Punto de restauración guardado. ¡Cero riesgos!");
          }
          break;
        }

        case "winget_completed": {
          const p = m.payload || {};
          if (p.success) {
            celebrate(p.action === "upgrade"
              ? "¡Todas tus apps quedaron al día con winget!"
              : `¡Pack instalado (${p.succeeded} apps)! Tu PC está lista.`);
          }
          break;
        }

        case "debloat_result": {
          if (m.payload && m.payload.success) {
            celebrate("Windows más liviano y limpio. Todo reversible en Restauración.");
          }
          break;
        }

        case "repair_completed": {
          const p = m.payload || {};
          if (p.success) {
            celebrate(`¡Reparación ${(p.tool || "Windows").toUpperCase()} terminada sin errores!`);
          }
          break;
        }

        case "dns_apply_result": {
          if (m.payload && m.payload.success) {
            celebrate("DNS optimizado y caché limpia. ¡Menor latencia lista!");
          }
          break;
        }

        case "packet_loss_result": {
          const p = m.payload || {};
          if (p.lossPercent === 0) {
            celebrate(`0% Packet Loss en ${p.host}. ¡Conexión impecable!`);
          }
          break;
        }

        case "license_activate_result": {
          if (m.payload && m.payload.success) {
            celebrate("★ ¡PulseCore Pro activado! Todas las funciones desbloqueadas.");
          }
          break;
        }
      }
    });
  }
})();
