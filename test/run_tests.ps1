# Acceptance tests for the "one click close background apps" EXE.
# Uses throwaway dummy processes only; never touches real system processes.
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
    if (-not (Test-Path $p)) { return "" }
    $c = Get-Content $p -Raw -Encoding UTF8
    if ($null -eq $c) { return "" }
    return [string]$c
}

Write-Host "`n=== T1: build a windowed victim app (test fixture) ==="
$victimExe = Join-Path $t "VictimApp.exe"
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe ("/out:" + $victimExe) /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $root "tools\Victim.cs") | Out-Null
Check "T1.1 victim fixture built" (Test-Path $victimExe) $victimExe

Write-Host "`n=== T2: force-kill a windowless (background) process by PID ==="
$dummy = Start-Process powershell -ArgumentList '-NoProfile','-Command','Start-Sleep 900' -WindowStyle Hidden -PassThru
$dummyId = $dummy.Id
Start-Sleep -Milliseconds 1500
Check "T2.0 dummy started" (Alive $dummyId) ("pid=" + $dummyId)
& $exe ("--pid=" + $dummyId) ("--out=" + (Join-Path $t "t2.txt")) | Out-Null
Start-Sleep -Milliseconds 500
Check "T2.1 dummy killed" (-not (Alive $dummyId)) ("alive=" + (Alive $dummyId))
$t2 = ReadOut "t2.txt"
Check "T2.2 result reported" ($t2 -match "成功") ("log len=" + $t2.Length)

Write-Host "`n=== T3: graceful close (WM_CLOSE) of a windowed app ==="
$victim = Start-Process $victimExe -PassThru
$victimId = $victim.Id
Start-Sleep -Milliseconds 2000
Check "T3.0 victim started" (Alive $victimId) ("pid=" + $victimId)
& $exe ("--pid=" + $victimId) ("--out=" + (Join-Path $t "t3.txt")) | Out-Null
Start-Sleep -Milliseconds 500
Check "T3.1 victim closed" (-not (Alive $victimId)) ("alive=" + (Alive $victimId))
$t3 = ReadOut "t3.txt"
$graceful = [bool]($t3 -match "正常退出")
Check "T3.2 closed gracefully (not force-killed)" $graceful "see t3.txt"

Write-Host "`n=== T4: process tree kill (child must die with its parent) ==="
$parent = Start-Process cmd -ArgumentList '/c','powershell -NoProfile -Command "Start-Sleep 900"' -WindowStyle Hidden -PassThru
$parentId = $parent.Id
Start-Sleep -Seconds 3
$childId = 0
$ci = Get-CimInstance Win32_Process -Filter ("ParentProcessId=" + $parentId) -ErrorAction SilentlyContinue | Select-Object -First 1
if ($ci) { $childId = [int]$ci.ProcessId }
Check "T4.0 parent+child started" ((Alive $parentId) -and (Alive $childId)) ("parent=" + $parentId + " child=" + $childId)
& $exe ("--pid=" + $parentId) ("--out=" + (Join-Path $t "t4.txt")) | Out-Null
Start-Sleep -Milliseconds 800
Check "T4.1 parent killed" (-not (Alive $parentId)) ("alive=" + (Alive $parentId))
Check "T4.2 child killed too (tree kill)" (-not (Alive $childId)) ("alive=" + (Alive $childId))

Write-Host "`n=== T5: self / parent-chain protection ==="
$me = $PID
& $exe ("--pid=" + $me) ("--out=" + (Join-Path $t "t5.txt")) | Out-Null
Start-Sleep -Milliseconds 500
Check "T5.1 ancestor survived" (Alive $me) ("powershell pid=" + $me)
$t5 = ReadOut "t5.txt"
$refused = [bool]($t5 -match "不能关闭")
Check "T5.2 refused with explicit reason" $refused "see t5.txt"

Write-Host "`n=== T6: non-existent PID ==="
& $exe "--pid=999990" ("--out=" + (Join-Path $t "t6.txt")) | Out-Null
Check "T6.1 handled gracefully, exit 0" ($LASTEXITCODE -eq 0) ("exit=" + $LASTEXITCODE)

Write-Host "`n=== T7: --all without --yes must NOT kill anything ==="
$dummy2 = Start-Process powershell -ArgumentList '-NoProfile','-Command','Start-Sleep 900' -WindowStyle Hidden -PassThru
$dummy2Id = $dummy2.Id
Start-Sleep -Milliseconds 1500
& $exe --all ("--out=" + (Join-Path $t "t7.txt")) | Out-Null
Start-Sleep -Milliseconds 500
Check "T7.1 dummy still alive after dry-run" (Alive $dummy2Id) ("alive=" + (Alive $dummy2Id))
$t7 = ReadOut "t7.txt"
Check "T7.2 printed the plan" (($t7 -match "预演模式") -or ($t7 -match "扫描报告")) ("len=" + $t7.Length)
Stop-Process -Id $dummy2Id -Force -ErrorAction SilentlyContinue

Write-Host "`n=== T8: system-critical protection (no candidate may be a critical process) ==="
& $exe --list --json ("--out=" + (Join-Path $t "t8.json")) | Out-Null
$j = Get-Content (Join-Path $t "t8.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$critical = @("csrss","winlogon","lsass","dwm","explorer","svchost","services","wininit","smss",
              "fontdrvhost","ctfmon","sihost","taskhostw","conhost","SecurityHealthService",
              "StartMenuExperienceHost","TextInputHost","RuntimeBroker","audiodg","WmiPrvSE","spoolsv")
$candNames = @($j.candidates | ForEach-Object { $_.name })
$leak = @($candNames | Where-Object { $critical -contains $_ })
Check "T8.1 no critical process in candidates" ($leak.Count -eq 0) ("leaked=" + ($leak -join ","))
$selfProtected = @($j.protected | Where-Object { $_.pid -eq $j.selfPid })
Check "T8.2 self is protected" ($selfProtected.Count -eq 1) ("selfPid=" + $j.selfPid)
$ancProtected = @($j.protected | Where-Object { $_.reason -match "父进程" })
Check "T8.3 parent chain protected" ($ancProtected.Count -ge 1) ("count=" + $ancProtected.Count)

Write-Host "`n=== T9: kill the same PID twice (idempotent / no crash) ==="
$d3 = Start-Process powershell -ArgumentList '-NoProfile','-Command','Start-Sleep 900' -WindowStyle Hidden -PassThru
$d3Id = $d3.Id
Start-Sleep -Milliseconds 1200
& $exe ("--pid=" + $d3Id) | Out-Null
$e1 = $LASTEXITCODE
& $exe ("--pid=" + $d3Id) | Out-Null
$e2 = $LASTEXITCODE
Check "T9.1 both runs clean" (($e1 -eq 0) -and ($e2 -eq 0)) ("exit1=" + $e1 + " exit2=" + $e2)

Write-Host "`n================ SUMMARY ================"
$fail = @($results | Where-Object { -not $_.Pass }).Count
$results | Format-Table -AutoSize
Write-Host ("TOTAL: {0}   FAILED: {1}" -f $results.Count, $fail)
exit $fail
