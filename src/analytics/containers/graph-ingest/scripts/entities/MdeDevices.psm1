function Get-EntityConfig {
    return @{
        Name         = 'mde_devices'
        ScheduleTier = 'core'
        Phase1       = $true
        Phase2       = $false
        ApiSource    = 'mde'
        OutputFile   = 'mde_devices'
        DetailType   = $null
    }
}

function Invoke-Phase1 {
    param(
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$EntityIds
    )

    # --- Acquire MDE token via client assertion JWT (no MSAL dependency) ---
    $clientId = $script:AuthConfig.ClientId
    $tenantId = $script:AuthConfig.TenantId
    $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $script:CertBytes, [string]::Empty,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
    )

    $getMdeToken = {
        $tokenEndpoint = "https://login.microsoftonline.com/$tenantId/oauth2/v2.0/token"

        # Build JWT header with x5t (cert thumbprint)
        $x5t = [Convert]::ToBase64String($cert.GetCertHash()).Replace('+','-').Replace('/','_').TrimEnd('=')
        $headerJson = "{`"alg`":`"RS256`",`"typ`":`"JWT`",`"x5t`":`"$x5t`"}"
        $headerB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($headerJson)).Replace('+','-').Replace('/','_').TrimEnd('=')

        # Build JWT claims
        $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        $claimsJson = "{`"aud`":`"$tokenEndpoint`",`"iss`":`"$clientId`",`"sub`":`"$clientId`",`"jti`":`"$([guid]::NewGuid())`",`"nbf`":$now,`"exp`":$($now + 300)}"
        $claimsB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($claimsJson)).Replace('+','-').Replace('/','_').TrimEnd('=')

        # Sign with RSA-SHA256
        $unsigned = "$headerB64.$claimsB64"
        $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
        $sig = $rsa.SignData(
            [Text.Encoding]::UTF8.GetBytes($unsigned),
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1
        )
        $sigB64 = [Convert]::ToBase64String($sig).Replace('+','-').Replace('/','_').TrimEnd('=')

        $tokenResponse = Invoke-RestMethod -Method POST -Uri $tokenEndpoint -Body @{
            grant_type            = 'client_credentials'
            client_id             = $clientId
            client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
            client_assertion      = "$unsigned.$sigB64"
            scope                 = 'https://api.securitycenter.microsoft.com/.default'
        }
        return $tokenResponse.access_token
    }

    $token = & $getMdeToken
    $headers = @{ Authorization = "Bearer $token" }

    # Reconnect callback for Invoke-WithRetry — re-acquires token on 401
    $reconnect = {
        $token = & $getMdeToken
        $headers['Authorization'] = "Bearer $token"
    }

    # --- Paginated retrieval from MDE API ---
    $count = 0
    $uri = 'https://api.security.microsoft.com/api/machines?$top=10000'

    do {
        $response = Invoke-WithRetry -OnAuthReconnect $reconnect -ScriptBlock {
            Invoke-RestMethod -Method GET -Uri $uri -Headers $headers -ErrorAction Stop
        }
        foreach ($device in $response.value) {
            $Writer.WriteLine(($device | ConvertTo-Json -Compress -Depth 5))
            $EntityIds.Add($device.id)
            $count++
        }
        if ($count % 1000 -eq 0 -and $count -gt 0) { $Writer.Flush() }
        $uri = $response.'@odata.nextLink'
    } while ($uri)

    $Writer.Flush()
    $RecordCount.Value = $count
}

function Invoke-Phase2 {
    param(
        [string[]]$EntityIds,
        [string]$OutputDirectory,
        [string]$RunId,
        [int]$PoolSize
    )
    return @{ RecordCount = 0; ChunkCount = 0 }
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
