$ErrorActionPreference = "Stop"

$Source = Split-Path -Parent $MyInvocation.MyCommand.Path
$Destination = "C:\Dev\ConstructionAnimation"
$Managed = "C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II\Cities2_Data\Managed"
$Required = @(
    "Game.dll",
    "Colossal.Core.dll",
    "Colossal.Logging.dll",
    "Unity.Entities.dll",
    "Unity.Collections.dll",
    "Unity.Mathematics.dll"
)

Write-Host "Construction Animation - setup" -ForegroundColor Cyan

if (-not (Test-Path $Managed)) {
    throw "Pasta Managed nao encontrada: $Managed"
}

foreach ($dll in $Required) {
    $path = Join-Path $Managed $dll
    if (-not (Test-Path $path)) {
        throw "DLL obrigatoria nao encontrada: $path"
    }
}

New-Item -ItemType Directory -Path "C:\Dev" -Force | Out-Null

if ((Resolve-Path $Source).Path -ne $Destination) {
    if (Test-Path $Destination) {
        Write-Host "A atualizar $Destination" -ForegroundColor Yellow
    } else {
        Write-Host "A criar $Destination" -ForegroundColor Green
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -Path (Join-Path $Source "*") -Destination $Destination -Recurse -Force
}

Write-Host ""
Write-Host "Projeto preparado em: $Destination" -ForegroundColor Green
Write-Host "Game.dll: $(Join-Path $Managed 'Game.dll')"

$toolPath = [Environment]::GetEnvironmentVariable("CSII_TOOLPATH", "User")
if ($toolPath -and (Test-Path (Join-Path $toolPath "Mod.props"))) {
    Write-Host "CSII_TOOLPATH encontrado: $toolPath" -ForegroundColor Green
} else {
    Write-Host "CSII_TOOLPATH ainda nao esta configurado. O projeto pode ser aberto para inspecao, mas a integracao oficial de build/deploy do CS2 pode precisar do toolchain do jogo." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Abrir no Visual Studio:" -ForegroundColor Cyan
Write-Host "  $Destination\ConstructionAnimation.csproj"
