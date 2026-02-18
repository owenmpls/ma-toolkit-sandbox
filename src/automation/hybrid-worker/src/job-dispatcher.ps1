<#
.SYNOPSIS
    Job dispatcher for the Hybrid Worker.
.DESCRIPTION
    Receives job messages from Service Bus, validates them, routes to the correct
    execution engine (RunspacePool for PS 7.x or SessionPool for PS 5.1),
    collects results, and sends result messages back. Periodically checks for updates.
#>

function Test-JobMessage {
    <#
    .SYNOPSIS
        Validates a deserialized job message has the required fields.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Job
    )

    $requiredFields = @('JobId', 'FunctionName', 'Parameters')
    $missing = @()

    foreach ($field in $requiredFields) {
        if (-not $Job.PSObject.Properties[$field] -or $null -eq $Job.$field) {
            $missing += $field
        }
    }

    if ($missing.Count -gt 0) {
        return [PSCustomObject]@{
            IsValid = $false
            Error   = "Missing required fields: $($missing -join ', ')"
        }
    }

    return [PSCustomObject]@{
        IsValid = $true
        Error   = $null
    }
}

function ConvertTo-ParameterHashtable {
    <#
    .SYNOPSIS
        Converts a PSCustomObject (from JSON) to a hashtable for splatting.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $Parameters
    )

    $hashtable = @{}

    if ($Parameters -is [System.Management.Automation.PSCustomObject]) {
        foreach ($prop in $Parameters.PSObject.Properties) {
            $hashtable[$prop.Name] = $prop.Value
        }
    }
    elseif ($Parameters -is [hashtable]) {
        $hashtable = $Parameters
    }

    return $hashtable
}

function New-JobResult {
    <#
    .SYNOPSIS
        Creates a standardized job result object.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Job,

        [Parameter(Mandatory)]
        [string]$WorkerId,

        [Parameter(Mandatory)]
        [ValidateSet('success', 'failure')]
        [string]$Status,

        $Result = $null,

        [PSCustomObject]$ErrorInfo = $null,

        [long]$DurationMs = 0
    )

    $resultType = 'Boolean'
    $resultValue = $Result

    if ($null -ne $Result -and $Result -isnot [bool]) {
        $resultType = 'Object'
        # Ensure PSCustomObject is serializable
        if ($Result -is [System.Management.Automation.PSCustomObject]) {
            $resultValue = $Result
        }
        else {
            try {
                $resultValue = [PSCustomObject]@{ Value = $Result }
            }
            catch {
                $resultValue = [PSCustomObject]@{ Value = $Result.ToString() }
            }
        }
    }

    return [PSCustomObject]@{
        jobId           = $Job.JobId
        batchId         = if ($Job.PSObject.Properties['BatchId']) { $Job.BatchId } else { $null }
        workerId        = $WorkerId
        functionName    = $Job.FunctionName
        status          = $Status
        resultType      = $resultType
        result          = $resultValue
        error           = $ErrorInfo
        durationMs      = $DurationMs
        timestamp       = (Get-Date).ToUniversalTime().ToString('o')
        correlationData = if ($Job.PSObject.Properties['CorrelationData']) { $Job.CorrelationData } else { $null }
    }
}

