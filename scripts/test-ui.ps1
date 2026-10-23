$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/..").Path
$app = Join-Path $root 'src/RedRoute.App/bin/Release/net10.0-windows/RED_ROUTE.exe'
$result = Join-Path $root 'artifacts/ui/result.txt'
function Invoke-Smoke([string[]]$Arguments, [int]$ExpectedExit) {
    if (Test-Path -LiteralPath $result) { Remove-Item -LiteralPath $result }
    $process = Start-Process -FilePath $app -ArgumentList $Arguments -WorkingDirectory $root -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(45000)) {
        Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
        throw 'WPF smoke test timed out'
    }
    $process.Refresh()
    if ($process.ExitCode -ne $ExpectedExit -or !(Test-Path -LiteralPath $result)) { throw "WPF smoke test returned $($process.ExitCode), expected $ExpectedExit" }
    Get-Content -LiteralPath $result
}
# A broken UI check must terminate with a failing code, never wait forever in a crash dialog.
Invoke-Smoke -Arguments @('--smoke-test', '--smoke-test-failure') -ExpectedExit 1
Invoke-Smoke -Arguments @('--smoke-test') -ExpectedExit 0
