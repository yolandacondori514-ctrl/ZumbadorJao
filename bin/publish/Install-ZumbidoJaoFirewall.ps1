[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string]$TrustedSubnet
)

$ErrorActionPreference = 'Stop'

$cidrParts = $TrustedSubnet.Split('/')
if ($cidrParts.Count -ne 2) {
	throw "La subred '$TrustedSubnet' no tiene formato CIDR IPv4, por ejemplo 192.168.10.0/24."
}

$ipAddress = $null
if (-not [System.Net.IPAddress]::TryParse($cidrParts[0], [ref]$ipAddress)) {
	throw "La dirección '$($cidrParts[0])' no es una dirección IPv4 válida."
}
if ($ipAddress.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
	throw "La dirección '$($cidrParts[0])' no es una dirección IPv4 válida."
}

$prefixLength = 0
if (-not [int]::TryParse($cidrParts[1], [ref]$prefixLength) -or
	$prefixLength -lt 0 -or $prefixLength -gt 32) {
	throw "El prefijo '$($cidrParts[1])' debe estar entre 0 y 32."
}

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
	throw 'Ejecuta PowerShell como administrador para configurar el Firewall de Windows.'
}

$ruleName = 'ZumbidoJao - TCP 8080 - Red autorizada'

Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule `
	-DisplayName $ruleName `
	-Direction Inbound `
	-Action Allow `
	-Protocol TCP `
	-LocalPort 8080 `
	-RemoteAddress $TrustedSubnet `
	-Profile Domain,Private `
	-Description 'Permite solicitudes ZumbidoJao únicamente desde la subred autorizada.' | Out-Null

Write-Host "Regla creada para la subred autorizada: $TrustedSubnet"
Write-Host 'No se habilita el puerto en perfiles Public.'
