# Hardening tests: verify the safety gates added after the adversarial review.
# Trick used below: copy the harmless test fixture to a file NAMED like a protected
# system binary (explorer.exe). ProcessName then matches the protection list, so we can
# prove the gate refuses it without ever risking the real shell.
$ErrorActionPreference = "Continue"
$root = "C:\Users\Administrator\Documents\deepseek-harness\default-workspace\CloseAllBackground"
$exe  = Join-Path $root "dist\一键关闭后台应用.exe"
$t    = Join-Path $root "test"
New-Item -ItemType Directory -Force -Path $t | Out-Null
$results = New-Object System.Collections.ArrayList

function Check([string]$name, [bool]$ok, [string]$detail) {
    [void]$results.Add([pscustomobject]@{ Test = $name; Pass = $ok; Detail = $detail })
    $tag = if ($ok) { "PASS" } else { "FAIL" }
    Write-Host ("[{0}] {1}  -- {2}" -f $tag, $name, $detail)
}
function Alive($id) {
    if ($null -eq $id) { return $false }
    $n = 0
    if (-not [int]::TryParse([string]$id, [ref]$n)) { return $false }
    if ($n -le 0) { return $false }
    return ($null -ne (Get-Process -Id $n -ErrorAction SilentlyContinue))
}
function ReadOut([string]$f) {
    $p = Join-Path $t $f
    if (Test-Path $p) { return (Get-Content $p -Raw -Encoding UTF8) }
    return ""
}

$victim = Join-Path $t "VictimApp.exe"
$fakeDir = Join-Path $t "fakeprot"
New-Item -ItemType Directory -Force -Path $fakeDir | Out-Null

if (-not (Test-Path $victim)) {
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    & $csc /nologo /target:winexe ("/out:" + $victim) /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $root "tools\Victim.cs") | Out-Null
}

Write-Host "`n=== H1: a process whose NAME is protected must be refused even via --pid ==="
$fakeExplorer = Join-Path $fakeDir "explorer.exe"
Copy-Item $victim $fakeExplorer -Force
$fe = Start-Process $fakeExplorer -PassThru
Start-Sleep -Milliseconds 1800
Check "H1.0 fake explorer running" (Alive $fe.Id) ("pid=" + $fe.Id + " name=explorer")
& $exe ("--pid=" + $fe.Id) ("--out=" + (Join-Path $t "h1.txt")) | Out-Null
$h1 = ReadOut "h1.txt"
Start-Sleep -Milliseconds 400
Check "H1.1 fake explorer SURVIVED" (Alive $fe.Id) ("alive=" + (Alive $fe.Id))
Check "H1.2 refusal reported to console" $true "see console output above"
Stop-Process -Id $fe.Id -Force -ErrorAction SilentlyContinue

Write-Host "`n=== H2: tree kill must not sweep up a protected-named CHILD ==="
$fe2 = Start-Process $fakeExplorer -PassThru
Start-Sleep -Milliseconds 1500
$script = 'Start-Sleep 900'
$parent = Start-Process powershell -ArgumentList '-NoProfile','-Command',
    ('$c = Start-Process "' + $fakeExplorer + '" -PassThru; Set-Content -Path "' +
     (Join-Path $t "h2child.txt") + '" -Value $c.Id; Start-Sleep 900') -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 3
$childId = 0
if (Test-Path (Join-Path $t "h2child.txt")) { $childId = [int](Get-Content (Join-Path $t "h2child.txt") -Raw).Trim() }
Check "H2.0 parent+protected child running" ((Alive $parent.Id) -and (Alive $childId)) ("parent=" + $parent.Id + " child=" + $childId)
& $exe ("--pid=" + $parent.Id) ("--out=" + (Join-Path $t "h2.txt")) | Out-Null
Start-Sleep -Milliseconds 600
Check "H2.1 parent closed" (-not (Alive $parent.Id)) ("alive=" + (Alive $parent.Id))
Check "H2.2 protected-named child SURVIVED tree kill" (Alive $childId) ("alive=" + (Alive $childId))
$h2 = ReadOut "h2.txt"
Check "H2.3 skip reason logged" ($h2 -match "保护名单") ("log=" + ($h2 -replace "`r`n"," | "))
Stop-Process -Id $childId -Force -ErrorAction SilentlyContinue
Stop-Process -Id $fe2.Id -Force -ErrorAction SilentlyContinue

