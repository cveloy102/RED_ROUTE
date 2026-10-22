param([string]$Go = "$PSScriptRoot/../.tools/go/bin/go.exe")
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/..").Path
$revision = '16724484cfa00d9b2c14d8280dc055564e2e12ba'
$source = Join-Path $root 'vendor/amnezia-box'
$destination = Join-Path $root 'artifacts/engine'
New-Item -ItemType Directory -Force $source,$destination,(Join-Path $root '.tools') | Out-Null
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
# Persist the allocation cursor before the mapping, then commit the mapping before
# returning a DNS answer. A crash must never forget or reassign an address already cached by a browser.
$fakeIpFile = Join-Path $source 'dns/transport/fakeip/store.go'
$fakeIpText = [IO.File]::ReadAllText($fakeIpFile).Replace("`r`n", "`n")
if (!$fakeIpText.Contains('// RED_ROUTE: durable DNS answer before delivery.')) {
    $old = @'
	s.storage.FakeIPStoreAsync(address, domain, s.logger)
	s.storage.FakeIPSaveMetadataAsync(&adapter.FakeIPMetadata{
		Inet4Range:   s.inet4Range,
		Inet6Range:   s.inet6Range,
		Inet4Current: s.inet4Current,
		Inet6Current: s.inet6Current,
	})
'@
    $replacement = @'
	// RED_ROUTE: durable DNS answer before delivery.
	if err := s.storage.FakeIPSaveMetadata(&adapter.FakeIPMetadata{
		Inet4Range:   s.inet4Range,
		Inet6Range:   s.inet6Range,
		Inet4Current: s.inet4Current,
		Inet6Current: s.inet6Current,
	}); err != nil {
		return netip.Addr{}, err
	}
	if err := s.storage.FakeIPStore(address, domain); err != nil {
		return netip.Addr{}, err
	}
'@
    $old = $old.Replace("`r`n", "`n"); $replacement = $replacement.Replace("`r`n", "`n")
    if (!$fakeIpText.Contains($old)) { throw 'Pinned engine DNS durability patch no longer matches source' }
    [IO.File]::WriteAllText($fakeIpFile, $fakeIpText.Replace($old, $replacement), [Text.UTF8Encoding]::new($false))
}
$fakeIpText = [IO.File]::ReadAllText($fakeIpFile).Replace("`r`n", "`n")
if (!$fakeIpText.Contains('allocationAccess sync.Mutex')) {
    $createSignature = 'func (s *Store) Create(domain string, isIPv6 bool) (netip.Addr, error) {'
    if (!$fakeIpText.Contains($createSignature)) { throw 'Pinned engine DNS allocation lock patch no longer matches source' }
    $fakeIpText = $fakeIpText.Replace('"net/netip"', "`"net/netip`"`n`t`"sync`"")
    $fakeIpText = $fakeIpText.Replace('type Store struct {', "type Store struct {`n`tallocationAccess sync.Mutex")
    $fakeIpText = $fakeIpText.Replace($createSignature, "$createSignature`n`ts.allocationAccess.Lock()`n`tdefer s.allocationAccess.Unlock()")
    $fakeIpText = $fakeIpText.Replace('func (s *Store) Close() error {', "func (s *Store) Close() error {`n`ts.allocationAccess.Lock()`n`tdefer s.allocationAccess.Unlock()")
    [IO.File]::WriteAllText($fakeIpFile, $fakeIpText, [Text.UTF8Encoding]::new($false))
}
Copy-Item -LiteralPath "$PSScriptRoot/engine/fakeip_store_test.go" -Destination "$source/dns/transport/fakeip/redroute_store_test.go" -Force
& $Go test -C $source ./dns/transport/fakeip -run '^TestRedRoute' -count=1
if ($LASTEXITCODE -ne 0) { throw 'DNS crash durability checks failed' }
& $Go build -C $source -tags 'with_gvisor,with_quic,with_wireguard,with_awg' -trimpath -ldflags '-s -w -X github.com/sagernet/sing-box/constant.Version=1.260910.0-redroute3' -o "$destination/amnezia-box.exe" ./cmd/sing-box
if ($LASTEXITCODE -ne 0) { throw 'VPN core build failed' }
Copy-Item -LiteralPath "$source/LICENSE" -Destination "$destination/AMNEZIA-BOX-LICENSE.txt"
& $Go version -m "$destination/amnezia-box.exe" | Set-Content "$destination/build-info.txt"
& "$destination/amnezia-box.exe" version
