📄 **[Ver Banner Presentación de PulseCore (PDF)](./docs/pulsecore_banner.pdf)**

# PulseCore v1.0.0

**PulseCore** es una aplicación de escritorio nativa para Windows orientada a la telemetría de hardware en tiempo real y la optimización gamer segura y reversible. Combina un backend en **C# / .NET (WPF)** con una interfaz moderna acelerada por hardware en **WebView2** de estética *Dark Luxury*.

---

## Stack Tecnológico

- **Runtime y UI nativa:** .NET (`net11.0-windows`) + WPF (`WindowChrome` sin bordes clásicos)
- **Motor web embebido:** `Microsoft.Web.WebView2` (HTML5, CSS3 Glassmorphism, Canvas 2D puro a 60fps, sin frameworks JS pesados)
- **Sensores de hardware:** `LibreHardwareMonitorLib` con fallback automático a APIs nativas Win32 (`GetSystemTimes`, `GlobalMemoryStatusEx`, `DriveInfo`)
- **Interoperabilidad Windows:** P/Invoke a `psapi.dll` (`EmptyWorkingSet`), `kernel32.dll`, `powercfg` e `ipconfig` sin ventanas de consola
- **Testing:** `xUnit` (`PulseCore.Tests`)

---

## Funcionalidades Principales

- **Telemetría en tiempo real (1 Hz):**
  - Instrumentos radiales de precisión de 240° con marcas de escala para **CPU** (uso %, temperatura °C y frecuencia MHz), **GPU** (carga %, temperatura °C y barra de VRAM), **RAM física real** y **Storage (`C:`)**.
  - Gráfico interactivo en Canvas 2D con historial de 60 segundos, crosshair con tooltip flotante y selector entre `Uso (%)`, `Temp (°C)` y `RAM (%)`.
  - Panel **Gaming Readiness** con índice en vivo (`0–100`), barra LED de 20 segmentos y botón **Boost Rápido** (Alto Rendimiento + liberación de RAM + vaciado DNS).
- **Optimizador de Sistema:**
  - Cambio real y reversible del plan de energía de Windows (`Equilibrado`, `Alto rendimiento`, `Máximo rendimiento`).
  - Liberador de RAM (`EmptyWorkingSet`) sin cerrar programas, con contador de sesión.
  - Monitor del Top 5 de procesos por consumo de RAM.
- **Limpieza Profunda (Dry Run primero):**
  - Escaneo sin borrado y limpieza selectiva omitiendo archivos en uso: `%TEMP%` / `C:\Windows\Temp`, Shaders DirectX (`D3DSCache`), caché GPU (`NVIDIA` / `AMD`) y caché DNS (`ipconfig /flushdns`).
- **Modo Juego Automático & Monitor de Ping:**
  - *Process Watcher* ligero cada 3 segundos con lista persistida en `%APPDATA%\PulseCore\games.json`.
  - Al detectar un juego: guarda el plan actual, activa Alto Rendimiento, sube la prioridad del proceso a `High` (tolerante a anti-cheat) y libera RAM. Al cerrarse el juego, restaura automáticamente el plan de energía original.
  - Monitor de **Ping y Jitter** (4 muestras contra Cloudflare `1.1.1.1`, Google `8.8.8.8` y AWS Sudamérica) con fallback automático a TCP 443 si ICMP está bloqueado.
- **Módulo Fortnite Pro (100% seguro):**
  - Solo opera sobre `%LOCALAPPDATA%\FortniteGame\Saved\Config\WindowsClient\GameUserSettings.ini` y carpetas `Saved\Demos`, `Crashes` y `Logs` (nunca toca ejecutables ni memoria del juego).
  - Crea automáticamente un backup intacto en `%APPDATA%\PulseCore\Backups\GameUserSettings.original.ini` antes del primer cambio, aplica presets `Competitivo FPS` o `Calidad Visual` con límite de FPS y opción *Solo Lectura*, y permite restaurar el original en 1 clic.
- **Logs en vivo y Exportación:**
  - Consola tipo terminal con filtros por categoría (`GAME`, `FORTNITE`, `BOOST`, `CLEAN`, `POWER`, `INFO`, `WARN`, `ERR`) y exportación a un archivo JSON de diagnóstico en el Escritorio.
- **Pulse-Llama:**
  - Mascota táctica interactiva en SVG vectorial en la sidebar que reacciona en tiempo real a temperaturas, carga, Modo Juego y acciones completadas (ocultable con persistencia en `localStorage`).

---

## Arquitectura

```text
PulseCore/
├── PulseCore.slnx
├── PulseCore.App/
│   ├── MainWindow.xaml(.cs)        # Ventana sin bordes + puente JSON WebView2 + loop 1 Hz
│   ├── app.manifest                # Elevación UAC (requireAdministrator) + PerMonitorV2 DPI
│   ├── Models/                     # DTOs inmutables (Telemetry, Optimizer, GameMode, Fortnite)
│   ├── Services/                   # Servicios desacoplados sin dependencias de UI
│   │   ├── TelemetryService.cs     # LibreHardwareMonitor + FallbackProbe Win32
│   │   ├── PowerPlanService.cs     # powercfg sin consola
│   │   ├── MemoryOptimizerService.cs
│   │   ├── CleanerService.cs       # Dry Run + validación de rutas seguras
│   │   ├── ProcessMonitorService.cs
│   │   ├── GameModeService.cs      # Process Watcher + games.json
│   │   ├── NetworkPingService.cs   # ICMP + fallback TCP 443
│   │   └── FortniteService.cs      # Parser INI + backup automático
│   └── wwwroot/                    # Frontend servido vía host virtual https://pulsecore.app/
│       ├── index.html, styles.css
│       └── app.js, features.js, gamemode.js, fortnite.js, mascot.js
└── PulseCore.Tests/                # Tests unitarios xUnit deterministas en carpetas temporales
```

### Puente Asíncrono C# ↔ JS
La comunicación entre el frontend y el backend nativo se realiza exclusivamente mediante mensajes JSON tipados:
- **JS → C#:** `window.chrome.webview.postMessage({ type, payload })` recibido en `WebMessageReceived`. Las operaciones pesadas corren fuera del hilo de UI (`Task.Run`) protegidas con `SemaphoreSlim` para evitar solapamientos.
- **C# → JS:** `CoreWebView2.PostWebMessageAsJson(...)` despachado siempre en el hilo de UI vía `Dispatcher.InvokeAsync`.

---

## Requisitos

- Windows 10 / 11 (x64)
- [.NET SDK](https://dotnet.microsoft.com/) compatible con el `TargetFramework` del proyecto (`net11.0-windows`)
- Microsoft Edge **WebView2 Runtime** (preinstalado en Windows 11)

---

## Compilación, Tests y Ejecución

```powershell
# 1. Compilar en Debug
dotnet build PulseCore.slnx

# 2. Ejecutar tests unitarios (no modifican archivos reales del sistema)
dotnet test PulseCore.slnx

# 3. Compilar para producción (Release)
dotnet build PulseCore.slnx -c Release
```

### Ejecutar como Administrador
`PulseCore.exe` incluye `app.manifest` con `requireAdministrator` para que `LibreHardwareMonitorLib` pueda leer temperaturas reales de CPU/GPU y limpiar `C:\Windows\Temp`.

- **Desde el Explorador o terminal:** ejecutá `PulseCore.App\bin\Release\net11.0-windows\PulseCore.exe` (Windows pedirá confirmación UAC automáticamente).
- **Desde CLI con `dotnet run`:** abrí PowerShell **como Administrador** y corré:
  ```powershell
  dotnet run --project PulseCore.App -c Release
  ```
