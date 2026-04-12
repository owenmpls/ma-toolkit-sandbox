function Get-HttpStatusCode {
    <#
    .SYNOPSIS
        Extract a typed HTTP status code from an exception chain.
        Returns [int] status code or $null if the exception is not HTTP-typed.
    #>
    param([Parameter(Mandatory)][System.Exception]$Exception)

    $current = $Exception
    while ($current) {
        # HttpResponseException (Invoke-RestMethod in PS 7.4+)
        # HttpRequestException (.NET 5+ has .StatusCode)
        # WebException (.Response is HttpWebResponse)
        if ($current.PSObject.Properties['Response'] -and $null -ne $current.Response) {
            $resp = $current.Response
            if ($resp.PSObject.Properties['StatusCode'] -and $null -ne $resp.StatusCode) {
                return [int]$resp.StatusCode
            }
        }
        # HttpRequestException in .NET 5+ has StatusCode directly
        if ($current.PSObject.Properties['StatusCode'] -and $null -ne $current.StatusCode) {
            return [int]$current.StatusCode
        }
        # Graph SDK ODataError has ResponseStatusCode
        if ($current.PSObject.Properties['ResponseStatusCode'] -and $null -ne $current.ResponseStatusCode) {
            return [int]$current.ResponseStatusCode
        }
        $current = $current.InnerException
    }
    return $null
}

function Get-ErrorClassification {
    <#
    .SYNOPSIS
        Classify an error as Auth, Throttle, Skippable, or Unknown.
        Checks typed HTTP status codes first, then falls back to message matching.
    .PARAMETER ErrorRecord
        The $_ from a catch block.
    .PARAMETER ApiFamily
        One of 'graph', 'exo', 'spo', 'mde'. Drives which skippable/throttle patterns to use.
    #>
    param(
        [Parameter(Mandatory)]$ErrorRecord,
        [ValidateSet('graph', 'exo', 'spo', 'mde')]
        [string]$ApiFamily = 'graph'
    )

    $ex = $ErrorRecord.Exception
    # Walk exception chain to find deepest meaningful message
    $innermost = $ex
    while ($innermost.InnerException) { $innermost = $innermost.InnerException }
    $message = if (-not [string]::IsNullOrWhiteSpace($innermost.Message)) {
        $innermost.Message
    } elseif (-not [string]::IsNullOrWhiteSpace($ex.Message)) {
        $ex.Message
    } else {
        $ex.GetType().FullName
    }
    $matchText = "$($ex.Message) $($innermost.Message)"

    $statusCode = Get-HttpStatusCode -Exception $ex

    # Extract Retry-After from response headers or message text
    $retryAfter = $null
    if ($null -ne $statusCode) {
        try {
            $resp = $null
            $cur = $ex
            while ($cur -and -not $resp) {
                if ($cur.PSObject.Properties['Response'] -and $null -ne $cur.Response) { $resp = $cur.Response }
                $cur = $cur.InnerException
            }
            if ($resp -and $resp.PSObject.Properties['Headers'] -and $resp.Headers['Retry-After']) {
                $retryAfter = [int]$resp.Headers['Retry-After']
            }
        } catch { }
    }
    if ($null -eq $retryAfter -and $matchText -match 'Retry-After[:\s]+(\d+)') {
        $retryAfter = [int]$Matches[1]
    }

    $result = @{
        Category   = 'Unknown'
        StatusCode = $statusCode
        RetryAfter = $retryAfter
        Message    = $message
    }

    # --- Classify by typed HTTP status code first ---
    if ($null -ne $statusCode) {
        switch ($statusCode) {
            401 { $result.Category = 'Auth'; return $result }
            429 { $result.Category = 'Throttle'; return $result }
            503 { $result.Category = 'Throttle'; return $result }
            404 { $result.Category = 'Skippable'; return $result }
            403 {
                if ($ApiFamily -in @('spo', 'mde')) {
                    $result.Category = 'Skippable'
                } else {
                    $result.Category = 'Auth'
                }
                return $result
            }
        }
    }

    # --- Fall back to message matching (non-HTTP exceptions) ---
    # Auth
    if ($matchText -match 'Unauthorized|token.*expired|Access token has expired|ACS50012') {
        $result.Category = 'Auth'
        return $result
    }

    # Throttle (base patterns for all APIs)
    $throttlePattern = 'TooManyRequests|throttled|Too many requests|Rate limit|Server Busy|ServerBusyException'
    if ($ApiFamily -eq 'exo') {
        $throttlePattern += '|MicroDelay|BackoffException|Too many concurrent'
    }
    if ($matchText -match $throttlePattern) {
        $result.Category = 'Throttle'
        return $result
    }

    # Skippable (per-API patterns)
    $skippablePattern = switch ($ApiFamily) {
        'graph' { 'Request_ResourceNotFound|ResourceNotFound|Synchronization_ObjectNotFound' }
        'exo'   { 'MapiExceptionNotFound|ManagementObjectNotFoundException|couldn''t be found|mailbox.*doesn''t exist|couldn''t find' }
        'spo'   { 'locked|no access|does not exist|Cannot find site|AccessDenied|SiteNotFound' }
        'mde'   { 'ResourceNotFound|Not Found' }
    }
    if ($matchText -match $skippablePattern) {
        $result.Category = 'Skippable'
        return $result
    }

    return $result
}

