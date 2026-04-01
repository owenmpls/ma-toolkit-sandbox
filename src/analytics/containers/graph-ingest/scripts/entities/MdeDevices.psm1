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

    # --- Acquire MDE token using MSAL (loaded with Microsoft.Graph.Authentication) ---
    $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $script:CertBytes, [string]::Empty,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
    )

    $mdeApp = [Microsoft.Identity.Client.ConfidentialClientApplicationBuilder]::Create(
        $script:AuthConfig.ClientId
    ).WithCertificate($cert).WithAuthority(
        "https://login.microsoftonline.com/$($script:AuthConfig.TenantId)"
    ).Build()

    $mdeScope = @('https://api.securitycenter.microsoft.com/.default')
    $tokenResult = $mdeApp.AcquireTokenForClient($mdeScope).ExecuteAsync().GetAwaiter().GetResult()
    $headers = @{ Authorization = "Bearer $($tokenResult.AccessToken)" }

    # Reconnect callback for Invoke-WithRetry — re-acquires token on 401
    $reconnect = {
        $freshToken = $mdeApp.AcquireTokenForClient($mdeScope).ExecuteAsync().GetAwaiter().GetResult()
        $headers['Authorization'] = "Bearer $($freshToken.AccessToken)"
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
