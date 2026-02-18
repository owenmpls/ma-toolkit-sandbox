<#
.SYNOPSIS
    PSSession pool for PS 5.1 functions in the Hybrid Worker.
.DESCRIPTION
    Manages a pool of persistent PowerShell Remoting sessions to the localhost
    Windows PowerShell 5.1 endpoint. Each session keeps its modules loaded
    across job invocations, avoiding cold-start overhead.
#>

function Initialize-SessionPool {
    <#
    .SYNOPSIS
        Creates PSSession pool to localhost Windows PowerShell 5.1.
    .DESCRIPTION
        Creates $Config.MaxPs51Sessions persistent PSSessions, loads enabled
        modules in each, and authenticates to on-prem services.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Config,

        [Parameter(Mandatory)]
        [hashtable]$OnPremCredentials  # { 'ad-service-account' = [PSCredential]; ... }
    )

    Write-WorkerLog -Message "Initializing PS 5.1 session pool (size=$($Config.MaxPs51Sessions))..."

    $sessions = @()
    $failedCount = 0

    for ($i = 0; $i -lt $Config.MaxPs51Sessions; $i++) {
        try {
            # Create persistent session to localhost Windows PowerShell 5.1
            $session = New-PSSession -ComputerName localhost `
                -ConfigurationName 'Microsoft.PowerShell' `
                -ErrorAction Stop

            # Load modules based on enabled service connections
            $initResult = Invoke-Command -Session $session -ScriptBlock {
                param($ServiceConnections, $Credentials, $HybridModulePath)

                $loaded = @()

                # Active Directory
                if ($ServiceConnections.activeDirectory.enabled) {
                    Import-Module ActiveDirectory -ErrorAction Stop
                    $loaded += 'ActiveDirectory'
                }

                # Exchange Server Management Shell
                if ($ServiceConnections.exchangeServer.enabled) {
                    $uri = $ServiceConnections.exchangeServer.connectionUri
                    $cred = $Credentials['exchangeServer']
                    $exSession = New-PSSession -ConfigurationName Microsoft.Exchange `
                        -ConnectionUri $uri -Credential $cred -Authentication Kerberos -ErrorAction Stop
                    Import-PSSession $exSession -AllowClobber -DisableNameChecking | Out-Null
                    $loaded += 'ExchangeServer'
                }

                # SharePoint Online
                if ($ServiceConnections.sharepointOnline.enabled) {
                    Import-Module Microsoft.Online.SharePoint.PowerShell -DisableNameChecking -ErrorAction Stop
                    $cred = $Credentials['sharepointOnline']
                    Connect-SPOService -Url $ServiceConnections.sharepointOnline.adminUrl -Credential $cred -ErrorAction Stop
                    $loaded += 'SharePointOnline'
                }

                # Microsoft Teams
                if ($ServiceConnections.teams.enabled) {
                    Import-Module MicrosoftTeams -ErrorAction Stop
                    $cred = $Credentials['teams']
                    Connect-MicrosoftTeams -Credential $cred -ErrorAction Stop
                    $loaded += 'MicrosoftTeams'
                }

                # Import HybridFunctions module (the actual function implementations)
                $manifestPath = Join-Path $HybridModulePath 'HybridFunctions.psd1'
                if (Test-Path $manifestPath) {
                    Import-Module $manifestPath -ErrorAction Stop
                    $loaded += 'HybridFunctions'
                }

                return $loaded
            } -ArgumentList $Config.ServiceConnections, $OnPremCredentials, $Config.HybridModulePath

            Write-WorkerLog -Message "Session ${i}: Modules loaded: $($initResult -join ', ')" -Properties @{ SessionIndex = $i }

            $sessions += [PSCustomObject]@{
                Session = $session
                Index   = $i
                Busy    = $false
                Job     = $null  # PowerShell background job handle
            }
        }
        catch {
            $failedCount++
            Write-WorkerLog -Message "Session $i failed to initialize: $($_.Exception.Message)" -Severity Error
            Write-WorkerException -Exception $_.Exception -Properties @{ SessionIndex = $i }
        }
    }

    if ($sessions.Count -eq 0) {
        throw "All PS 5.1 sessions failed to initialize."
    }

    if ($failedCount -gt 0) {
        Write-WorkerLog -Message "$failedCount session(s) failed. Running with reduced PS 5.1 parallelism." -Severity Warning
    }

    Write-WorkerEvent -EventName 'SessionPoolInitialized' -Properties @{
        PoolSize       = $sessions.Count
        FailedSessions = $failedCount
    }

    return [PSCustomObject]@{
        Sessions         = $sessions
        MaxSessions      = $Config.MaxPs51Sessions
        ActiveSessions   = $sessions.Count
    }
}

function Invoke-InSession {
    <#
    .SYNOPSIS
        Dispatches a function call to an available PSSession.
    .RETURNS
        Async handle object with same shape as Invoke-InRunspace for unified result collection.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Pool,

        [Parameter(Mandatory)]
        [string]$FunctionName,

        [Parameter(Mandatory)]
        [hashtable]$Parameters,

        [int]$MaxRetries = 5,
        [int]$BaseDelaySeconds = 2,
        [int]$MaxDelaySeconds = 120
    )

    # Find an available session
    $slot = $Pool.Sessions | Where-Object { -not $_.Busy } | Select-Object -First 1
    if (-not $slot) {
        throw "No available PS 5.1 sessions in pool."
    }
    $slot.Busy = $true

    # Dispatch as a PowerShell background job using Invoke-Command -AsJob
    # The scriptblock includes retry/throttle logic adapted for PS 5.1 syntax
    $job = Invoke-Command -Session $slot.Session -AsJob -ScriptBlock {
        param($FunctionName, $Parameters, $MaxRetries, $BaseDelaySeconds, $MaxDelaySeconds)

        $attempt = 0
        while ($true) {
            $attempt++
            try {
                $result = & $FunctionName @Parameters
                return [PSCustomObject]@{
                    Success = $true
                    Result  = $result
                    Error   = $null
                }
            }
            catch {
                $ex = $_.Exception
                $innermost = $ex
                while ($innermost.InnerException) { $innermost = $innermost.InnerException }

                $errorMessage = if ($innermost.Message) { $innermost.Message }
                                elseif ($ex.Message) { $ex.Message }
                                else { $ex.GetType().FullName }
                $errorType = $innermost.GetType().FullName
                $matchText = "$($ex.Message) $($innermost.Message)"

                # Check for throttling
                $isThrottled = $false
                $throttlePatterns = @(
                    'TooManyRequests', '429', 'throttled', 'Too many requests',
                    'Rate limit', 'Server Busy', 'please.*retry'
                )
                foreach ($pattern in $throttlePatterns) {
                    if ($matchText -match $pattern) {
                        $isThrottled = $true
                        break
                    }
                }

                if ($isThrottled -and $attempt -lt $MaxRetries) {
                    $retryAfter = 0
                    if ($matchText -match 'Retry-After[:\s]+(\d+)') {
                        $retryAfter = [int]$Matches[1]
                    }
                    if ($retryAfter -gt 0) {
                        Start-Sleep -Seconds $retryAfter
                    }
                    else {
                        $exp = [math]::Min($BaseDelaySeconds * [math]::Pow(2, $attempt - 1), $MaxDelaySeconds)
                        $jitter = Get-Random -Minimum 0.0 -Maximum ($exp * 0.3)
                        Start-Sleep -Seconds ([math]::Round($exp + $jitter, 1))
                    }
                    continue
                }

                return [PSCustomObject]@{
                    Success = $false
                    Result  = $null
                    Error   = [PSCustomObject]@{
                        Message     = $errorMessage
                        Type        = $errorType
                        IsThrottled = $isThrottled
                        Attempts    = $attempt
                    }
                }
            }
        }
    } -ArgumentList $FunctionName, $Parameters, $MaxRetries, $BaseDelaySeconds, $MaxDelaySeconds

    $slot.Job = $job

    # Return handle with same shape as Invoke-InRunspace for unified collection
    return [PSCustomObject]@{
        SessionSlot = $slot
        Job         = $job
        Engine      = 'SessionPool'
    }
}

function Get-SessionResult {
    <#
    .SYNOPSIS
        Collects result from a completed PSSession job.
    .RETURNS
        Same shape as Get-RunspaceResult: { Success, Result, Error }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$AsyncHandle
    )

    try {
        $output = Receive-Job -Job $AsyncHandle.Job -ErrorAction Stop
        Remove-Job -Job $AsyncHandle.Job -Force -ErrorAction SilentlyContinue

        if ($output -and $output.Count -gt 0) {
            return $output[-1]
        }

        return [PSCustomObject]@{
            Success = $true
            Result  = $null
            Error   = $null
        }
    }
    catch {
        Remove-Job -Job $AsyncHandle.Job -Force -ErrorAction SilentlyContinue
        return [PSCustomObject]@{
            Success = $false
            Result  = $null
            Error   = [PSCustomObject]@{
                Message     = $_.Exception.Message
                Type        = $_.Exception.GetType().FullName
                IsThrottled = $false
                Attempts    = 1
            }
        }
    }
    finally {
        # Release the session slot
        $AsyncHandle.SessionSlot.Busy = $false
        $AsyncHandle.SessionSlot.Job = $null
    }
}

function Test-SessionPoolHealth {
    <#
    .SYNOPSIS
        Tests session health and recreates dead sessions.
    .NOTES
        Known gap: Recreated sessions do not have modules reloaded.
        This will be addressed in a future update by extracting module-loading
        into a reusable helper function.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Pool
    )

    foreach ($slot in $Pool.Sessions) {
        if ($slot.Busy) { continue }  # Don't test busy sessions
        try {
            $result = Invoke-Command -Session $slot.Session -ScriptBlock { $true } -ErrorAction Stop
            if ($result -ne $true) { throw 'Health check returned unexpected result' }
        }
        catch {
            Write-WorkerLog -Message "Session $($slot.Index) is dead, recreating..." -Severity Warning
            try {
                Remove-PSSession -Session $slot.Session -ErrorAction SilentlyContinue
                $slot.Session = New-PSSession -ComputerName localhost -ConfigurationName 'Microsoft.PowerShell' -ErrorAction Stop
                # TODO: Re-initialize modules (same init logic as Initialize-SessionPool)
                Write-WorkerLog -Message "Session $($slot.Index) recreated successfully."
            }
            catch {
                Write-WorkerLog -Message "Failed to recreate session $($slot.Index): $($_.Exception.Message)" -Severity Error
            }
        }
    }
}

function Close-SessionPool {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Pool
    )

    Write-WorkerLog -Message 'Closing PS 5.1 session pool...'
    foreach ($slot in $Pool.Sessions) {
        try {
            if ($slot.Job) {
                Stop-Job -Job $slot.Job -ErrorAction SilentlyContinue
                Remove-Job -Job $slot.Job -Force -ErrorAction SilentlyContinue
            }
            Remove-PSSession -Session $slot.Session -ErrorAction SilentlyContinue
        }
        catch {
            Write-WorkerLog -Message "Error closing session $($slot.Index): $($_.Exception.Message)" -Severity Warning
        }
    }
    Write-WorkerLog -Message 'PS 5.1 session pool closed.'
}
