param([switch]$SkipEngine, [switch]$SkipInstaller)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/..").Path
Push-Location $root
try {
    if (!$SkipEngine) { & "$PSScriptRoot/build-engine.ps1" }
    dotnet build RED_ROUTE.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    dotnet run --project tests/RedRoute.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Checks failed' }
    $publish = Join-Path $root 'artifacts/publish'
    dotnet publish src/RedRoute.App -c Release -r win-x64 --self-contained true -o $publish -p:PublishSingleFile=false --nologo
    if ($LASTEXITCODE -ne 0) { throw 'UI publish failed' }
    dotnet publish src/RedRoute.Service -c Release -r win-x64 --self-contained true -o "$publish/service" -p:PublishSingleFile=false --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Service publish failed' }
    New-Item -ItemType Directory -Force "$publish/service/engine" | Out-Null
    Copy-Item "$root/artifacts/engine/*" "$publish/service/engine/" -Force
    Copy-Item "$root/THIRD-PARTY.md" $publish -Force
    if (!$SkipInstaller) {
        dotnet build installer/RedRoute.Installer.wixproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
        $version = ([xml](Get-Content -LiteralPath "$root/Directory.Build.props" -Raw)).Project.PropertyGroup.Version
        $package = Get-Item -LiteralPath "$root/installer/bin/Release/RED_ROUTE-$version-win-x64.msi"
        & "$PSScriptRoot/check-installer.ps1" -Path $package.FullName
        Copy-Item -LiteralPath $package.FullName -Destination "$root/artifacts/$($package.Name)" -Force
        Get-FileHash "$root/artifacts/$($package.Name)" -Algorithm SHA256 | Format-List
    }
} finally { Pop-Location }
