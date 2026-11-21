$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Write-Host "=== PulseCore v2.0.0 Release Builder ===" -ForegroundColor Cyan

# 1. Verificar dotnet CLI
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "No se encontró 'dotnet' en el PATH. Instalá o repará el .NET SDK antes de continuar."
    exit 1
}

# 2. Limpiar carpeta publish/ y dist/
$publishDir = Join-Path $root 'publish'
$distDir = Join-Path $root 'dist'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

# 3. Publicar en Release (win-x64)
Write-Host "Publicando PulseCore.App (Release, win-x64)..." -ForegroundColor Yellow
dotnet publish 'PulseCore.App\PulseCore.App.csproj' -c Release -r win-x64 --self-contained false -o $publishDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish falló con código $LASTEXITCODE."
    exit $LASTEXITCODE
}
Write-Host "Publish completado en: $publishDir" -ForegroundColor Green

# 4. Detectar Inno Setup (ISCC.exe) y compilar instalador
$issPath = Join-Path $root 'Installer\PulseCore.iss'
$isccCandidates = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) }

$iscc = $isccCandidates | Select-Object -First 1
if ($iscc) {
    Write-Host "Compilando instalador con Inno Setup ($iscc)..." -ForegroundColor Yellow
    & $iscc $issPath
    if ($LASTEXITCODE -ne 0) {
        Write-Error "ISCC.exe falló con código $LASTEXITCODE."
        exit $LASTEXITCODE
    }
    Write-Host "Instalador generado en: $(Join-Path $distDir 'PulseCore-Setup-v2.0.0.exe')" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "Inno Setup (ISCC.exe) no se detectó instalado automáticamente." -ForegroundColor Yellow
    Write-Host "Para generar PulseCore-Setup-v2.0.0.exe, instalá Inno Setup 6 y ejecutá:" -ForegroundColor White
    Write-Host "  & `"C:\Program Files (x86)\Inno Setup 6\ISCC.exe`" `"$issPath`"" -ForegroundColor Cyan
}
