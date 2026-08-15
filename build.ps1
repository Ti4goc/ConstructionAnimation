$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$msbuildCandidates = @(
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
)

$msbuild = $msbuildCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $msbuild) {
    throw "MSBuild do Visual Studio 2022 nao encontrado. Instala o Visual Studio 2022 com desenvolvimento .NET para desktop."
}

& $msbuild ".\ConstructionAnimation.csproj" /restore /p:Configuration=Debug /m
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Build concluido." -ForegroundColor Green
