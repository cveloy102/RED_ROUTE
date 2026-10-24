param([string]$Go = "$PSScriptRoot/../.tools/go1.26.10/go/bin/go.exe")
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/..").Path
if (!(Test-Path -LiteralPath $Go)) { $Go = (Get-Command go -ErrorAction Stop).Source }
$previousCases = $env:RED_ROUTE_ROUTING_CASES
try {
    $env:RED_ROUTE_ROUTING_CASES = Join-Path $root 'artifacts/checks/ipv6-routing-cases.json'
    if (!(Test-Path -LiteralPath $env:RED_ROUTE_ROUTING_CASES)) { throw 'Run the C# checks before the generated IPv6 route checks' }
    $env:GOTOOLCHAIN = 'local'; $env:GOWORK = 'off'; $env:CGO_ENABLED = '0'
    $env:GOOS = 'windows'; $env:GOARCH = 'amd64'
    $env:GOPATH = Join-Path $root '.tools/gopath'; $env:GOCACHE = Join-Path $root '.tools/gocache'
    & $Go test -C "$root/vendor/amnezia-box" ./route -run '^TestRedRoute' -count=1 -timeout=60s -v
    if ($LASTEXITCODE -ne 0) { throw 'Generated IPv6 routing regression failed' }
} finally { $env:RED_ROUTE_ROUTING_CASES = $previousCases }
