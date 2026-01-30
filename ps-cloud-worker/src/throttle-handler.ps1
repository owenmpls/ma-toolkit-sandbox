<#
.SYNOPSIS
    Throttle detection and retry logic for Microsoft Graph and Exchange Online.
.DESCRIPTION
    Evaluates exceptions to determine if they are caused by throttling,
    and implements exponential backoff with jitter for retry.
#>

function Test-IsThrottledException {
    <#
    .SYNOPSIS
        Determines if an exception is a throttling response from Graph or Exchange Online.
    .OUTPUTS
        PSCustomObject with IsThrottled (bool), RetryAfterSeconds (int), Source (string).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Exception]$Exception
    )

    $result = [PSCustomObject]@{
        IsThrottled       = $false
        RetryAfterSeconds = 0
        Source             = 'Unknown'
    }

    $message = $Exception.Message
    $innerMessage = if ($Exception.InnerException) { $Exception.InnerException.Message } else { '' }
    $fullMessage = "$message $innerMessage"

    # Microsoft Graph throttling patterns
    $graphThrottlePatterns = @(
        'TooManyRequests',
        '429',
        'Request_ResourceNotFound.*retry',
        'throttled',
        'Too many requests',
        'Rate limit',
        'ResourceNotFound.*temporarily'
    )

    foreach ($pattern in $graphThrottlePatterns) {
        if ($fullMessage -match $pattern) {
            $result.IsThrottled = $true
            $result.Source = 'MicrosoftGraph'
            break
        }
    }

    # Exchange Online throttling patterns
    if (-not $result.IsThrottled) {
        $exoThrottlePatterns = @(
            'Server Busy',
            'ServerBusyException',
            'MicroDelay',
            'BackoffException',
            'Too many concurrent connections',
            'exceeded.*throttl',
            'rate.*limit',
            'please.*retry',
            'throttl'
        )

        foreach ($pattern in $exoThrottlePatterns) {
            if ($fullMessage -match $pattern) {
                $result.IsThrottled = $true
                $result.Source = 'ExchangeOnline'
                break
            }
        }
    }

    # Extract Retry-After if present
    if ($result.IsThrottled) {
        if ($fullMessage -match 'Retry-After[:\s]+(\d+)') {
            $result.RetryAfterSeconds = [int]$Matches[1]
        }
        # Check for retry-after in exception data
        elseif ($Exception.PSObject.Properties['Response'] -and $Exception.Response) {
            try {
                $retryHeader = $Exception.Response.Headers | Where-Object { $_.Key -eq 'Retry-After' }
                if ($retryHeader) {
                    $result.RetryAfterSeconds = [int]$retryHeader.Value[0]
                }
            }
            catch {
                # Ignore errors reading response headers
            }
        }
    }

    return $result
}

function Invoke-WithThrottleRetry {
    <#
    .SYNOPSIS
        Executes a scriptblock with automatic retry on throttling exceptions.
    .DESCRIPTION
        Wraps execution with exponential backoff and jitter when throttling is detected.
        Non-throttling exceptions are thrown immediately.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [scriptblock]$ScriptBlock,

        [int]$MaxRetries = 5,

        [int]$BaseDelaySeconds = 2,

        [int]$MaxDelaySeconds = 120,

        [string]$OperationName = 'Operation',

        [hashtable]$LogProperties = @{}
    )

    $attempt = 0

    while ($true) {
        $attempt++
        try {
            return & $ScriptBlock
        }
        catch {
            $throttleInfo = Test-IsThrottledException -Exception $_.Exception

            if (-not $throttleInfo.IsThrottled) {
                # Not a throttling exception -- rethrow immediately
                throw
            }

            if ($attempt -ge $MaxRetries) {
                Write-WorkerLog -Message "Throttle retry exhausted for '$OperationName' after $MaxRetries attempts." -Severity Error -Properties ($LogProperties + @{ Attempt = $attempt })
                throw
            }

            # Calculate backoff delay
            $delaySeconds = if ($throttleInfo.RetryAfterSeconds -gt 0) {
                $throttleInfo.RetryAfterSeconds
            }
            else {
                # Exponential backoff with jitter
                $exponentialDelay = [math]::Min($BaseDelaySeconds * [math]::Pow(2, $attempt - 1), $MaxDelaySeconds)
                $jitter = Get-Random -Minimum 0.0 -Maximum ($exponentialDelay * 0.3)
                [math]::Round($exponentialDelay + $jitter, 1)
            }

            Write-WorkerLog -Message "Throttled by $($throttleInfo.Source) on '$OperationName'. Attempt $attempt/$MaxRetries. Retrying in ${delaySeconds}s." -Severity Warning -Properties ($LogProperties + @{
                Attempt           = $attempt
                DelaySeconds      = $delaySeconds
                ThrottleSource    = $throttleInfo.Source
                RetryAfterHeader  = $throttleInfo.RetryAfterSeconds
            })

            Write-WorkerMetric -Name 'ThrottleRetry' -Value 1 -Properties ($LogProperties + @{
                Source    = $throttleInfo.Source
                Operation = $OperationName
                Attempt   = $attempt
            })

            Start-Sleep -Seconds $delaySeconds
        }
    }
}
