# PulseCore v1.7.0

**PulseCore** es una aplicación de escritorio nativa para Windows orientada a la telemetría de hardware en tiempo real, optimización gamer segura y mantenimiento integral del sistema. Combina un backend en **C# / .NET (WPF + WinForms Tray)** con una interfaz moderna *Dark Luxury* acelerada por hardware en **WebView2**.

---

## Stack Tecnológico

- **Runtime y UI nativa:** .NET (`net11.0-windows`) + WPF (`WindowChrome` con fix nativo `WM_GETMINMAXINFO` multi-monitor) + `System.Windows.Forms.NotifyIcon`
- **Motor web embebido:** `Microsoft.Web.WebView2` (HTML5, CSS3 Glassmorphism, Canvas 2D puro a 60fps, sin frameworks JS pesados)
- **Telemetría y FPS:** `LibreHardwareMonitorLib` (con fallback Win32 `GetSystemTimes` / `GlobalMemoryStatusEx`) + `Microsoft.Diagnostics.Tracing.TraceEvent` (ETW pasivo DXGI/D3D9 sin inyección)
- **Interoperabilidad Windows:** P/Invoke a `psapi.dll` (`EmptyWorkingSet`), `user32.dll`, `kernel32.dll`, `powercfg`, `netsh`, `ipconfig`, `winget`, `sfc`, `DISM` y WMI
- **Testing e Instalador:** `xUnit` (`PulseCore.Tests`) + **Inno Setup 6** (`Installer/PulseCore.iss` + `build-release.ps1`)

---

## Funcionalidades Principales

- **Dashboard & Telemetría en vivo (1 Hz):**
  - 4 instrumentos radiales de precisión de 240° para **CPU** (uso %, °C, MHz), **GPU** (carga %, °C, barra VRAM), **RAM física real** y **Disco `C:`**.
  - Gráfico interactivo Canvas 2D de 60 segundos con crosshair, tooltip flotante y selector `Uso (%)` / `Temp (°C)` / `RAM (%)`.
  - Panel **Gaming Readiness** (0–100, barra LED de 20 segmentos) + **⚡ Boost Rápido** (Alto Rendimiento + liberación de RAM + vaciado DNS).
- **Mini-HUD Flotante sin inyección (`HudWindow`):**
  - Ventana WPF 100% XAML nativo (sin WebView2) en forma de píldora translúcida `Topmost` mostrando **`FPS` (ETW pasivo)**, `CPU %`, `GPU °C`, `RAM GB` y `Ping ms`.
  - Arrastrable y con modo **Click-Through** (`WS_EX_TRANSPARENT`) para no interceptar clics en partida.
- **Bandeja del Sistema (System Tray):**
  - Al cerrar con `X` se oculta en la bandeja para seguir vigilando juegos. Menú contextual con *Abrir PulseCore*, *Mostrar/Ocultar Mini-HUD*, *Desbloquear/Bloquear Mini-HUD*, *⚡ Boost Rápido* y *Salir*.
- **Optimizador y Limpieza Profunda:**
  - Cambio reversible de Planes de Energía, Liberador de RAM (`EmptyWorkingSet`) y Top 5 de procesos por RAM.
  - Limpieza con modo **Dry Run** previo: `%TEMP%` / `C:\Windows\Temp`, Shaders DirectX (`D3DSCache`), caché GPU (`NVIDIA` / `AMD`) y caché DNS.
- **Modo Juego Automático, Biblioteca y Red Avanzada:**
  - *Process Watcher* cada 3s (`games.json`), íconos reales en juegos configurados, cambio/restauración automática del plan de energía, elevación de prioridad, **Historial de 30 Sesiones** (`sessions.json`) y **Modal Resumen Post-Partida**.
  - **Autodetección de Steam y Epic Games** con portadas/íconos reales y lanzamiento en 1 clic con Boost (`⚡ Preparar y Jugar`).
  - **Monitor de Ping & Jitter** (Cloudflare, Google, AWS Sudamérica con fallback TCP 443), **DNS Switcher con Benchmark** (`Cloudflare`, `Google`, `Quad9`, `DHCP`) y **Test de Packet Loss** (20 pings).
- **Fortnite Pro (100% seguro):**
  - Opera solo sobre `GameUserSettings.ini` y `Saved\Demos`, `Crashes` y `Logs`. Crea backup original intacto en `%APPDATA%\PulseCore\Backups\GameUserSettings.original.ini`, aplica presets `Competitivo FPS` o `Calidad Visual` con límite de FPS / *Solo Lectura* y restaura en 1 clic.
- **Laboratorio de Periféricos:**
  - **Test de Polling Rate del Mouse** (`getCoalescedEvents`, gráfico en vivo y clasificación hasta `2000+ Hz`) y **Test de Fluidez y Ghosting del Monitor** (`requestAnimationFrame` + pista de 3 carriles con Pulse-Llama).
- **Apps / Recién Formateado (`winget`):**
  - Catálogo curado por categorías (*Gaming*, *Navegadores y Multimedia*, *Utilidades*), Pack Gamer, instalación silenciosa y `winget upgrade --all` con consola en vivo.
- **Check-up Inteligente, Suite de Windows y Centro de Restauración:**
  - Auditoría de 5 ajustes clave (0–100 pts) + **Debloat Seguro** (5 ajustes reversibles en Registro) + **Gestor de Inicio** (`disabled_startup.json`) + **Reparación SFC / DISM** en vivo.
  - **Centro de Restauración** unificado (`restore_history.json`) con *Deshacer* individual, *Revertir todo* y creación de **Puntos de Restauración de Windows**.
- **Logs, Pulse-Llama y Auto-Updater:**
  - Consola de eventos filtrable + exportación de diagnóstico `.json` al Escritorio.
  - Mascota táctica interactiva **Pulse-Llama** en SVG que reacciona en tiempo real (ocultable en `localStorage`).
  - **Auto-Updater** conectado a GitHub Releases (timeout 5s en segundo plano, banner discreto en la UI cuando hay nueva versión).

---

## Compilación, Tests y Generación del Instalador

```powershell
# Compilar en Debug o Release
dotnet build PulseCore.slnx
dotnet build PulseCore.slnx -c Release

# Ejecutar tests unitarios (aislados en carpetas temporales)
dotnet test PulseCore.slnx

# Generar publish/ y PulseCore-Setup-v1.7.0.exe (requiere Inno Setup 6)
.\build-release.ps1
```

### Ejecución como Administrador
`PulseCore.exe` incluye `app.manifest` con `requireAdministrator` para leer temperaturas reales de hardware (LibreHardwareMonitor), escuchar eventos ETW de FPS, cambiar DNS/planes de energía y ejecutar SFC/DISM.
