$modulePath = $PSScriptRoot
$functionFiles = @(
    'ADFunctions.ps1',
    'ExchangeServerFunctions.ps1',
    'SPOFunctions.ps1',
    'TeamsFunctions.ps1'
)
foreach ($file in $functionFiles) {
    $filePath = Join-Path $modulePath $file
    if (Test-Path $filePath) { . $filePath }
}
