param([switch]$SkipEngine, [switch]$SkipInstaller)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/..").Path
$runtimeVersion = '10.0.13'
Push-Location $root
try {
    if (!$SkipEngine) { & "$PSScriptRoot/build-engine.ps1" }
    dotnet build RED_ROUTE.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    dotnet run --project tests/RedRoute.Tests -c Release --no-build -- --native-lab
    if ($LASTEXITCODE -ne 0) { throw 'Checks failed' }
    & "$PSScriptRoot/test-ipv6.ps1"
    $publish = Join-Path $root 'artifacts/publish'
    dotnet publish src/RedRoute.App -c Release -r win-x64 --self-contained true -o $publish -p:PublishSingleFile=false -p:RuntimeFrameworkVersion=$runtimeVersion --nologo
    if ($LASTEXITCODE -ne 0) { throw 'UI publish failed' }
    dotnet publish src/RedRoute.Service -c Release -r win-x64 --self-contained true -o "$publish/service" -p:PublishSingleFile=false -p:RuntimeFrameworkVersion=$runtimeVersion --nologo
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
        dotnet publish src/RedRoute.Setup -c Release -r win-x64 --self-contained true -o "$root/artifacts/setup" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:RuntimeFrameworkVersion=$runtimeVersion --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Setup launcher build failed' }
        Copy-Item -LiteralPath "$root/artifacts/setup/RED_ROUTE-Setup.exe" -Destination "$root/artifacts/RED_ROUTE-$version-Setup.exe" -Force
        $setupHash = (Get-FileHash -LiteralPath "$root/artifacts/RED_ROUTE-$version-Setup.exe" -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText("$root/artifacts/RED_ROUTE-$version-Setup.exe.sha256", "$setupHash  RED_ROUTE-$version-Setup.exe`n", [Text.UTF8Encoding]::new($false))
        $engineSource = "$root/artifacts/RED_ROUTE-$version-engine-source.zip"
        if (Test-Path -LiteralPath $engineSource) { Remove-Item -LiteralPath $engineSource }
        [IO.Compression.ZipFile]::CreateFromDirectory("$root/vendor/amnezia-box", $engineSource)
    }
} finally { Pop-Location }
