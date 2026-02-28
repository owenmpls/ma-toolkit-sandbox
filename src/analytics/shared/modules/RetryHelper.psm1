function Invoke-WithRetry {
    param(
        [Parameter(Mandatory)][scriptblock]$ScriptBlock,
        [int]$MaxRetries = 5,
        [int]$BaseDelay = 2,
        [int]$MaxDelay = 120,
        [scriptblock]$OnAuthReconnect = $null
    )

    $attempt = 0
    while ($true) {
        try {
            return & $ScriptBlock
        }
        catch {
            $attempt++
            $msg = $_.Exception.Message

            # Auth errors - reconnect and retry
            $isAuthError = $msg -match '401|Unauthorized|token expired|ACS50012'
            if ($isAuthError -and $attempt -le $MaxRetries) {
                Write-Verbose "Auth error, reconnecting (attempt $attempt)..."
                if ($OnAuthReconnect) { & $OnAuthReconnect }
                continue
            }

            # Throttle errors - exponential backoff with jitter
            $isThrottle = $msg -match '429|TooManyRequests|ServerBusy|throttl|MicroDelay|BackoffException'
            if ($isThrottle -and $attempt -le $MaxRetries) {
                $retryAfter = if ($_.Exception.Response.Headers['Retry-After']) {
                    [int]$_.Exception.Response.Headers['Retry-After']
                } else {
                    [math]::Min($BaseDelay * [math]::Pow(2, $attempt - 1), $MaxDelay)
                }
                $jitter = Get-Random -Minimum 0 -Maximum ([math]::Max(1, [int]($retryAfter / 4)))
                $delay = $retryAfter + $jitter
                Write-Verbose "Throttled, backing off ${delay}s (attempt $attempt)..."
                Start-Sleep -Seconds $delay
                continue
            }

            # Skippable errors (SPO 403/404, locked sites)
            $isSkippable = $msg -match '403|404|site.*locked|AccessDenied|SiteNotFound'
            if ($isSkippable) {
                throw [System.InvalidOperationException]::new("SKIPPABLE: $msg", $_.Exception)
            }

            # Fatal - rethrow
            if ($attempt -gt $MaxRetries) { throw }
        }
    }
}

Export-ModuleMember -Function Invoke-WithRetry
