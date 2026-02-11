<#
.SYNOPSIS
    Runspace pool management for the PowerShell Cloud Worker.
.DESCRIPTION
    Creates and manages a pool of PowerShell runspaces, each initialized with
    authenticated MgGraph and Exchange Online sessions. Provides job dispatch
    and result collection capabilities.
#>

function Initialize-RunspacePool {
    <#
    .SYNOPSIS
        Creates and initializes the runspace pool with authenticated sessions.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Config,

        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    Write-WorkerLog -Message "Initializing runspace pool with MaxParallelism=$($Config.MaxParallelism)..."

    # Create initial session state with required modules
    $initialState = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault()

    # Import modules that should be available in each runspace
    $modulesToImport = @(
        'Microsoft.Graph.Authentication',
        'Microsoft.Graph.Users',
        'Microsoft.Graph.Groups',
        'Microsoft.Graph.Identity.SignIns',
        'ExchangeOnlineManagement'
    )

    foreach ($moduleName in $modulesToImport) {
        $initialState.ImportPSModule($moduleName)
    }

    # Import StandardFunctions module (fatal if missing — worker cannot process jobs without it)
    $standardModulePath = Join-Path $Config.StandardModulePath 'StandardFunctions.psd1'
    if (Test-Path $standardModulePath) {
        $initialState.ImportPSModule($standardModulePath)
        Write-WorkerLog -Message "Standard functions module loaded from '$standardModulePath'."
    }
    else {
        Write-WorkerLog -Message "FATAL: Standard functions module not found at '$standardModulePath'. Cannot start worker." -Severity Error
        throw "StandardFunctions module not found at '$standardModulePath'. Worker cannot process jobs without it."
    }

    # Import custom function modules
    $customModulesPath = $Config.CustomModulesPath
    if (Test-Path $customModulesPath) {
        $customModules = Get-ChildItem -Path $customModulesPath -Directory
        foreach ($moduleDir in $customModules) {
            $manifestPath = Join-Path $moduleDir.FullName "$($moduleDir.Name).psd1"
            if (Test-Path $manifestPath) {
                $initialState.ImportPSModule($manifestPath)
                Write-WorkerLog -Message "Custom module '$($moduleDir.Name)' loaded."
            }
        }
    }

    # Create the runspace pool
    $pool = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspacePool(
        1,
        $Config.MaxParallelism,
        $initialState,
        (Get-Host)
    )
    $pool.Open()

    Write-WorkerLog -Message "Runspace pool opened (Min=1, Max=$($Config.MaxParallelism))."

    # Export PFX bytes once for passing to runspaces (byte arrays serialize cleanly across runspace boundaries)
    $certBytes = $Certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx)

    # Authenticate each runspace by running auth init in parallel
    $authHandles = @()
    for ($i = 0; $i -lt $Config.MaxParallelism; $i++) {
        $ps = [PowerShell]::Create()
        $ps.RunspacePool = $pool

        $authScript = Get-RunspaceAuthScriptBlock -TenantId $Config.TargetTenantId -AppId $Config.AppId -Organization $Config.TargetOrganization -CertificateBytes $certBytes

        $ps.AddScript($authScript).AddParameters(@{
            TenantId         = $Config.TargetTenantId
            AppId            = $Config.AppId
            Organization     = $Config.TargetOrganization
            CertificateBytes = $certBytes
            RunspaceIndex    = $i
        }) | Out-Null

        $handle = $ps.BeginInvoke()
        $authHandles += @{
            PowerShell = $ps
            Handle     = $handle
            Index      = $i
        }
    }

    # Wait for all auth initializations to complete
    $failedRunspaces = @()
    foreach ($item in $authHandles) {
        try {
            $result = $item.PowerShell.EndInvoke($item.Handle)
            Write-WorkerLog -Message "Runspace $($item.Index): $result"

            if ($item.PowerShell.HadErrors) {
                foreach ($err in $item.PowerShell.Streams.Error) {
                    Write-WorkerLog -Message "Runspace $($item.Index) auth warning: $($err.Exception.Message)" -Severity Warning
                }
            }
        }
        catch {
            $failedRunspaces += $item.Index
            Write-WorkerLog -Message "Runspace $($item.Index) auth failed: $($_.Exception.Message)" -Severity Error
            Write-WorkerException -Exception $_.Exception -Properties @{ RunspaceIndex = $item.Index }
        }
        finally {
            $item.PowerShell.Dispose()
        }
    }

    if ($failedRunspaces.Count -eq $Config.MaxParallelism) {
        $pool.Close()
        $pool.Dispose()
        throw "All runspaces failed to authenticate. Cannot start worker."
    }

    # Zero the certificate byte array now that all runspaces have been initialized
    [Array]::Clear($certBytes, 0, $certBytes.Length)

    if ($failedRunspaces.Count -gt 0) {
        Write-WorkerLog -Message "$($failedRunspaces.Count) runspace(s) failed auth. Worker running with reduced parallelism." -Severity Warning
    }

    Write-WorkerEvent -EventName 'RunspacePoolInitialized' -Properties @{
        MaxParallelism  = $Config.MaxParallelism
        FailedRunspaces = $failedRunspaces.Count
    }

    return $pool
}

