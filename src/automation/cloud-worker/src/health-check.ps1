<#
.SYNOPSIS
    Health check endpoint for the PowerShell Cloud Worker.
.DESCRIPTION
    Provides a simple HTTP endpoint for health/liveness checks.
    Returns JSON with status information about worker components.
.NOTES
    Used by Azure Container Apps for liveness probes.
    Default port: 8080, configurable via HEALTH_CHECK_PORT env var.
#>

#Requires -Version 7.4

$script:HealthCheckRunning = $true
$script:Listener = $null

function Get-HealthStatus {
    <#
    .SYNOPSIS
        Returns the current health status of the worker.
    #>
    param(
        [object]$WorkerRunning,
        [object]$RunspacePool,
        [object]$ServiceBusReceiver,
        [object]$Config
    )

    $status = @{
        timestamp = (Get-Date -Format 'o')
        status = 'healthy'
        checks = @{}
    }

    # Check worker running state (handles both [ref] and plain value from Start-Job)
    if ($null -ne $WorkerRunning) {
        $isRunning = if ($WorkerRunning -is [ref]) { $WorkerRunning.Value } else { [bool]$WorkerRunning }
        $status.checks['workerRunning'] = @{
            status = if ($isRunning) { 'healthy' } else { 'degraded' }
            running = $isRunning
        }
        if (-not $isRunning) {
            $status.status = 'degraded'
        }
    }

    # Check runspace pool health
    if ($null -ne $RunspacePool) {
        try {
            $poolState = $RunspacePool.RunspacePoolStateInfo.State.ToString()
            $available = $RunspacePool.GetAvailableRunspaces()
            $maxRunspaces = $RunspacePool.GetMaxRunspaces()

            $status.checks['runspacePool'] = @{
                status = if ($poolState -eq 'Opened') { 'healthy' } else { 'unhealthy' }
                state = $poolState
                available = $available
                max = $maxRunspaces
                utilization = [math]::Round((($maxRunspaces - $available) / $maxRunspaces) * 100, 1)
            }

            if ($poolState -ne 'Opened') {
                $status.status = 'unhealthy'
            }
        }
        catch {
            $status.checks['runspacePool'] = @{
                status = 'error'
                error = $_.Exception.Message
            }
            $status.status = 'unhealthy'
        }
    }

    # Check Service Bus connection
    if ($null -ne $ServiceBusReceiver) {
        try {
            $isClosed = $ServiceBusReceiver.IsClosed

            $status.checks['serviceBus'] = @{
                status = if (-not $isClosed) { 'healthy' } else { 'unhealthy' }
                connected = (-not $isClosed)
            }

            if ($isClosed) {
                $status.status = 'unhealthy'
            }
        }
        catch {
            $status.checks['serviceBus'] = @{
                status = 'error'
                error = $_.Exception.Message
            }
            $status.status = 'unhealthy'
        }
    }

    # Add worker metadata
    if ($null -ne $Config) {
        $status.worker = @{
            id = $Config.WorkerId
            maxParallelism = $Config.MaxParallelism
            idleTimeoutSeconds = $Config.IdleTimeoutSeconds
        }
    }

    return $status
}

function Start-HealthCheckServer {
    <#
    .SYNOPSIS
        Starts the HTTP health check server.
    #>
    param(
        [int]$Port,
        [object]$WorkerRunning,
        [object]$RunspacePool,
        [object]$ServiceBusReceiver,
        [object]$Config
    )

    try {
        $script:Listener = [System.Net.HttpListener]::new()
        $script:Listener.Prefixes.Add("http://+:$Port/")
        $script:Listener.Start()

        Write-Host "[HEALTH] Health check server started on port $Port" -ForegroundColor Green

        while ($script:HealthCheckRunning) {
            try {
                # Use async with timeout to allow checking the running flag
                # and avoid blocking indefinitely if GetContextAsync hangs
                $contextTask = $script:Listener.GetContextAsync()
                $waitStart = [DateTime]::UtcNow
                $maxWaitSeconds = 30

                while (-not $contextTask.IsCompleted -and $script:HealthCheckRunning) {
                    Start-Sleep -Milliseconds 250
                    if (([DateTime]::UtcNow - $waitStart).TotalSeconds -ge $maxWaitSeconds) {
                        # Timed out waiting for a request — loop back and try again
                        break
                    }
                }

                if (-not $script:HealthCheckRunning) {
                    break
                }

                if (-not $contextTask.IsCompleted) {
                    continue
                }

                $context = $contextTask.GetAwaiter().GetResult()
                $request = $context.Request
                $response = $context.Response

                $healthStatus = Get-HealthStatus -WorkerRunning $WorkerRunning -RunspacePool $RunspacePool -ServiceBusReceiver $ServiceBusReceiver -Config $Config

                $jsonResponse = $healthStatus | ConvertTo-Json -Depth 5 -Compress
                $buffer = [System.Text.Encoding]::UTF8.GetBytes($jsonResponse)

                $response.ContentType = 'application/json'
                $response.ContentLength64 = $buffer.Length

                # Set status code based on health
                $response.StatusCode = switch ($healthStatus.status) {
                    'healthy' { 200 }
                    'degraded' { 200 }  # Still return 200 for degraded (k8s considers 200-399 healthy)
                    'unhealthy' { 503 }
                    default { 500 }
                }

                $response.OutputStream.Write($buffer, 0, $buffer.Length)
                $response.Close()

                # Log health check requests (verbose)
                if ($request.Url.AbsolutePath -eq '/health' -or $request.Url.AbsolutePath -eq '/healthz') {
                    # Normal health probe - don't log to reduce noise
                }
                else {
                    Write-Host "[HEALTH] $($request.HttpMethod) $($request.Url.AbsolutePath) -> $($response.StatusCode)" -ForegroundColor DarkGray
                }
            }
            catch [System.Net.HttpListenerException] {
                if ($script:HealthCheckRunning) {
                    Write-Host "[HEALTH] Listener exception: $($_.Exception.Message)" -ForegroundColor Yellow
                }
            }
            catch {
                if ($script:HealthCheckRunning) {
                    Write-Host "[HEALTH] Error processing request: $($_.Exception.Message)" -ForegroundColor Red
                }
            }
        }
    }
    catch {
        Write-Host "[HEALTH] Failed to start health check server: $($_.Exception.Message)" -ForegroundColor Red
    }
    finally {
        Stop-HealthCheckServer
    }
}

function Stop-HealthCheckServer {
    <#
    .SYNOPSIS
        Stops the HTTP health check server.
    #>
    $script:HealthCheckRunning = $false

    if ($null -ne $script:Listener) {
        try {
            $script:Listener.Stop()
            $script:Listener.Close()
            Write-Host '[HEALTH] Health check server stopped.' -ForegroundColor Yellow
        }
        catch {
            Write-Host "[HEALTH] Error stopping server: $($_.Exception.Message)" -ForegroundColor Red
        }
        $script:Listener = $null
    }
}

# Functions are available in caller scope via dot-sourcing