function Start-JobDispatcher {
    <#
    .SYNOPSIS
        Main job processing loop with dual-engine routing and periodic update checks.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [PSCustomObject]$Config,
        [Parameter(Mandatory)] $Receiver,
        [Parameter(Mandatory)] $Sender,
        [Parameter(Mandatory)] [Azure.Messaging.ServiceBus.ServiceBusClient]$Client,
        [Parameter(Mandatory)] [string]$JobsTopicName,
        [Parameter(Mandatory)] [PSCustomObject]$ServiceRegistry,
        # These may be $null if the corresponding engine is not enabled:
        [System.Management.Automation.Runspaces.RunspacePool]$RunspacePool,
        [PSCustomObject]$SessionPool,
        [Parameter(Mandatory)] [ref]$Running
    )

    $script:AllowedFunctions = Get-AllowedFunctions -ServiceRegistry $ServiceRegistry
    Write-WorkerLog -Message "Function whitelist: $($script:AllowedFunctions.Count) functions." -Properties @{
        AllowedFunctions = ($script:AllowedFunctions -join ', ')
    }

    Write-WorkerLog -Message 'Job dispatcher started.'
    Write-WorkerEvent -EventName 'DispatcherStarted'

    $activeJobs = [System.Collections.Generic.List[PSCustomObject]]::new()
    $lastActivityTime = [DateTime]::UtcNow
    $lastUpdateCheck = [DateTime]::UtcNow
    $idleTimeoutSeconds = $Config.IdleTimeoutSeconds

    while ($Running.Value) {
        try {
            # --- Idle timeout check ---
            if ($idleTimeoutSeconds -gt 0 -and $activeJobs.Count -eq 0) {
                $idleSeconds = ([DateTime]::UtcNow - $lastActivityTime).TotalSeconds
                if ($idleSeconds -ge $idleTimeoutSeconds) {
                    Write-WorkerLog -Message "Idle timeout reached (${idleTimeoutSeconds}s)."
                    Write-WorkerEvent -EventName 'IdleTimeoutShutdown'
                    $Running.Value = $false
                    break
                }
            }
            elseif ($activeJobs.Count -gt 0) {
                $lastActivityTime = [DateTime]::UtcNow
            }

            # --- Periodic update check ---
            if ($Config.UpdateEnabled) {
                $updateCheckInterval = $Config.UpdatePollIntervalMinutes * 60
                if (([DateTime]::UtcNow - $lastUpdateCheck).TotalSeconds -ge $updateCheckInterval) {
                    $lastUpdateCheck = [DateTime]::UtcNow
                    $updateInfo = Test-UpdateAvailable -Config $Config
                    if ($updateInfo) {
                        Write-WorkerLog -Message "Update available: v$($updateInfo.version). Initiating update..." -Severity Information
                        $downloaded = Install-WorkerUpdate -Config $Config -UpdateInfo $updateInfo
                        if ($downloaded) {
                            Write-WorkerLog -Message 'Update downloaded. Will apply after draining active jobs.'
                            $Running.Value = $false
                            # Don't break — let the shutdown drain loop handle active jobs
                        }
                    }
                }
            }

            # --- Check for completed jobs (both engines) ---
            $completedIndexes = @()
            for ($i = 0; $i -lt $activeJobs.Count; $i++) {
                $activeJob = $activeJobs[$i]
                $isCompleted = $false

                if ($activeJob.Engine -eq 'RunspacePool') {
                    $isCompleted = $activeJob.AsyncHandle.Handle.IsCompleted
                }
                elseif ($activeJob.Engine -eq 'SessionPool') {
                    $isCompleted = $activeJob.AsyncHandle.Job.State -in @('Completed', 'Failed', 'Stopped')
                }

                if ($isCompleted) {
                    $completedIndexes += $i
                    $lastActivityTime = [DateTime]::UtcNow

                    try {
                        # Collect result using the appropriate engine's collector
                        $executionResult = if ($activeJob.Engine -eq 'RunspacePool') {
                            Get-RunspaceResult -AsyncHandle $activeJob.AsyncHandle
                        } else {
                            Get-SessionResult -AsyncHandle $activeJob.AsyncHandle
                        }

                        $duration = [long]((Get-Date) - $activeJob.StartTime).TotalMilliseconds
                        $status = if ($executionResult.Success) { 'success' } else { 'failure' }

                        $resultMsg = New-JobResult -Job $activeJob.Job -WorkerId $Config.WorkerId `
                            -Status $status -Result $executionResult.Result `
                            -ErrorInfo $executionResult.Error -DurationMs $duration

                        Send-ServiceBusResult -Sender $Sender -Result $resultMsg
                        Complete-ServiceBusMessage -Receiver $Receiver -Message $activeJob.SbMessage

                        Write-WorkerLog -Message "Job '$($activeJob.Job.JobId)' $status ($($duration)ms, $($activeJob.Engine))." -Properties @{
                            JobId = $activeJob.Job.JobId; FunctionName = $activeJob.Job.FunctionName
                            DurationMs = $duration; Engine = $activeJob.Engine
                        }
                        Write-WorkerMetric -Name 'JobDuration' -Value $duration -Properties @{
                            FunctionName = $activeJob.Job.FunctionName; Status = $status; Engine = $activeJob.Engine
                        }
                    }
                    catch {
                        Write-WorkerLog -Message "Error processing result for '$($activeJob.Job.JobId)': $($_.Exception.Message)" -Severity Error
                        Abandon-ServiceBusMessage -Receiver $Receiver -Message $activeJob.SbMessage
                    }
                }
            }

            # Remove completed (reverse order)
            for ($i = $completedIndexes.Count - 1; $i -ge 0; $i--) {
                $activeJobs.RemoveAt($completedIndexes[$i])
            }

            # --- Determine available slots per engine ---
            $runspaceActive = ($activeJobs | Where-Object { $_.Engine -eq 'RunspacePool' }).Count
            $sessionActive = ($activeJobs | Where-Object { $_.Engine -eq 'SessionPool' }).Count
            $runspaceSlots = if ($RunspacePool) { $Config.MaxParallelism - $runspaceActive } else { 0 }
            $sessionSlots = if ($SessionPool) { $SessionPool.ActiveSessions - $sessionActive } else { 0 }
            $totalSlots = $runspaceSlots + $sessionSlots

            if ($totalSlots -le 0) {
                Start-Sleep -Milliseconds 100
                continue
            }

            # --- Receive new messages ---
            $receiverRef = [ref]$Receiver
            $messages = Receive-ServiceBusMessages -ReceiverRef $receiverRef -Client $Client `
                -TopicName $JobsTopicName -WorkerId $Config.WorkerId `
                -MaxMessages $totalSlots -WaitTimeSeconds 2
            $Receiver = $receiverRef.Value

            if (-not $messages -or $messages.Count -eq 0) { continue }
            $lastActivityTime = [DateTime]::UtcNow

            foreach ($message in $messages) {
                try {
                    $job = ConvertFrom-ServiceBusMessage -Message $message
                    $validation = Test-JobMessage -Job $job
                    if (-not $validation.IsValid) {
                        $errorResult = New-JobResult -Job ([PSCustomObject]@{
                            JobId = $job.JobId ?? 'unknown'; BatchId = $null; FunctionName = $job.FunctionName ?? 'unknown'
                        }) -WorkerId $Config.WorkerId -Status 'failure' -ErrorInfo ([PSCustomObject]@{
                            message = "Invalid job: $($validation.Error)"; type = 'ValidationError'; isThrottled = $false; attempts = 0
                        })
                        Send-ServiceBusResult -Sender $Sender -Result $errorResult
                        Complete-ServiceBusMessage -Receiver $Receiver -Message $message
                        continue
                    }

                    # Check whitelist
                    if ($job.FunctionName -notin $script:AllowedFunctions) {
                        $errorResult = New-JobResult -Job $job -WorkerId $Config.WorkerId -Status 'failure' -ErrorInfo ([PSCustomObject]@{
                            message = "Function '$($job.FunctionName)' not allowed."; type = 'SecurityValidationError'; isThrottled = $false; attempts = 0
                        })
                        Send-ServiceBusResult -Sender $Sender -Result $errorResult
                        Complete-ServiceBusMessage -Receiver $Receiver -Message $message
                        continue
                    }

                    # Route to correct engine
                    $engine = Get-FunctionEngine -FunctionName $job.FunctionName -ServiceRegistry $ServiceRegistry
                    $parameters = ConvertTo-ParameterHashtable -Parameters $job.Parameters

                    # Check engine-specific capacity
                    if ($engine -eq 'RunspacePool' -and $runspaceSlots -le 0) {
                        Abandon-ServiceBusMessage -Receiver $Receiver -Message $message
                        continue
                    }
                    if ($engine -eq 'SessionPool' -and $sessionSlots -le 0) {
                        Abandon-ServiceBusMessage -Receiver $Receiver -Message $message
                        continue
                    }

                    Write-WorkerLog -Message "Dispatching '$($job.JobId)': $($job.FunctionName) [$engine]" -Properties @{
                        JobId = $job.JobId; FunctionName = $job.FunctionName; Engine = $engine
                    }

                    $asyncHandle = if ($engine -eq 'RunspacePool') {
                        Invoke-InRunspace -Pool $RunspacePool -FunctionName $job.FunctionName `
                            -Parameters $parameters -MaxRetries $Config.MaxRetryCount `
                            -BaseDelaySeconds $Config.BaseRetryDelaySeconds `
                            -MaxDelaySeconds $Config.MaxRetryDelaySeconds
                    }
                    else {
                        Invoke-InSession -Pool $SessionPool -FunctionName $job.FunctionName `
                            -Parameters $parameters -MaxRetries $Config.MaxRetryCount `
                            -BaseDelaySeconds $Config.BaseRetryDelaySeconds `
                            -MaxDelaySeconds $Config.MaxRetryDelaySeconds
                    }

                    $activeJobs.Add([PSCustomObject]@{
                        Job         = $job
                        AsyncHandle = $asyncHandle
                        SbMessage   = $message
                        StartTime   = Get-Date
                        Engine      = $engine
                    })

                    # Update available slots
                    if ($engine -eq 'RunspacePool') { $runspaceSlots-- }
                    else { $sessionSlots-- }

                    Write-WorkerMetric -Name 'JobDispatched' -Value 1 -Properties @{
                        FunctionName = $job.FunctionName; Engine = $engine
                    }
                }
                catch {
                    Write-WorkerLog -Message "Error dispatching: $($_.Exception.Message)" -Severity Error
                    Abandon-ServiceBusMessage -Receiver $Receiver -Message $message
                }
            }
        }
        catch {
            Write-WorkerLog -Message "Dispatcher loop error: $($_.Exception.Message)" -Severity Error
            Write-WorkerException -Exception $_.Exception
            Start-Sleep -Seconds 1
        }
    }

    # --- Shutdown drain ---
    if ($activeJobs.Count -gt 0) {
        Write-WorkerLog -Message "Draining $($activeJobs.Count) active job(s) ($($Config.ShutdownGraceSeconds)s grace)..."
        $timeout = [DateTime]::UtcNow.AddSeconds($Config.ShutdownGraceSeconds)

        while ($activeJobs.Count -gt 0 -and [DateTime]::UtcNow -lt $timeout) {
            $completedIndexes = @()
            for ($i = 0; $i -lt $activeJobs.Count; $i++) {
                $aj = $activeJobs[$i]
                $done = if ($aj.Engine -eq 'RunspacePool') { $aj.AsyncHandle.Handle.IsCompleted }
                        else { $aj.AsyncHandle.Job.State -in @('Completed', 'Failed', 'Stopped') }
                if ($done) {
                    $completedIndexes += $i
                    try {
                        $result = if ($aj.Engine -eq 'RunspacePool') { Get-RunspaceResult -AsyncHandle $aj.AsyncHandle }
                                  else { Get-SessionResult -AsyncHandle $aj.AsyncHandle }
                        $dur = [long]((Get-Date) - $aj.StartTime).TotalMilliseconds
                        $st = if ($result.Success) { 'success' } else { 'failure' }
                        $msg = New-JobResult -Job $aj.Job -WorkerId $Config.WorkerId -Status $st `
                            -Result $result.Result -ErrorInfo $result.Error -DurationMs $dur
                        Send-ServiceBusResult -Sender $Sender -Result $msg
                        Complete-ServiceBusMessage -Receiver $Receiver -Message $aj.SbMessage
                    }
                    catch {
                        Abandon-ServiceBusMessage -Receiver $Receiver -Message $aj.SbMessage
                    }
                }
            }
            for ($i = $completedIndexes.Count - 1; $i -ge 0; $i--) { $activeJobs.RemoveAt($completedIndexes[$i]) }
            if ($activeJobs.Count -gt 0) { Start-Sleep -Milliseconds 200 }
        }

        if ($activeJobs.Count -gt 0) {
            Write-WorkerLog -Message "$($activeJobs.Count) job(s) did not complete within shutdown timeout." -Severity Warning
        }
    }

    Write-WorkerLog -Message 'Job dispatcher stopped.'
    Write-WorkerEvent -EventName 'DispatcherStopped'
}
