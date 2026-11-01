$stage = 'D:\Projects\rzswitch\bin\Release\net10.0\win-x64\publish\rzswitch.exe'
$stageTracker = 'D:\Projects\rzswitch\bin\Release\net10.0\win-x64\publish\rztracker.exe'
$dest  = 'D:\Projects\rzswitch\publish\rzswitch.exe'
$destTracker = 'D:\Projects\rzswitch\publish\rztracker.exe'
$out   = 'D:\Projects\rzswitch\deploy-log.txt'

function Log { param($m) $m | Add-Content $out; Write-Host $m }

Log "=== Deploy $(Get-Date) ==="
Log "Stopping service..."
net stop rzswitch 2>&1 | ForEach-Object { Log $_ }
Start-Sleep 3

Get-CimInstance Win32_Process -Filter "Name='rztracker.exe'" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; Log "killed rztracker $($_.ProcessId)" }

Log "Copying..."
Copy-Item $stage $dest -Force
Log "Copied $([math]::Round((Get-Item $dest).Length/1KB,0)) KB"

Log "Copying tracker..."
Copy-Item $stageTracker $destTracker -Force
Log "Copied $([math]::Round((Get-Item $destTracker).Length/1KB,0)) KB"

Log "Starting service..."
net start rzswitch 2>&1 | ForEach-Object { Log $_ }
Start-Sleep 3

$p = Get-Process -Name rzswitch -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq 0 }
if ($p) { Log "Service: PID=$($p.Id) WS=$([math]::Round($p.WorkingSet64/1MB,1))MB" }