# Builds the self-contained exe, the portable copy and the installer into .\dist
param([string]$Version = "0.2.7")
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$pub = Join-Path $root "publish\selfcontained"
$dist = Join-Path $root "dist"

dotnet publish (Join-Path $root "src\OKBoard") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:Version=$Version -o $pub
if ($LASTEXITCODE) { throw "dotnet publish failed" }

New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item (Join-Path $pub "OKBoard.exe") (Join-Path $dist "OKBoard-$Version-portable.exe") -Force

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }
& $iscc /Q "/DAppVersion=$Version" "/DAppExe=$pub\OKBoard.exe" (Join-Path $PSScriptRoot "OKBoard.iss")
if ($LASTEXITCODE) { throw "ISCC failed" }

Get-ChildItem $dist | ForEach-Object {
    "{0,-36} {1,8:N1} MB  SHA256 {2}" -f $_.Name, ($_.Length / 1MB), (Get-FileHash $_.FullName).Hash
}