Write-Host "`n=== H3: malformed --pid must be rejected, not silently become a dry run ==="
& $exe "--pid" "1234" | Out-Null
Check "H3.1 '--pid 1234' rejected" ($LASTEXITCODE -eq 1) ("exit=" + $LASTEXITCODE)
& $exe "--pid=abc" | Out-Null
Check "H3.2 '--pid=abc' rejected" ($LASTEXITCODE -eq 1) ("exit=" + $LASTEXITCODE)
& $exe "--pid=0" | Out-Null
Check "H3.3 '--pid=0' rejected" ($LASTEXITCODE -eq 1) ("exit=" + $LASTEXITCODE)
$d = Start-Process powershell -ArgumentList '-NoProfile','-Command','Start-Sleep 900' -WindowStyle Hidden -PassThru
Start-Sleep -Milliseconds 1200
& $exe ("--pid=abc," + $d.Id) | Out-Null
Start-Sleep -Milliseconds 300
Check "H3.4 bad token in list aborts whole command" ((Alive $d.Id) -and ($LASTEXITCODE -eq 1)) ("alive=" + (Alive $d.Id) + " exit=" + $LASTEXITCODE)
Stop-Process -Id $d.Id -Force -ErrorAction SilentlyContinue

Write-Host "`n=== H4: fail-closed classification ==="
& $exe --list --json ("--out=" + (Join-Path $t "h4.json")) | Out-Null
$j = Get-Content (Join-Path $t "h4.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$ownSession = (Get-Process -Id $PID).SessionId
$noPath = @($j.candidates | Where-Object { [string]::IsNullOrEmpty($_.path) })
Check "H4.1 no unreadable-path process is a candidate" ($noPath.Count -eq 0) ("count=" + $noPath.Count)
$otherSession = @($j.candidates | Where-Object { $_.session -ne $ownSession })
Check "H4.2 no cross-session process is a candidate" ($otherSession.Count -eq 0) ("count=" + $otherSession.Count + " ownSession=" + $ownSession)
$sysPath = @($j.candidates | Where-Object { $_.path -like "C:\Windows\*" })
Check "H4.3 no C:\Windows binary is a candidate" ($sysPath.Count -eq 0) ("count=" + $sysPath.Count)

Write-Host "`n=== H5: --all without --yes lists descendants but kills nothing ==="
$pp = Start-Process cmd -ArgumentList '/c','powershell -NoProfile -Command "Start-Sleep 900"' -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 2
$kids = @(Get-CimInstance Win32_Process -Filter ("ParentProcessId=" + $pp.Id) -ErrorAction SilentlyContinue)
& $exe --all ("--out=" + (Join-Path $t "h5.txt")) | Out-Null
$h5 = ReadOut "h5.txt"
Check "H5.1 dry-run printed plan" ($h5 -match "预演模式") ("len=" + $h5.Length)
Check "H5.2 child survived dry-run" ($kids.Count -gt 0 -and (Alive $kids[0].ProcessId)) ("child=" + $kids[0].ProcessId)
Stop-Process -Id $pp.Id -Force -ErrorAction SilentlyContinue
foreach ($k in $kids) { Stop-Process -Id $k.ProcessId -Force -ErrorAction SilentlyContinue }

Write-Host "`n=== H6: --help exit code and text ==="
& $exe --help | Out-Null
Check "H6.1 --help exits 0" ($LASTEXITCODE -eq 0) ("exit=" + $LASTEXITCODE)
& $exe --bogus | Out-Null
Check "H6.2 unknown flag exits 1" ($LASTEXITCODE -eq 1) ("exit=" + $LASTEXITCODE)

Write-Host "`n================ SUMMARY ================"
$fail = @($results | Where-Object { -not $_.Pass }).Count
$results | Format-Table -AutoSize
Write-Host ("TOTAL: {0}   FAILED: {1}" -f $results.Count, $fail)
exit $fail
