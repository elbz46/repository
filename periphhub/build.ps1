# Windows build: needs the .NET 10 SDK and NSIS (winget install NSIS.NSIS).
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
[xml]$proj = Get-Content src/PeriphHub/PeriphHub.csproj
$version = $proj.Project.PropertyGroup[0].Version
if (Test-Path out) { Remove-Item out -Recurse -Force }
dotnet publish src/PeriphHub/PeriphHub.csproj -c Release -r win-x64 --self-contained true -p:Version=$version -o out/publish
New-Item -ItemType Directory -Force out/installer | Out-Null
$makensis = (Get-Command makensis -ErrorAction SilentlyContinue)?.Source
if (-not $makensis) { $makensis = "${env:ProgramFiles(x86)}\NSIS\makensis.exe" }
& $makensis -V2 "-DVERSION=$version" "-DSRCDIR=$PWD\out\publish" "-DOUTDIR=$PWD\out\installer" installer\PeriphHub.nsi
Get-ChildItem out/installer
