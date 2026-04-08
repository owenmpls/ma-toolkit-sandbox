function Write-ToAdlsRest {
    param(
        [Parameter(Mandatory)][string]$StorageAccountUrl,
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][string]$BlobPath,
        [Parameter(Mandatory)][string]$LocalFile
    )

    # Acquire token based on auth method
    $authMethod = $env:STORAGE_AUTH_METHOD ?? 'managed_identity'

    if ($authMethod -eq 'service_principal') {
        # Build client assertion (JWT signed with certificate)
        $spCertName  = $env:STORAGE_SP_CERT_NAME
        $spTenantId  = $env:STORAGE_SP_TENANT_ID
        $spClientId  = $env:STORAGE_SP_CLIENT_ID
        $spCertBytes = Get-CertificateBytes -VaultName $env:KEYVAULT_NAME -CertName $spCertName

        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $spCertBytes, [string]::Empty,
            [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
        )
        $thumbprint = [System.Convert]::ToBase64String($cert.GetCertHash())
        $now = [DateTimeOffset]::UtcNow
        $header = @{ alg = 'RS256'; typ = 'JWT'; x5t = $thumbprint } | ConvertTo-Json -Compress
        $payload = @{
            aud = "https://login.microsoftonline.com/$spTenantId/oauth2/v2.0/token"
            iss = $spClientId
            sub = $spClientId
            jti = [guid]::NewGuid().ToString()
            nbf = $now.ToUnixTimeSeconds()
            exp = $now.AddMinutes(10).ToUnixTimeSeconds()
        } | ConvertTo-Json -Compress

        $toBase64Url = { param($bytes) [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_') }
        $headerB64 = & $toBase64Url ([System.Text.Encoding]::UTF8.GetBytes($header))
        $payloadB64 = & $toBase64Url ([System.Text.Encoding]::UTF8.GetBytes($payload))
        $dataToSign = [System.Text.Encoding]::UTF8.GetBytes("$headerB64.$payloadB64")

        $rsa = $cert.GetRSAPrivateKey()
        $sigBytes = $rsa.SignData($dataToSign, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $sigB64 = & $toBase64Url $sigBytes
        $clientAssertion = "$headerB64.$payloadB64.$sigB64"

        $tokenResponse = Invoke-RestMethod `
            -Uri "https://login.microsoftonline.com/$spTenantId/oauth2/v2.0/token" `
            -Method POST `
            -ContentType 'application/x-www-form-urlencoded' `
            -Body @{
                client_id             = $spClientId
                scope                 = 'https://storage.azure.com/.default'
                client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
                client_assertion      = $clientAssertion
                grant_type            = 'client_credentials'
            }
        $token = $tokenResponse.access_token
    } else {
        # ACA managed identity token
        $tokenResponse = Invoke-RestMethod `
            -Uri "$($env:IDENTITY_ENDPOINT)?resource=https://storage.azure.com/&api-version=2019-08-01" `
            -Headers @{ 'X-IDENTITY-HEADER' = $env:IDENTITY_HEADER }
        $token = $tokenResponse.access_token
    }

    $headers = @{
        'Authorization' = "Bearer $token"
        'x-ms-version'  = '2021-08-06'
    }

    $baseUrl = "$StorageAccountUrl/$ContainerName/$BlobPath"
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