function Invoke-InRunspace {
    <#
    .SYNOPSIS
        Dispatches a function call to the runspace pool and returns an async handle.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.Runspaces.RunspacePool]$Pool,

        [Parameter(Mandatory)]
        [string]$FunctionName,

        [Parameter(Mandatory)]
        [hashtable]$Parameters,

        [int]$MaxRetries = 5,
        [int]$BaseDelaySeconds = 2,
        [int]$MaxDelaySeconds = 120
    )

    $ps = [PowerShell]::Create()
    $ps.RunspacePool = $Pool

    # Build the execution script with throttle retry wrapper
    $executionScript = {
        param($FunctionName, $Parameters, $MaxRetries, $BaseDelaySeconds, $MaxDelaySeconds)

        # Inline helper: reconnect Graph and EXO using stored certificate bytes
        function Reconnect-WorkerAuth {
            $cfg = $global:WorkerAuthConfig
            $certBytes = $global:WorkerCertBytes
            $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $certBytes,
                [string]::Empty,
                [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
            )
            Connect-MgGraph -ClientId $cfg.AppId -TenantId $cfg.TenantId -Certificate $cert -NoWelcome -ErrorAction Stop
            $exoParams = @{
                Certificate = $cert
                AppId       = $cfg.AppId
                Organization = $cfg.Organization
                ShowBanner   = $false
                ErrorAction  = 'Stop'
            }
            Connect-ExchangeOnline @exoParams
        }

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

                # Walk the exception chain to find the deepest meaningful message.
                # PowerShell wraps cmdlet errors in CmdletInvocationException /
                # ActionPreferenceStopException whose Message is often empty —
                # the real Graph/EXO error lives in InnerException.
                $innermost = $ex
                while ($innermost.InnerException) { $innermost = $innermost.InnerException }

                $errorMessage = if (-not [string]::IsNullOrWhiteSpace($innermost.Message)) {
                    $innermost.Message
                } elseif (-not [string]::IsNullOrWhiteSpace($ex.Message)) {
                    $ex.Message
                } else {
                    $ex.GetType().FullName
                }

                $errorType = $innermost.GetType().FullName

                # Build a combined string for pattern matching (outer + inner)
                $matchText = "$($ex.Message) $($innermost.Message)"

                # Check for auth errors — refresh EXO token and retry
                $isAuthError = $false
                $authPatterns = @('401', 'Unauthorized', 'token.*expired', 'Access token has expired')
                foreach ($pattern in $authPatterns) {
                    if ($matchText -match $pattern) {
                        $isAuthError = $true
                        break
                    }
                }

                if ($isAuthError -and $global:WorkerAuthConfig -and $attempt -lt $MaxRetries) {
                    try {
                        Reconnect-WorkerAuth
                    }
                    catch {
                        Write-Warning "Failed to reconnect auth (attempt $attempt): $($_.Exception.Message)"
                    }
                    continue
                }

                # Check for throttling
                $isThrottled = $false
                $retryAfter = 0
                $throttlePatterns = @(
                    'TooManyRequests', '429', 'throttled', 'Too many requests',
                    'Rate limit', 'Server Busy', 'ServerBusyException',
                    'MicroDelay', 'BackoffException', 'Too many concurrent',
                    'exceeded.*throttl', 'rate.*limit', 'please.*retry'
                )

                foreach ($pattern in $throttlePatterns) {
                    if ($matchText -match $pattern) {
                        $isThrottled = $true
                        break
                    }
                }

                if ($matchText -match 'Retry-After[:\s]+(\d+)') {
                    $retryAfter = [int]$Matches[1]
                }

                if ($isThrottled -and $attempt -lt $MaxRetries) {
                    $delay = if ($retryAfter -gt 0) {
                        $retryAfter
                    }
                    else {
                        $exp = [math]::Min($BaseDelaySeconds * [math]::Pow(2, $attempt - 1), $MaxDelaySeconds)
                        $jitter = Get-Random -Minimum 0.0 -Maximum ($exp * 0.3)
                        [math]::Round($exp + $jitter, 1)
                    }
                    Start-Sleep -Seconds $delay
                    continue
                }

                return [PSCustomObject]@{
                    Success     = $false
                    Result      = $null
                    Error       = [PSCustomObject]@{
                        Message     = $errorMessage
                        Type        = $errorType
                        IsThrottled = $isThrottled
                        Attempts    = $attempt
                    }
                }
            }
        }
    }

    $ps.AddScript($executionScript).AddParameters(@{
        FunctionName    = $FunctionName
        Parameters      = $Parameters
        MaxRetries      = $MaxRetries
        BaseDelaySeconds = $BaseDelaySeconds
        MaxDelaySeconds  = $MaxDelaySeconds
    }) | Out-Null

    $handle = $ps.BeginInvoke()

    return [PSCustomObject]@{
        PowerShell = $ps
        Handle     = $handle
    }
}

