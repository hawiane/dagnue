param(
    [Parameter(Mandatory=$true)]
    [string]$CertificatePath,
    [ValidateSet('x64','arm64')]
    [string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
if (-not $env:NEXUS_CERT_PASSWORD) { throw 'Set NEXUS_CERT_PASSWORD before packaging.' }
$workspace = Split-Path -Parent $PSScriptRoot
$output = Join-Path $workspace 'artifacts\msix'
$artifactRoot = Join-Path $workspace 'artifacts'
$stagingPrefix = Join-Path $artifactRoot ("msix-staging-{0}-" -f [Guid]::NewGuid().ToString('N'))
$project = Join-Path $workspace 'src\Nexus.App\Nexus.App.csproj'
New-Item -ItemType Directory -Force -Path $output | Out-Null

Push-Location $workspace
try {
    dotnet publish $project -c Release -r "win-$Architecture" --no-restore `
        -p:Platform=$Architecture -p:GenerateAppxPackageOnBuild=true -p:AppxBundle=Never `
        -p:AppxPackageSigningEnabled=true -p:PackageCertificateKeyFile=$CertificatePath `
        -p:PackageCertificatePassword=$env:NEXUS_CERT_PASSWORD -p:AppxPackageDir=$stagingPrefix

    $generated = Get-ChildItem $artifactRoot -Directory | Where-Object { $_.FullName.StartsWith($stagingPrefix) }
    foreach ($directory in $generated) {
        $packageName = $directory.Name.Substring((Split-Path $stagingPrefix -Leaf).Length)
        Move-Item -LiteralPath $directory.FullName -Destination (Join-Path $output $packageName)
    }
}
finally {
    Pop-Location
}
