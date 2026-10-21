[CmdletBinding()]

$ErrorActionPreference = 'Stop'
$ruleName = 'ZumbidoJao - TCP 8080 - Red autorizada'
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule

Write-Host 'Regla de Firewall retirada.'
Write-Host 'El directorio de la aplicación puede eliminarse después de cerrar ZumbidoJao.'