function Get-RunspaceResult {
    <#
    .SYNOPSIS
        Collects the result from an async runspace invocation.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$AsyncHandle
    )

    try {
        $output = $AsyncHandle.PowerShell.EndInvoke($AsyncHandle.Handle)

        if ($AsyncHandle.PowerShell.HadErrors) {
            $errorMessages = $AsyncHandle.PowerShell.Streams.Error | ForEach-Object {
                $errEx = $_.Exception
                $inner = $errEx
                while ($inner.InnerException) { $inner = $inner.InnerException }
                if (-not [string]::IsNullOrWhiteSpace($inner.Message)) { $inner.Message }
                elseif (-not [string]::IsNullOrWhiteSpace($errEx.Message)) { $errEx.Message }
                else { $errEx.GetType().FullName }
            }
            return [PSCustomObject]@{
                Success = $false
                Result  = $null
                Error   = [PSCustomObject]@{
                    Message     = ($errorMessages -join '; ')
                    Type        = 'RunspaceError'
                    IsThrottled = $false
                    Attempts    = 1
                }
            }
        }

        # The execution script returns a PSCustomObject with Success, Result, Error
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
        $catchEx = $_.Exception
        $catchInner = $catchEx
        while ($catchInner.InnerException) { $catchInner = $catchInner.InnerException }
        return [PSCustomObject]@{
            Success = $false
            Result  = $null
            Error   = [PSCustomObject]@{
                Message     = if (-not [string]::IsNullOrWhiteSpace($catchInner.Message)) { $catchInner.Message } elseif (-not [string]::IsNullOrWhiteSpace($catchEx.Message)) { $catchEx.Message } else { $catchEx.GetType().FullName }
                Type        = $catchInner.GetType().FullName
                IsThrottled = $false
                Attempts    = 1
            }
        }
    }
    finally {
        $AsyncHandle.PowerShell.Dispose()
    }
}

function Close-RunspacePool {
    <#
    .SYNOPSIS
        Gracefully closes and disposes the runspace pool.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Management.Automation.Runspaces.RunspacePool]$Pool
    )

    Write-WorkerLog -Message 'Closing runspace pool...'
    try {
        $Pool.Close()
        $Pool.Dispose()
        Write-WorkerLog -Message 'Runspace pool closed.'
    }
    catch {
        Write-WorkerLog -Message "Error closing runspace pool: $($_.Exception.Message)" -Severity Warning
    }
}
