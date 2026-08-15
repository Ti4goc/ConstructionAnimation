$Managed = "C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II\Cities2_Data\Managed"
$names = @("Game.dll","Colossal.Core.dll","Colossal.Logging.dll","Unity.Entities.dll","Unity.Collections.dll","Unity.Mathematics.dll")

Write-Host "Cities: Skylines II - ambiente de desenvolvimento" -ForegroundColor Cyan
Write-Host "Managed: $Managed"
Write-Host ""
foreach ($name in $names) {
    $p = Join-Path $Managed $name
    if (Test-Path $p) {
        $v = (Get-Item $p).VersionInfo.FileVersion
        Write-Host "[OK] $name $v" -ForegroundColor Green
    } else {
        Write-Host "[FALTA] $name" -ForegroundColor Red
    }
}

$toolPath = [Environment]::GetEnvironmentVariable("CSII_TOOLPATH", "User")
if ($toolPath) {
    Write-Host ""
    Write-Host "CSII_TOOLPATH=$toolPath"
} else {
    Write-Host ""
    Write-Host "CSII_TOOLPATH nao definido." -ForegroundColor Yellow
}
