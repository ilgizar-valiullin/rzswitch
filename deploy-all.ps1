$stageSvc = 'D:\Projects\rzswitch\bin\Release\net10.0\win-x64\publish\rzswitch.exe'
$stageSetup = 'D:\Projects\rzswitch\setup\bin\Release\net10.0-windows\win-x64\publish\rzswitch-setup.exe'
$stageTracker = 'D:\Projects\rzswitch\bin\Release\net10.0\win-x64\publish\rztracker.exe'
$destSvc = 'D:\Projects\rzswitch\publish\rzswitch.exe'
$destSetup = 'D:\Projects\rzswitch\publish\rzswitch-setup.exe'
$destTracker = 'D:\Projects\rzswitch\publish\rztracker.exe'
$out   = 'D:\Projects\rzswitch\deploy-log.txt'

function Log { param($m) $m | Add-Content $out; Write-Host $m }

Log "=== Deploy All $(Get-Date) ==="

Log "Stopping service..."
net stop rzswitch 2>&1 | ForEach-Object { Log $_ }
Start-Sleep 3

Get-CimInstance Win32_Process -Filter "Name='rztracker.exe'" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; Log "killed rztracker $($_.ProcessId)" }

Log "Copying service..."
Copy-Item $stageSvc $destSvc -Force
Log "Copied $([math]::Round((Get-Item $destSvc).Length/1KB,0)) KB"

Log "Copying setup..."
Copy-Item $stageSetup $destSetup -Force
Log "Copied $([math]::Round((Get-Item $destSetup).Length/1KB,0)) KB"

Log "Copying tracker..."
Copy-Item $stageTracker $destTracker -Force
Log "Copied $([math]::Round((Get-Item $destTracker).Length/1KB,0)) KB"

Log "Starting service..."
net start rzswitch 2>&1 | ForEach-Object { Log $_ }
Start-Sleep 5

$p = Get-Process -Name rzswitch -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq 0 }
if ($p) {
    Log "Service: PID=$($p.Id) WS=$([math]::Round($p.WorkingSet64/1MB,1))MB Commit=$([math]::Round($p.PagedMemorySize64/1MB,1))MB VM=$([math]::Round($p.VirtualMemorySize64/1GB,2))GB"
}