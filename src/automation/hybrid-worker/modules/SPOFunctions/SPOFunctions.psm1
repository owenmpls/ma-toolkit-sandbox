$modulePath = $PSScriptRoot
$functionFiles = @(
    'SPOOperations.ps1'
)
foreach ($file in $functionFiles) {
    $filePath = Join-Path $modulePath $file
    if (Test-Path $filePath) { . $filePath }
}
