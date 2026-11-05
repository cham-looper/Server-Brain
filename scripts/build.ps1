<#
  Builds Server Brain end to end.

    .\scripts\build.ps1                 build UI + publish the Windows app (out\win-x64)
    .\scripts\build.ps1 -Installer      ...and compile the installer (needs Inno Setup 6)
    .\scripts\build.ps1 -Linux          also publish the headless Linux daemon (out\linux-x64)
    .\scripts\build.ps1 -SkipTests      skip the unit tests

  Needs: .NET 9 SDK, Node.js 20+.
#>
param(
  [switch]$Installer,
  [switch]$Linux,
  [switch]$SkipTests,
  [switch]$SkipWeb
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root
$version = ([xml](Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.Version
Write-Host "== Server Brain $version" -ForegroundColor Cyan

if (-not (Test-Path "$root\assets\icon.ico")) { & "$PSScriptRoot\make-icon.ps1" }

if (-not $SkipWeb) {
  Write-Host "== Building the dashboard UI" -ForegroundColor Cyan
  Push-Location "$root\web"
  if (-not (Test-Path node_modules)) { npm install --no-fund --no-audit; if ($LASTEXITCODE) { throw 'npm install failed' } }
  npm run build; if ($LASTEXITCODE) { throw 'UI build failed' }
  Pop-Location
}

if (-not $SkipTests) {
  Write-Host "== Running tests" -ForegroundColor Cyan
  dotnet test "$root\tests\ServerBrain.Tests" -c Release --nologo -v q; if ($LASTEXITCODE) { throw 'Tests failed' }
}

Write-Host "== Publishing the Windows app" -ForegroundColor Cyan
$out = "$root\out\win-x64"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
# A folder (not a single compressed exe): the runtime maps files from disk instead of unpacking them into RAM,
# which keeps the idle tray process around 25 MB private memory instead of ~170 MB. The installer compresses it anyway.
dotnet publish "$root\src\ServerBrain.App" -c Release -r win-x64 --self-contained true -o $out `
  -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false --nologo -v q
if ($LASTEXITCODE) { throw 'Publish failed' }
Get-ChildItem $out -Include *.xml -Recurse | Remove-Item -Force
$mb = [math]::Round((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "Published $((Get-ChildItem $out -Recurse -File).Count) files, $mb MB -> $out"

if ($Linux) {
  Write-Host "== Publishing the Linux daemon" -ForegroundColor Cyan
  $lout = "$root\out\linux-x64"
  if (Test-Path $lout) { Remove-Item $lout -Recurse -Force }
  dotnet publish "$root\src\ServerBrain.Daemon" -c Release -r linux-x64 --self-contained true -o $lout `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none --nologo -v q
  if ($LASTEXITCODE) { throw 'Linux publish failed' }
  Compress-Archive -Path "$lout\*" -DestinationPath "$root\out\server-brain-$version-linux-x64.zip" -Force
}

if ($Installer) {
  Write-Host "== Building the installer" -ForegroundColor Cyan
  $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $iscc) { throw 'Inno Setup 6 was not found. Install it with: winget install JRSoftware.InnoSetup' }
  & $iscc "/DAppVersion=$version" "$root\installer\ServerBrain.iss"; if ($LASTEXITCODE) { throw 'Installer build failed' }
  Get-ChildItem "$root\out\ServerBrain-Setup-*.exe" | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize | Out-String | Write-Host
}
Write-Host "== Done" -ForegroundColor Green
