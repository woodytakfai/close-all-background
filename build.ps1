# Build script: compile a zero-dependency EXE using the .NET Framework compiler
# shipped with Windows.  Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
# NOTE: kept ASCII-only on purpose (Windows PowerShell 5.1 reads BOM-less files as ANSI).
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root "src"
$dist = Join-Path $root "dist"

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) { throw "csc.exe (.NET Framework 4.x) not found" }

New-Item -ItemType Directory -Force -Path $dist | Out-Null
$out = Join-Path $dist ([char]0x4E00 + [char]0x952E + [char]0x5173 + [char]0x95ED +
                        [char]0x540E + [char]0x53F0 + [char]0x5E94 + [char]0x7528 + ".exe")

# 1) icon
$py = "C:\Users\Administrator\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe"
if ((Test-Path $py) -and -not (Test-Path (Join-Path $src "app.ico"))) {
    & $py (Join-Path $root "tools\make_icon.py") | Out-Null
}

# 2) compile
# NOTE: every "/opt:" + value pair MUST be parenthesised - in PowerShell the comma
# operator binds tighter than '+', otherwise the value becomes a separate argument.
$sources = Get-ChildItem (Join-Path $src "*.cs") | ForEach-Object { $_.FullName }
$cscArgs = @(
    "/nologo", "/target:winexe", "/platform:anycpu", "/optimize+", "/codepage:65001",
    ("/win32icon:" + (Join-Path $src "app.ico")),
    ("/win32manifest:" + (Join-Path $src "app.manifest")),
    ("/out:" + $out),
    "/r:System.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll", "/r:System.Core.dll"
) + $sources

Write-Host "Compiling ..." -ForegroundColor Cyan
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "compile failed, exit code $LASTEXITCODE" }

$f = Get-Item $out
Write-Host ("OK  " + $f.FullName) -ForegroundColor Green
Write-Host ("Size: " + $f.Length + " bytes")
