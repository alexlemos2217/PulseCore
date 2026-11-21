(() => {
  "use strict";

  const FALLBACK_URL = "https://github.com/alexlemos2217/PulseCore/releases/latest";
  const API_URL = "https://api.github.com/repos/alexlemos2217/PulseCore/releases/latest";

  // 1. Descarga dinámica conectada a GitHub Releases
  async function loadLatestRelease() {
    try {
      const resp = await fetch(API_URL, { headers: { Accept: "application/vnd.github+json" } });
      if (!resp.ok) return;
      const data = await resp.json();

      const tag = data.tag_name || "v1.8.0";
      let downloadUrl = data.html_url || FALLBACK_URL;
      let assetInfo = "";

      if (Array.isArray(data.assets)) {
        const exe = data.assets.find((a) => a.name && a.name.toLowerCase().endsWith(".exe"));
        if (exe && exe.browser_download_url) {
          downloadUrl = exe.browser_download_url;
          const mb = exe.size ? ` · ${(exe.size / (1024 * 1024)).toFixed(1)} MB` : "";
          assetInfo = ` · ${exe.name}${mb}`;
        }
      }

      document.querySelectorAll("[data-version-badge]").forEach((el) => {
        el.textContent = tag;
      });
      document.querySelectorAll("[data-download-btn]").forEach((btn) => {
        btn.href = downloadUrl;
      });

      const meta = document.getElementById("releaseMeta");
      if (meta) {
        meta.textContent = `Última release: ${tag}${assetInfo} · Gratis y auditable`;
      }
    } catch (_) {
      // Fallback silencioso a GitHub Releases
    }
  }

  // 2. Mouse-tracking border glow en tarjetas
  let raf = 0;
  let pointer = null;
  document.addEventListener("pointermove", (e) => {
    pointer = { x: e.clientX, y: e.clientY };
    if (raf) return;
    raf = requestAnimationFrame(() => {
      raf = 0;
      if (!pointer) return;
      document.querySelectorAll(".glow-card").forEach((card) => {
        const r = card.getBoundingClientRect();
        card.style.setProperty("--mx", `${pointer.x - r.left}px`);
        card.style.setProperty("--my", `${pointer.y - r.top}px`);
      });
    });
  });

  // 3. FAQ Acordeón interactivo
  document.querySelectorAll(".faq-q").forEach((btn) => {
    btn.addEventListener("click", () => {
      const item = btn.closest(".faq-item");
      const open = item.classList.toggle("open");
      btn.setAttribute("aria-expanded", String(open));
    });
  });

  loadLatestRelease();
})();
