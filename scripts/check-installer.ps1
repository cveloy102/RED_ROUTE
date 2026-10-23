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
Assert-MsiRow 'SELECT `Action` FROM `CustomAction` WHERE `Source` = ''REINSTALLMODE'' AND `Target` = ''amus'' AND `Type` = 51' 'Major upgrade installs a complete matching private runtime regardless of existing file versions'
Assert-MsiRow 'SELECT `Name` FROM `Registry` WHERE `Name` = ''OwnerSid'' AND `Value` = ''[REDROUTEOWNER]''' 'Service ownership uses the interactive user rather than elevated installer credentials'
Assert-MsiRow 'SELECT `Name` FROM `RegLocator` WHERE `Name` = ''OwnerSid''' 'Upgrade locates and preserves the existing owner'
Assert-MsiRow 'SELECT `Name` FROM `ServiceControl` WHERE `Name` = ''RED_ROUTE'' AND `Event` = 163 AND `Wait` = 1' 'Install, upgrade and uninstall wait for orderly service shutdown'
$secure = $database.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''SecureCustomProperties'''); $secure.Execute()
$secureRow = $secure.Fetch()
if ($null -eq $secureRow -or $secureRow.StringData(1).Split(';') -notcontains 'REDROUTEOWNER') { throw 'Installer owner property must survive elevation' }
$secure.Close(); Write-Output 'PASS: Owner identity survives Windows Installer elevation'
$files = $database.OpenView('SELECT `FileName` FROM `File`'); $files.Execute()
while ($null -ne ($file = $files.Fetch())) {
    $name = $file.StringData(1).Split('|')[-1]
    if ($name -match '(?i)(lab-server|synthetic|\.conf$|settings\.dpapi$|\.pdb$)') { throw "Unexpected test or private file in installer: $name" }
}
$files.Close(); Write-Output 'PASS: No lab executables, profiles, private settings or debug symbols are shipped'
foreach ($required in @('hostpolicy.dll', 'hostfxr.dll', 'coreclr.dll', 'System.Private.CoreLib.dll')) {
    $runtimeFiles = $database.OpenView('SELECT `FileName` FROM `File`'); $runtimeFiles.Execute()
    $count = 0
    while ($null -ne ($file = $runtimeFiles.Fetch())) { if ($file.StringData(1).Split('|')[-1] -eq $required) { $count++ } }
    $runtimeFiles.Close()
    if ($count -ne 2) { throw "Private runtime must contain $required for both UI and service, found $count" }
    Write-Output "PASS: Both application and service contain $required"
}
