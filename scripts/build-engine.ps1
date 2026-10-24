param([string]$Go = "$PSScriptRoot/../.tools/go1.26.10/go/bin/go.exe")
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
# Pin fixes reported by govulncheck; preserve them in the distributed corresponding source.
& $Go get -C $source golang.org/x/net@v0.60.0 golang.org/x/crypto@v0.57.0 golang.org/x/text@v0.42.0
if ($LASTEXITCODE -ne 0) { throw 'Cannot apply pinned Go dependency security updates' }
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
# Loading a durable cursor must not consume it. A restart which reuses only existing
# mappings may crash without ever allocating a new address or calling Store.Close.
$cacheFile = Join-Path $source 'experimental/cachefile/fakeip.go'
$cacheText = [IO.File]::ReadAllText($cacheFile).Replace("`r`n", "`n")
if (!$cacheText.Contains('// RED_ROUTE: metadata reads are non-destructive.')) {
    $old = @'
		err := bucket.Delete(keyMetadata)
		if err != nil {
			return err
		}
		return metadata.UnmarshalBinary(metadataBinary)
'@
    $replacement = @'
		// RED_ROUTE: metadata reads are non-destructive.
		return metadata.UnmarshalBinary(metadataBinary)
'@
    $old = $old.Replace("`r`n", "`n"); $replacement = $replacement.Replace("`r`n", "`n")
    if (!$cacheText.Contains($old)) { throw 'Pinned engine DNS metadata read patch no longer matches source' }
    $cacheText = $cacheText.Replace($old, $replacement)
    $cacheText = $cacheText.Replace("var metadata adapter.FakeIPMetadata`n`terr := c.DB.Batch", "var metadata adapter.FakeIPMetadata`n`terr := c.DB.View")
    [IO.File]::WriteAllText($cacheFile, $cacheText, [Text.UTF8Encoding]::new($false))
}
Copy-Item -LiteralPath "$PSScriptRoot/engine/fakeip_cache_test.go" -Destination "$source/experimental/cachefile/redroute_fakeip_test.go" -Force
& "$PSScriptRoot/engine/patch-ipv6.ps1" -Source $source
& "$PSScriptRoot/engine/patch-awg.ps1" -Go $Go -Source $source
& $Go test -C $source ./dns/transport/fakeip ./experimental/cachefile ./protocol/awg ./route -count=1 -timeout=90s
if ($LASTEXITCODE -ne 0) { throw 'DNS crash durability checks failed' }
& $Go test -C $source github.com/amnezia-vpn/amneziawg-go/v3/device -run '^(TestTwoDevicePing|TestAWGDevicePing|TestNoiseHandshake|TestConcurrencySafety|TestUpDown)$' -count=1 -timeout=90s
if ($LASTEXITCODE -ne 0) { throw 'AWG packet and concurrency checks failed' }
& $Go build -C $source -tags 'with_gvisor,with_quic,with_wireguard,with_awg' -trimpath -ldflags '-s -w -X github.com/sagernet/sing-box/constant.Version=1.260910.0-redroute6' -o "$destination/amnezia-box.exe" ./cmd/sing-box
if ($LASTEXITCODE -ne 0) { throw 'VPN core build failed' }
New-Item -ItemType Directory -Force "$root/artifacts/checks" | Out-Null
& $Go build -C $source -trimpath -o "$root/artifacts/checks/redroute-lab-server.exe" "$root/tools/native-lab-server/main.go"
if ($LASTEXITCODE -ne 0) { throw 'Userspace integration fixture build failed' }
Copy-Item -LiteralPath "$source/LICENSE" -Destination "$destination/AMNEZIA-BOX-LICENSE.txt"
& $Go version -m "$destination/amnezia-box.exe" | Set-Content "$destination/build-info.txt"
& "$destination/amnezia-box.exe" version
