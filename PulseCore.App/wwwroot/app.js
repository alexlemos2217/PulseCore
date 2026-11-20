(() => {
  "use strict";

  const host = window.chrome && window.chrome.webview;
  const $ = (id) => document.getElementById(id);

  // ---------- Puente JS -> C# ----------
  const send = (type, payload) => host && host.postMessage({ type, payload });

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
      if (fn) fn(msg.payload || {});
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
