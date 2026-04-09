function Write-ToAdlsRest {
    param(
        [Parameter(Mandatory)][string]$StorageAccountName,
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][string]$BlobPath,
        [Parameter(Mandatory)][string]$LocalFile
    )

    # Get Managed Identity token from ACA identity endpoint
    $tokenResponse = Invoke-RestMethod `
        -Uri "$($env:IDENTITY_ENDPOINT)?resource=https://storage.azure.com/&api-version=2019-08-01" `
        -Headers @{ 'X-IDENTITY-HEADER' = $env:IDENTITY_HEADER }
    $token = $tokenResponse.access_token
    $headers = @{
        'Authorization' = "Bearer $token"
        'x-ms-version'  = '2021-08-06'
    }

    $baseUrl = "https://${StorageAccountName}.dfs.core.windows.net/${ContainerName}/${BlobPath}"
    $content = [System.IO.File]::ReadAllBytes($LocalFile)

    # 1. Create file
    Invoke-RestMethod -Uri "${baseUrl}?resource=file" `
        -Method PUT -Headers $headers

    # 2. Append data
    Invoke-RestMethod -Uri "${baseUrl}?action=append&position=0" `
        -Method PATCH -Headers $headers `
        -Body $content -ContentType 'application/octet-stream'

    # 3. Flush (finalize)
    Invoke-RestMethod -Uri "${baseUrl}?action=flush&position=$($content.Length)" `
        -Method PATCH -Headers $headers
}

Export-ModuleMember -Function Write-ToAdlsRest
