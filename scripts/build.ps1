param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$artifact = Join-Path $workspace 'artifacts\portable'
$project = Join-Path $workspace 'src\Nexus.App\Nexus.App.csproj'
$launcherProject = Join-Path $workspace 'src\Dagnue.Launcher\Dagnue.Launcher.csproj'
$launcherOutput = Join-Path $workspace '.generated\launcher'
$rootLauncher = Join-Path $workspace 'DAGNUE.exe'
$framework = 'net9.0-windows10.0.26100.0'

Push-Location $workspace
try {
    dotnet restore Nexus.slnx
    dotnet test tests\Nexus.Core.Tests\Nexus.Core.Tests.csproj -c $Configuration --no-restore
    # The WinUI 2.3 publish target currently emits a self-contained deps file without
    # the CoreCLR runtime-pack entry. The self-contained build output is complete and
    # is the directory copied into the portable artifact below.
    dotnet build $project -c $Configuration -r win-x64 --self-contained true `
        -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true `
        -p:EnableMsixTooling=false -p:EnableWinAppRunSupport=false -p:PublishTrimmed=false `
        -p:SyncPortableAfterBuild=false

    $source = Join-Path $workspace "src\Nexus.App\bin\$Configuration\$framework\win-x64"
    if (Test-Path $artifact) { Remove-Item -LiteralPath $artifact -Recurse -Force }
    New-Item -ItemType Directory -Path $artifact | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $artifact -Recurse -Force
    dotnet publish $launcherProject -c $Configuration -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -o $launcherOutput
    Copy-Item -LiteralPath (Join-Path $launcherOutput 'DAGNUE.exe') -Destination $rootLauncher -Force
    Write-Host "Portable build: $artifact\Nexus.App.exe"
    Write-Host "Root launcher:  $rootLauncher"
}
finally {
    Pop-Location
}
