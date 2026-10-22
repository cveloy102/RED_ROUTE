param([string]$Go = "$PSScriptRoot/../.tools/go/bin/go.exe")
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/..").Path
$revision = '16724484cfa00d9b2c14d8280dc055564e2e12ba'
$source = Join-Path $root 'vendor/amnezia-box'
$destination = Join-Path $root 'artifacts/engine'
New-Item -ItemType Directory -Force $source,$destination | Out-Null
if (!(Test-Path "$source/go.mod")) {
    $archive = Join-Path $root '.tools/amnezia-box.zip'
    Invoke-WebRequest "https://github.com/amnezia-vpn/amnezia-box/archive/$revision.zip" -OutFile $archive
    Expand-Archive -LiteralPath $archive -DestinationPath "$root/vendor" -Force
    $extracted = Join-Path $root "vendor/amnezia-box-$revision"
    Get-ChildItem -LiteralPath $extracted -Force | Move-Item -Destination $source
}
$env:GOTOOLCHAIN = 'local'
$env:GOWORK = 'off'
$env:CGO_ENABLED = '0'
$env:GOOS = 'windows'
$env:GOARCH = 'amd64'
$env:GOPATH = Join-Path $root '.tools/gopath'
$env:GOCACHE = Join-Path $root '.tools/gocache'
# Official AWG endpoint formatter needs brackets for an IPv6 endpoint literal.
$endpointFile = Join-Path $source 'protocol/awg/endpoint.go'
$endpointText = [IO.File]::ReadAllText($endpointFile)
$endpointText = $endpointText.Replace('peer.Address + ":" + format.ToString(peer.Port)', 'net.JoinHostPort(peer.Address, format.ToString(peer.Port))')
[IO.File]::WriteAllText($endpointFile, $endpointText, [Text.UTF8Encoding]::new($false))
$runFile = Join-Path $source 'cmd/sing-box/cmd_run.go'
$runText = [IO.File]::ReadAllText($runFile)
if (!$runText.Contains('redRouteControl(osSignals)')) {
    $original = "func run() error {`n`tosSignals := make(chan os.Signal, 1)"
    if (!$runText.Contains($original)) { throw 'Pinned engine shutdown patch no longer matches source' }
    $runText = $runText.Replace($original, "$original`n`tredRouteControl(osSignals)")
    [IO.File]::WriteAllText($runFile, $runText, [Text.UTF8Encoding]::new($false))
}
Copy-Item -LiteralPath "$PSScriptRoot/engine/redroute_control.go" -Destination "$source/cmd/sing-box/redroute_control.go" -Force
& $Go build -C $source -tags 'with_gvisor,with_quic,with_wireguard,with_awg' -trimpath -ldflags '-s -w -X github.com/sagernet/sing-box/constant.Version=1.260910.0-redroute1' -o "$destination/amnezia-box.exe" ./cmd/sing-box
if ($LASTEXITCODE -ne 0) { throw 'VPN core build failed' }
Copy-Item -LiteralPath "$source/LICENSE" -Destination "$destination/AMNEZIA-BOX-LICENSE.txt"
& $Go version -m "$destination/amnezia-box.exe" | Set-Content "$destination/build-info.txt"
& "$destination/amnezia-box.exe" version
