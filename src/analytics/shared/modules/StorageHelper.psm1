function Write-ToAdls {
    param(
        [Parameter(Mandatory)][string]$StorageAccountName,
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][string]$BlobPath,
        [Parameter(Mandatory)][string]$LocalFile
    )

    $ctx = New-AzStorageContext -StorageAccountName $StorageAccountName -UseConnectedAccount
    Set-AzStorageBlobContent -Context $ctx `
        -Container $ContainerName `
        -Blob $BlobPath `
        -File $LocalFile `
        -Force | Out-Null
}

Export-ModuleMember -Function Write-ToAdls
