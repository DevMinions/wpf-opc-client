# diagnose-run.ps1 - find out why the NEW (x64) Dc.App build shows no window
# Run from the wpf\ folder:  .\diagnose-run.ps1
# ASCII-only on purpose (avoid PS 5.1 encoding issues).

$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot

function Get-LatestLog {
    Get-ChildItem "$root\src\Dc.App\bin\*\net8.0-windows\logs\dc-*.log" -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
}

Write-Host ""
Write-Host "==== 0. Which Dc.App.exe builds exist (newest first) ====" -ForegroundColor Cyan
Get-ChildItem "$root\src\Dc.App\bin\*\net8.0-windows\Dc.App.exe" -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object @{n='Path';e={$_.FullName.Replace($root,'')}}, LastWriteTime, Length |
    Format-Table -AutoSize

Write-Host "==== 1. Kill ALL running Dc.App / dotnet (clear single-instance + zombies) ====" -ForegroundColor Cyan
Get-Process Dc.App -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ("  kill Dc.App pid " + $_.Id) -ForegroundColor Yellow; $_ | Stop-Process -Force }
Get-Process dotnet  -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ("  kill dotnet  pid " + $_.Id) -ForegroundColor DarkYellow; $_ | Stop-Process -Force }
Start-Sleep -Milliseconds 600

Write-Host ""
Write-Host "==== 2. Launch the x64 build (the NEW UI) directly ====" -ForegroundColor Cyan
$exe = Join-Path $root "src\Dc.App\bin\x64\Debug\net8.0-windows\Dc.App.exe"
if (-not (Test-Path $exe)) {
    Write-Host "x64 build not found at: $exe" -ForegroundColor Red
    Write-Host "Build it first:  .\build.ps1" -ForegroundColor Red
    return
}
Write-Host ("launching: " + $exe) -ForegroundColor DarkGray
$t0 = Get-Date
$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
Start-Sleep -Seconds 6
if ($p.HasExited) {
    Write-Host ("PROCESS EXITED (code " + $p.ExitCode + ") - the NEW build crashed/closed at startup.") -ForegroundColor Red
} else {
    Write-Host ("PROCESS STILL RUNNING (pid " + $p.Id + ") - NEW UI window should be visible (or in tray).") -ForegroundColor Green
}

Write-Host ""
Write-Host "==== 3. Serilog log tail (post-mutex startup) ====" -ForegroundColor Cyan
$log = Get-LatestLog
if ($log) { Write-Host ("log: " + $log.FullName) -ForegroundColor DarkGray; Get-Content $log.FullName -Tail 25 }
else { Write-Host "(no log file)" -ForegroundColor Yellow }

Write-Host ""
Write-Host "==== 4. Windows Application event-log errors since launch (catches pre-log XAML crashes) ====" -ForegroundColor Cyan
$errs = Get-EventLog -LogName Application -EntryType Error -After $t0.AddSeconds(-2) -ErrorAction SilentlyContinue |
        Where-Object { $_.Source -match 'Application Error|\.NET Runtime|Windows Error Reporting' }
if ($errs) {
    foreach ($e in $errs) {
        Write-Host ("--- " + $e.TimeGenerated + " [" + $e.Source + "] ---") -ForegroundColor Red
        Write-Host $e.Message
    }
} else {
    Write-Host "(no Application event-log errors in this window)" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "==== DONE. Copy ALL output above and send it back. ====" -ForegroundColor Cyan