function Get-RetryDelay {
    <#
    .SYNOPSIS
        Compute backoff delay in seconds for a retry attempt.
    #>
    param(
        [Parameter(Mandatory)][hashtable]$Classification,
        [Parameter(Mandatory)][int]$Attempt,
        [int]$BaseDelay = 2,
        [int]$MaxDelay = 120
    )

    if ($Classification.RetryAfter -and $Classification.RetryAfter -gt 0) {
        return $Classification.RetryAfter
    }

    $exp = [math]::Min($BaseDelay * [math]::Pow(2, $Attempt - 1), $MaxDelay)
    $jitter = Get-Random -Minimum 0.0 -Maximum ($exp * 0.3)
    return [math]::Round($exp + $jitter, 1)
}

function Invoke-WithRetry {
    param(
        [Parameter(Mandatory)][scriptblock]$ScriptBlock,
        [int]$MaxRetries = 5,
        [int]$BaseDelay = 2,
        [int]$MaxDelay = 120,
        [string]$ApiFamily = 'mde',
        [scriptblock]$OnAuthReconnect = $null
    )

    $attempt = 0
    while ($true) {
        try {
            return & $ScriptBlock
        }
        catch {
            $attempt++
            $class = Get-ErrorClassification -ErrorRecord $_ -ApiFamily $ApiFamily

            if ($class.Category -eq 'Skippable') {
                throw [System.InvalidOperationException]::new("SKIPPABLE: $($class.Message)", $_.Exception)
            }

            if ($attempt -gt $MaxRetries) { throw }

            if ($class.Category -eq 'Auth') {
                Write-Verbose "Auth error, reconnecting (attempt $attempt)..."
                if ($OnAuthReconnect) { & $OnAuthReconnect }
                continue
            }

            if ($class.Category -eq 'Throttle') {
                $delay = Get-RetryDelay -Classification $class -Attempt $attempt -BaseDelay $BaseDelay -MaxDelay $MaxDelay
                Write-Verbose "Throttled, backing off ${delay}s (attempt $attempt)..."
                Start-Sleep -Seconds $delay
                continue
            }

            # Unknown — retry with backoff as conservative approach
            $delay = Get-RetryDelay -Classification $class -Attempt $attempt -BaseDelay $BaseDelay -MaxDelay $MaxDelay
            Start-Sleep -Seconds $delay
        }
    }
}

Export-ModuleMember -Function Get-ErrorClassification, Get-RetryDelay, Invoke-WithRetry
