<#
.SYNOPSIS
    Checks connection to online servers and VPN tunnels securely without exposing credentials.
#>

param(
    [string]$RemoteHost = "109.123.238.232",
    [int[]]$Ports = @(80, 1433, 3389, 8080, 22),
    [string]$WebEndpoint = "https://dhalor-remote-task-desk.urgptcry.chatgpt.site"
)

$ErrorActionPreference = 'SilentlyContinue'

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "   Online Server Connectivity Check     " -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# 1. WireGuard Tunnel Status
Write-Host "[1/3] WireGuard VPN Tunnel:" -ForegroundColor Yellow
$wgAdapters = Get-NetAdapter | Where-Object { $_.InterfaceDescription -match 'WireGuard' -or $_.Name -match 'WireGuard|VCON' }
if ($wgAdapters) {
    foreach ($adapter in $wgAdapters) {
        $color = if ($adapter.Status -eq 'Up') { 'Green' } else { 'Red' }
        Write-Host "  * Interface : $($adapter.Name) ($($adapter.InterfaceDescription))"
        Write-Host "  * Status    : $($adapter.Status)" -ForegroundColor $color
    }
} else {
    Write-Host "  * No active WireGuard adapter detected." -ForegroundColor DarkGray
}

# 2. Remote Server Ports
Write-Host "`n[2/3] Remote Server ($RemoteHost):" -ForegroundColor Yellow
foreach ($port in $Ports) {
    $res = Test-NetConnection -ComputerName $RemoteHost -Port $port -WarningAction SilentlyContinue
    $statusText = if ($res.TcpTestSucceeded) { "ONLINE" } else { "OFFLINE" }
    $color = if ($res.TcpTestSucceeded) { "Green" } else { "DarkGray" }
    
    $serviceName = switch ($port) {
        80   { "HTTP Web" }
        443  { "HTTPS Web" }
        1433 { "MSSQL Database" }
        3389 { "Remote Desktop (RDP)" }
        8080 { "App Web Service" }
        22   { "SSH Management" }
        default { "Port $port" }
    }
    Write-Host ("  * {0,-22} (Port {1,5}) : {2}" -f $serviceName, $port, $statusText) -ForegroundColor $color
}

# 3. Web Application Endpoint
Write-Host "`n[3/3] Web Application Service:" -ForegroundColor Yellow
$httpCode = curl.exe -s -o NUL -w "%{http_code}" --connect-timeout 5 $WebEndpoint
if ($httpCode -and $httpCode -ne "000") {
    Write-Host "  * $WebEndpoint : ONLINE (HTTP $httpCode)" -ForegroundColor Green
} else {
    Write-Host "  * $WebEndpoint : UNREACHABLE" -ForegroundColor Red
}

Write-Host "`nDiagnostics complete.`n" -ForegroundColor Cyan
