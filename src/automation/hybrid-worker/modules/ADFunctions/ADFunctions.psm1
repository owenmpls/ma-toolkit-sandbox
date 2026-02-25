$modulePath = $PSScriptRoot
$functionFiles = @(
    'ADForestConnection.ps1',
    'ADOperations.ps1'
)
foreach ($file in $functionFiles) {
    $filePath = Join-Path $modulePath $file
    if (Test-Path $filePath) { . $filePath }
}
