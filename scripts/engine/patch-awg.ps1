param([Parameter(Mandatory)][string]$Go, [Parameter(Mandatory)][string]$Source)
$ErrorActionPreference = 'Stop'
$modulePath = & $Go list -C $Source -m -f '{{.Dir}}' github.com/amnezia-vpn/amneziawg-go/v3
if ($LASTEXITCODE -ne 0 -or !$modulePath) { throw 'Cannot locate pinned AWG module' }
$localModule = Join-Path $Source 'redroute-deps/amneziawg-go'
if (!(Test-Path -LiteralPath (Join-Path $localModule 'go.mod'))) {
    New-Item -ItemType Directory -Force (Split-Path $localModule -Parent) | Out-Null
    Copy-Item -LiteralPath $modulePath -Destination $localModule -Recurse
    Get-ChildItem -LiteralPath $localModule -Recurse -File | ForEach-Object { $_.IsReadOnly = $false }
}
$originalFile = Join-Path $localModule 'device/send.go'
$text = [IO.File]::ReadAllText($originalFile).Replace("`r`n", "`n")
$old = @'
		count, readErr = device.tun.device.Read(bufs, sizes, offset)
		for i := 0; i < count; i++ {
			if sizes[i] < 1 {
				continue
			}

			elem := elems[i]
			elem.packet = bufs[i][offset : offset+sizes[i]]
			elem.padding = padding
'@
$replacement = @'
		count, readErr = device.tun.device.Read(bufs, sizes, offset)
		// RED_ROUTE: configuration may change while the first TUN read is blocked.
		// Relocate payload before using the current S4, rather than dropping the first packet.
		currentPadding := device.paddings.transport.Load()
		currentOffset := MessageTransportHeaderSize + int(currentPadding)
		for i := 0; i < count; i++ {
			if sizes[i] < 1 || currentOffset+sizes[i] > len(bufs[i]) {
				continue
			}
			if currentOffset != offset {
				copy(bufs[i][currentOffset:currentOffset+sizes[i]], bufs[i][offset:offset+sizes[i]])
			}

			elem := elems[i]
			elem.packet = bufs[i][currentOffset : currentOffset+sizes[i]]
			elem.padding = currentPadding
'@
$old = $old.Replace("`r`n", "`n"); $replacement = $replacement.Replace("`r`n", "`n")
if (!$text.Contains('// RED_ROUTE: configuration may change while the first TUN read is blocked.')) {
    if (!$text.Contains($old)) { throw 'Pinned AWG first-packet patch no longer matches source' }
    [IO.File]::WriteAllText($originalFile, $text.Replace($old, $replacement), [Text.UTF8Encoding]::new($false))
}
& $Go mod edit -C $Source '-replace=github.com/amnezia-vpn/amneziawg-go/v3=./redroute-deps/amneziawg-go'
if ($LASTEXITCODE -ne 0) { throw 'Cannot select patched AWG sources' }
