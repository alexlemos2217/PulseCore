(() => {
  "use strict";

  const host = window.chrome && window.chrome.webview;
  const $ = (id) => document.getElementById(id);
  const pc = () => window.PulseCore || {};
  const send = (type, payload) => host && host.postMessage({ type, payload });

  const state = {
    isPro: false,
    status: null,
    activating: false,
    confirmDeactivate: false,
    deactivateTimer: 0,
  };

  const modal = $("proModal");
  const btnProStatus = $("btnProStatus");
  const modalFree = $("proModalFree");
  const modalActive = $("proModalActive");
  const modalTag = $("proModalTag");
  const modalTitle = $("proModalTitle");
  const modalSub = $("proModalSub");
  const featureBanner = $("proFeatureBanner");
  const featureBannerText = $("proFeatureBannerText");
  const keyInput = $("proKeyInput");
  const btnActivate = $("btnActivatePro");
  const btnActivateLabel = $("btnActivateProLabel");
  const modalMsg = $("proModalMsg");
  const btnDeactivate = $("btnDeactivatePro");

  function setMsg(text, tone = "info") {
    if (!modalMsg) return;
    if (!text) {
      modalMsg.hidden = true;
      modalMsg.textContent = "";
      return;
    }
    modalMsg.hidden = false;
    modalMsg.dataset.tone = tone;
    modalMsg.textContent = text;
  }

  function resetDeactivateBtn() {
    state.confirmDeactivate = false;
    clearTimeout(state.deactivateTimer);
    if (btnDeactivate) {
      btnDeactivate.textContent = "Quitar licencia de esta PC";
      btnDeactivate.classList.remove("confirming");
    }
  }

  function applyStatus(status) {
    if (!status) return;
    state.status = status;
    state.isPro = !!status.isPro;

    document.body.classList.toggle("is-pro", state.isPro);

    if (btnProStatus) {
      btnProStatus.classList.toggle("is-pro", state.isPro);
      btnProStatus.innerHTML = state.isPro
        ? "<span>★ PRO</span>"
        : "<span>⚡ Hazte Pro</span>";
      btnProStatus.title = state.isPro
        ? "PulseCore Pro activo · Ver detalles de licencia"
        : "Activar o comprar PulseCore Pro";
    }

    if (modalFree && modalActive) {
      modalFree.hidden = state.isPro;
      modalActive.hidden = !state.isPro;
    }

    if (state.isPro) {
      if (modalTag) modalTag.textContent = "LICENCIA ACTIVA";
      if (modalTitle) modalTitle.textContent = "PulseCore Pro";
      if (modalSub) modalSub.textContent = "Todas las herramientas avanzadas están desbloqueadas.";
      if ($("proMaskedKey")) $("proMaskedKey").textContent = status.maskedKey || "—";
      if ($("proEmail")) $("proEmail").textContent = status.email || "No registrado";
      if ($("proHwid")) $("proHwid").textContent = status.hwid || "—";
      if ($("proStatusNote")) $("proStatusNote").textContent = status.statusMessage || "Licencia verificada y cifrada en este equipo.";
    } else {
      if (modalTag) modalTag.textContent = "PULSECORE PRO";
      if (modalTitle) modalTitle.textContent = "Desbloqueá todo el potencial de tu PC";
      if (modalSub) modalSub.textContent = "Licencia de por vida · Hasta 2 PCs · Sin suscripciones";
    }
  }

  function openModal(featureName) {
    if (!modal) return;
    resetDeactivateBtn();
    setMsg("");

    if (!state.isPro && featureBanner && featureBannerText) {
      if (featureName) {
        featureBannerText.textContent = `${featureName} es una función de PulseCore Pro.`;
        featureBanner.hidden = false;
      } else {
        featureBanner.hidden = true;
      }
    } else if (featureBanner) {
      featureBanner.hidden = true;
    }

    modal.hidden = false;
    if (!state.isPro && keyInput) {
      setTimeout(() => keyInput.focus(), 60);
    }
  }

  function closeModal() {
    if (!modal) return;
    modal.hidden = true;
    resetDeactivateBtn();
    setMsg("");
  }

  // ==================== INTERCEPTOR PRO EN FASE DE CAPTURA ====================
  // Evita que botones/switches Pro queden en estado loading en modo Free
  // y abre el modal de activación al instante (el backend C# también valida siempre).
  function findProFeature(target) {
    if (!target || !target.closest) return null;
    const interactive = target.closest("button, input, label.switch, a");
    if (!interactive) return null;
    const hostEl = interactive.closest("[data-pro-feature]");
    if (!hostEl) return null;
    return hostEl.getAttribute("data-pro-feature") || "PulseCore Pro";
  }

  document.addEventListener("click", (e) => {
    if (state.isPro) return;
    const feature = findProFeature(e.target);
    if (!feature) return;
    e.preventDefault();
    e.stopImmediatePropagation();
    openModal(feature);
  }, true);

  document.addEventListener("submit", (e) => {
    if (state.isPro) return;
    const hostEl = e.target && e.target.closest ? e.target.closest("[data-pro-feature]") : null;
    if (!hostEl) return;
    e.preventDefault();
    e.stopImmediatePropagation();
    openModal(hostEl.getAttribute("data-pro-feature") || "PulseCore Pro");
  }, true);

  // ==================== CONTROLES DEL MODAL ====================
  btnProStatus?.addEventListener("click", () => openModal(null));
  $("btnCloseProModal")?.addEventListener("click", closeModal);
  $("btnOkProModal")?.addEventListener("click", closeModal);
  modal?.addEventListener("click", (e) => {
    if (e.target === modal) closeModal();
  });
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && modal && !modal.hidden) closeModal();
  });

  $("btnBuyPro")?.addEventListener("click", () => {
    send("license:openCheckout");
    pc().addLog?.("INFO", "Abriendo página oficial de compra de PulseCore Pro…");
  });

  $("proActivateForm")?.addEventListener("submit", (e) => {
    e.preventDefault();
    if (state.activating) return;
    const key = (keyInput?.value || "").trim();
    if (!key) {
      setMsg("Ingresá tu clave de licencia de Gumroad para activar.", "err");
      keyInput?.focus();
      return;
    }
    state.activating = true;
    btnActivate?.classList.add("loading");
    if (btnActivate) btnActivate.disabled = true;
    if (btnActivateLabel) btnActivateLabel.textContent = "Verificando…";
    setMsg("Validando licencia con Gumroad…", "info");
    send("license:activate", { key });
  });

  btnDeactivate?.addEventListener("click", () => {
    if (!state.confirmDeactivate) {
      state.confirmDeactivate = true;
      btnDeactivate.textContent = "¿Confirmar? Clic otra vez";
      btnDeactivate.classList.add("confirming");
      state.deactivateTimer = setTimeout(resetDeactivateBtn, 4500);
      return;
    }
    resetDeactivateBtn();
    send("license:deactivate");
    pc().toast?.("Licencia eliminada de esta PC.", "info");
    pc().addLog?.("SYS", "Licencia Pro removida localmente; volviendo a modo Free.");
  });

  // ==================== MENSAJES DESDE C# ====================
  if (host) {
    host.addEventListener("message", (e) => {
      const m = e.data;
      if (!m || !m.type) return;

      switch (m.type) {
        case "license_status":
          applyStatus(m.payload);
          break;

        case "license_activate_result": {
          const p = m.payload || {};
          state.activating = false;
          btnActivate?.classList.remove("loading");
          if (btnActivate) btnActivate.disabled = false;
          if (btnActivateLabel) btnActivateLabel.textContent = "Activar";

          if (p.status) applyStatus(p.status);

          if (p.success) {
            if (keyInput) keyInput.value = "";
            setMsg(p.message || "¡PulseCore Pro activado!", "ok");
            pc().toast?.("★ ¡PulseCore Pro activado! Todas las funciones desbloqueadas.", "ok");
            pc().addLog?.("SYS", "Licencia PulseCore Pro activada y cifrada con DPAPI.");
            setTimeout(closeModal, 900);
          } else {
            setMsg(p.message || "No se pudo validar la clave de licencia.", "err");
            pc().addLog?.("WARN", `Activación Pro rechazada: ${p.message || "clave inválida"}`);
          }
          break;
        }

        case "pro_required": {
          const p = m.payload || {};
          // Defensa en profundidad: rehabilitar perfiles si quedaron deshabilitados
          document.querySelectorAll("[data-qprof]").forEach((b) => { b.disabled = false; });
          openModal(p.feature || "Esta función");
          break;
        }
      }
    });
  }

  // Exponer helpers en window.PulseCore
  window.PulseCoreLicense = {
    isPro: () => state.isPro,
    openModal,
  };

  send("license:getStatus");
})();
