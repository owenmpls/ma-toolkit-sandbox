<#
.SYNOPSIS
    Job dispatcher for the PowerShell Cloud Worker.
.DESCRIPTION
    Receives job messages from Service Bus, validates them, dispatches to the
    runspace pool, collects results, and sends result messages back.
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
        [ValidateSet('Success', 'Failure')]
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
        JobId        = $Job.JobId
        BatchId      = if ($Job.PSObject.Properties['BatchId']) { $Job.BatchId } else { $null }
        WorkerId     = $WorkerId
        FunctionName = $Job.FunctionName
        Status       = $Status
        ResultType   = $resultType
        Result       = $resultValue
        Error        = $ErrorInfo
        DurationMs   = $DurationMs
        Timestamp    = (Get-Date).ToUniversalTime().ToString('o')
    }
}

function Start-JobDispatcher {
    <#
    .SYNOPSIS
        Main job processing loop. Receives messages, dispatches to runspace pool, returns results.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Config,

        [Parameter(Mandatory)]
        $Receiver,

        [Parameter(Mandatory)]
        $Sender,

        [Parameter(Mandatory)]
        [System.Management.Automation.Runspaces.RunspacePool]$Pool,

        [Parameter(Mandatory)]
        [ref]$Running
    )

    Write-WorkerLog -Message 'Job dispatcher started. Listening for messages...'
    Write-WorkerEvent -EventName 'DispatcherStarted'

    # Track active async jobs
    $activeJobs = [System.Collections.Generic.List[PSCustomObject]]::new()

    # Idle timeout tracking -- when no messages are received and no jobs are active
    # for this duration, the worker shuts down so ACA can scale to zero.
    $lastActivityTime = [DateTime]::UtcNow
    $idleTimeoutSeconds = $Config.IdleTimeoutSeconds

    if ($idleTimeoutSeconds -gt 0) {
        Write-WorkerLog -Message "Idle timeout configured: ${idleTimeoutSeconds}s. Worker will shut down when idle."
    }
    else {
        Write-WorkerLog -Message 'Idle timeout disabled (IDLE_TIMEOUT_SECONDS=0). Worker will run until stopped.'
    }

    while ($Running.Value) {
        try {
            # Check idle timeout when no jobs are in flight
            if ($idleTimeoutSeconds -gt 0 -and $activeJobs.Count -eq 0) {
                $idleSeconds = ([DateTime]::UtcNow - $lastActivityTime).TotalSeconds
                if ($idleSeconds -ge $idleTimeoutSeconds) {
                    Write-WorkerLog -Message "Idle timeout reached (${idleTimeoutSeconds}s with no activity). Initiating graceful shutdown." -Properties @{
                        IdleTimeoutSeconds  = $idleTimeoutSeconds
                        IdleDurationSeconds = [math]::Round($idleSeconds, 0)
                    }
                    Write-WorkerEvent -EventName 'IdleTimeoutShutdown' -Properties @{
                        IdleTimeoutSeconds  = $idleTimeoutSeconds
                        IdleDurationSeconds = [math]::Round($idleSeconds, 0)
                    }
                    $Running.Value = $false
                    break
                }
            }

            # Check for completed async jobs
            $completedIndexes = @()
            for ($i = 0; $i -lt $activeJobs.Count; $i++) {
                $activeJob = $activeJobs[$i]
                if ($activeJob.AsyncHandle.Handle.IsCompleted) {
                    $completedIndexes += $i

                    # Job completed -- reset idle timer
                    $lastActivityTime = [DateTime]::UtcNow

                    try {
                        $executionResult = Get-RunspaceResult -AsyncHandle $activeJob.AsyncHandle
                        $duration = [long]((Get-Date) - $activeJob.StartTime).TotalMilliseconds

                        if ($executionResult.Success) {
                            $resultMsg = New-JobResult -Job $activeJob.Job -WorkerId $Config.WorkerId `
                                -Status 'Success' -Result $executionResult.Result -DurationMs $duration

                            Write-WorkerLog -Message "Job '$($activeJob.Job.JobId)' completed successfully ($($duration)ms)." -Properties @{
                                JobId        = $activeJob.Job.JobId
                                FunctionName = $activeJob.Job.FunctionName
                                DurationMs   = $duration
                            }
                        }
                        else {
                            $resultMsg = New-JobResult -Job $activeJob.Job -WorkerId $Config.WorkerId `
                                -Status 'Failure' -ErrorInfo $executionResult.Error -DurationMs $duration

                            Write-WorkerLog -Message "Job '$($activeJob.Job.JobId)' failed: $($executionResult.Error.Message)" -Severity Error -Properties @{
                                JobId        = $activeJob.Job.JobId
                                FunctionName = $activeJob.Job.FunctionName
                                ErrorType    = $executionResult.Error.Type
                                DurationMs   = $duration
                            }
                        }

                        # Send result and complete the Service Bus message
                        Send-ServiceBusResult -Sender $Sender -Result $resultMsg
                        Complete-ServiceBusMessage -Receiver $Receiver -Message $activeJob.SbMessage

                        Write-WorkerMetric -Name 'JobDuration' -Value $duration -Properties @{
                            FunctionName = $activeJob.Job.FunctionName
                            Status       = $resultMsg.Status
                        }
                    }
                    catch {
                        Write-WorkerLog -Message "Error processing result for job '$($activeJob.Job.JobId)': $($_.Exception.Message)" -Severity Error
                        Write-WorkerException -Exception $_.Exception -Properties @{
                            JobId = $activeJob.Job.JobId
                        }
                        # Abandon message so it can be retried
                        Abandon-ServiceBusMessage -Receiver $Receiver -Message $activeJob.SbMessage
                    }
                }
            }

            # Remove completed jobs (reverse order to maintain indexes)
            for ($i = $completedIndexes.Count - 1; $i -ge 0; $i--) {
                $activeJobs.RemoveAt($completedIndexes[$i])
            }

            # Determine how many new messages we can accept
            $availableSlots = $Config.MaxParallelism - $activeJobs.Count
            if ($availableSlots -le 0) {
                Start-Sleep -Milliseconds 100
                continue
            }

            # Receive new messages
            $messages = Receive-ServiceBusMessages -Receiver $Receiver -MaxMessages $availableSlots -WaitTimeSeconds 2

            if (-not $messages -or $messages.Count -eq 0) {
                continue
            }

            # Messages received -- reset idle timer
            $lastActivityTime = [DateTime]::UtcNow

            foreach ($message in $messages) {
                try {
                    $job = ConvertFrom-ServiceBusMessage -Message $message
                    $validation = Test-JobMessage -Job $job

                    if (-not $validation.IsValid) {
                        Write-WorkerLog -Message "Invalid job message: $($validation.Error)" -Severity Warning
                        $errorResult = New-JobResult -Job ([PSCustomObject]@{
                            JobId        = $job.JobId ?? 'unknown'
                            BatchId      = $job.BatchId ?? $null
                            FunctionName = $job.FunctionName ?? 'unknown'
                        }) -WorkerId $Config.WorkerId -Status 'Failure' -ErrorInfo ([PSCustomObject]@{
                            Message     = "Invalid job: $($validation.Error)"
                            Type        = 'ValidationError'
                            IsThrottled = $false
                            Attempts    = 0
                        })
                        Send-ServiceBusResult -Sender $Sender -Result $errorResult
                        Complete-ServiceBusMessage -Receiver $Receiver -Message $message
                        continue
                    }

                    # Convert parameters from PSCustomObject to hashtable
                    $parameters = ConvertTo-ParameterHashtable -Parameters $job.Parameters

                    Write-WorkerLog -Message "Dispatching job '$($job.JobId)': $($job.FunctionName)" -Properties @{
                        JobId        = $job.JobId
                        BatchId      = $job.BatchId
                        FunctionName = $job.FunctionName
                    }

                    # Dispatch to runspace pool
                    $asyncHandle = Invoke-InRunspace -Pool $Pool `
                        -FunctionName $job.FunctionName `
                        -Parameters $parameters `
                        -MaxRetries $Config.MaxRetryCount `
                        -BaseDelaySeconds $Config.BaseRetryDelaySeconds `
                        -MaxDelaySeconds $Config.MaxRetryDelaySeconds

                    $activeJobs.Add([PSCustomObject]@{
                        Job         = $job
                        AsyncHandle = $asyncHandle
                        SbMessage   = $message
                        StartTime   = Get-Date
                    })

                    Write-WorkerMetric -Name 'JobDispatched' -Value 1 -Properties @{
                        FunctionName = $job.FunctionName
                    }
                }
                catch {
                    Write-WorkerLog -Message "Error dispatching job: $($_.Exception.Message)" -Severity Error
                    Write-WorkerException -Exception $_.Exception
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

    # Wait for remaining active jobs to complete
    if ($activeJobs.Count -gt 0) {
        Write-WorkerLog -Message "Waiting for $($activeJobs.Count) active job(s) to complete..."
        $timeout = [DateTime]::UtcNow.AddSeconds(30)

        while ($activeJobs.Count -gt 0 -and [DateTime]::UtcNow -lt $timeout) {
            $completedIndexes = @()
            for ($i = 0; $i -lt $activeJobs.Count; $i++) {
                if ($activeJobs[$i].AsyncHandle.Handle.IsCompleted) {
                    $completedIndexes += $i
                    try {
                        $executionResult = Get-RunspaceResult -AsyncHandle $activeJobs[$i].AsyncHandle
                        $duration = [long]((Get-Date) - $activeJobs[$i].StartTime).TotalMilliseconds
                        $status = if ($executionResult.Success) { 'Success' } else { 'Failure' }

                        $resultMsg = New-JobResult -Job $activeJobs[$i].Job -WorkerId $Config.WorkerId `
                            -Status $status -Result $executionResult.Result -ErrorInfo $executionResult.Error -DurationMs $duration

                        Send-ServiceBusResult -Sender $Sender -Result $resultMsg
                        Complete-ServiceBusMessage -Receiver $Receiver -Message $activeJobs[$i].SbMessage
                    }
                    catch {
                        Write-WorkerLog -Message "Error during shutdown for job '$($activeJobs[$i].Job.JobId)': $($_.Exception.Message)" -Severity Error
                        Abandon-ServiceBusMessage -Receiver $Receiver -Message $activeJobs[$i].SbMessage
                    }
                }
            }

            for ($i = $completedIndexes.Count - 1; $i -ge 0; $i--) {
                $activeJobs.RemoveAt($completedIndexes[$i])
            }

            if ($activeJobs.Count -gt 0) {
                Start-Sleep -Milliseconds 200
            }
        }

        if ($activeJobs.Count -gt 0) {
            Write-WorkerLog -Message "$($activeJobs.Count) job(s) did not complete within shutdown timeout." -Severity Warning
        }
    }

    Write-WorkerLog -Message 'Job dispatcher stopped.'
    Write-WorkerEvent -EventName 'DispatcherStopped'
}
