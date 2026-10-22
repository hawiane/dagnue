param(
    [Parameter(Mandatory=$true)] [string]$PackagePath,
    [Parameter(Mandatory=$true)] [string]$CertificatePath
)

$ErrorActionPreference = 'Stop'
$certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CertificatePath)
$store = New-Object System.Security.Cryptography.X509Certificates.X509Store('TrustedPeople','CurrentUser')
$store.Open('ReadWrite')
try { $store.Add($certificate) } finally { $store.Close() }
Add-AppxPackage -Path $PackagePath
Write-Host 'NEXUS installed for the current Windows user.'
