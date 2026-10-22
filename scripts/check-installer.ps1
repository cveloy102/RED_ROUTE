param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase((Resolve-Path -LiteralPath $Path).Path, 0)
function Assert-MsiRow([string]$Query, [string]$Description) {
    $view = $database.OpenView($Query)
    $view.Execute()
    if ($null -eq $view.Fetch()) { throw "Installer check failed: $Description" }
    $view.Close()
    Write-Output "PASS: $Description"
}
Assert-MsiRow 'SELECT `Dialog` FROM `Dialog` WHERE `Dialog` = ''RedRouteWelcome''' 'Welcome dialog exists'
Assert-MsiRow 'SELECT `Dialog` FROM `Dialog` WHERE `Dialog` = ''RedRouteSuccess''' 'Completion dialog exists'
Assert-MsiRow 'SELECT `Action` FROM `InstallUISequence` WHERE `Action` = ''RedRouteSuccess'' AND `Sequence` = -1' 'Success screen is scheduled after successful installation'
Assert-MsiRow 'SELECT `Control_` FROM `ControlEvent` WHERE `Dialog_` = ''RedRouteSuccess'' AND `Control_` = ''Finish'' AND `Event` = ''DoAction'' AND `Argument` = ''OpenRedRoute''' 'Finish offers application launch'
Assert-MsiRow 'SELECT `Control` FROM `Control` WHERE `Dialog_` = ''RedRouteSuccess'' AND `Control` = ''Launch''' 'Launch checkbox exists'
Assert-MsiRow 'SELECT `Name` FROM `ServiceInstall` WHERE `Name` = ''RED_ROUTE''' 'Per-machine Windows service remains in package'
Assert-MsiRow 'SELECT `ServiceName` FROM `Wix4ServiceConfig` WHERE `ServiceName` = ''RED_ROUTE'' AND `FirstFailureActionType` = ''restart'' AND `RestartServiceDelayInSeconds` = 5' 'Unexpected service failures trigger automatic restart after five seconds'
Assert-MsiRow 'SELECT `Property` FROM `Property` WHERE `Property` = ''ALLUSERS'' AND `Value` = ''1''' 'Installation still requires machine-wide privileges'
