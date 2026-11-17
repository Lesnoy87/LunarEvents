param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

dotnet build (Join-Path $here "LunarEvents.csproj") -c Release -p:GameDir="$GameDir"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}
Copy-Item (Join-Path $here "bin\Release\netstandard2.1\LunarEvents.dll") (Join-Path $here "..\LunarEvents.dll") -Force
Write-Host "LunarEvents.dll обновлён." -ForegroundColor Green
