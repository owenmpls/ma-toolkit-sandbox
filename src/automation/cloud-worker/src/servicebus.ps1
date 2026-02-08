<#
.SYNOPSIS
    Azure Service Bus integration for the PowerShell Cloud Worker.
.DESCRIPTION
    Manages Service Bus client, subscription, message receiving, and result sending
    using the Azure.Messaging.ServiceBus .NET SDK.
#>

function Initialize-ServiceBusAssemblies {
    <#
    .SYNOPSIS
        Loads the required .NET assemblies for Service Bus operations.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$DotNetLibPath
    )

    $assemblies = @(
        'Azure.Core',
        'Azure.Identity',
        'Azure.Messaging.ServiceBus',
        'System.Memory.Data',
        'System.ClientModel',
        'Microsoft.Identity.Client',
        'Microsoft.Bcl.AsyncInterfaces',
        'System.Diagnostics.DiagnosticSource'
    )

    foreach ($assembly in $assemblies) {
        $dllPath = Join-Path $DotNetLibPath "$assembly.dll"
        if (Test-Path $dllPath) {
            try {
                Add-Type -Path $dllPath -ErrorAction SilentlyContinue
            }
            catch {
                # Some assemblies may already be loaded or have version conflicts, continue
                Write-Verbose "Assembly load note for $assembly : $($_.Exception.Message)"
            }
        }
        else {
            Write-WorkerLog -Message "Assembly not found: $dllPath" -Severity Warning
        }
    }

    Write-WorkerLog -Message 'Service Bus assemblies loaded.'
}

function New-ServiceBusClient {
    <#
    .SYNOPSIS
        Creates a Service Bus client using DefaultAzureCredential (managed identity).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Namespace
    )

    Write-WorkerLog -Message "Creating Service Bus client for namespace '$Namespace'..."

    $credential = [Azure.Identity.DefaultAzureCredential]::new()
    $client = [Azure.Messaging.ServiceBus.ServiceBusClient]::new($Namespace, $credential)

    Write-WorkerLog -Message 'Service Bus client created.'
    return $client
}

function New-ServiceBusSender {
    <#
    .SYNOPSIS
        Creates a sender for the results topic.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [Azure.Messaging.ServiceBus.ServiceBusClient]$Client,

        [Parameter(Mandatory)]
        [string]$TopicName
    )

    $sender = $Client.CreateSender($TopicName)
    Write-WorkerLog -Message "Service Bus sender created for topic '$TopicName'."
    return $sender
}

function Get-SubscriptionName {
    <#
    .SYNOPSIS
        Returns the standard subscription name for a given worker ID.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$WorkerId
    )

    return "worker-$WorkerId"
}

function New-ServiceBusReceiver {
    <#
    .SYNOPSIS
        Creates a receiver for the worker's subscription on the jobs topic.
    .DESCRIPTION
        The subscription is expected to exist with a SQL filter matching the worker ID.
        Subscription creation with filter is handled at deployment time (Bicep/ARM).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [Azure.Messaging.ServiceBus.ServiceBusClient]$Client,

        [Parameter(Mandatory)]
        [string]$TopicName,

        [Parameter(Mandatory)]
        [string]$WorkerId
    )

    $subscriptionName = Get-SubscriptionName -WorkerId $WorkerId

    $receiverOptions = [Azure.Messaging.ServiceBus.ServiceBusReceiverOptions]::new()
    $receiverOptions.ReceiveMode = [Azure.Messaging.ServiceBus.ServiceBusReceiveMode]::PeekLock

    $receiver = $Client.CreateReceiver($TopicName, $subscriptionName, $receiverOptions)
    Write-WorkerLog -Message "Service Bus receiver created for topic '$TopicName', subscription '$subscriptionName'."
    return $receiver
}

function Receive-ServiceBusMessages {
    <#
    .SYNOPSIS
        Receives a batch of messages from the Service Bus subscription.
    .DESCRIPTION
        Non-blocking receive with configurable timeout. Returns available messages.
        On transient connection errors, recreates the receiver and retries.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ref]$ReceiverRef,

        [Parameter(Mandatory)]
        [Azure.Messaging.ServiceBus.ServiceBusClient]$Client,

        [Parameter(Mandatory)]
        [string]$TopicName,

        [Parameter(Mandatory)]
        [string]$WorkerId,

        [int]$MaxMessages = 10,

        [int]$WaitTimeSeconds = 5
    )

    $waitTime = [TimeSpan]::FromSeconds($WaitTimeSeconds)

    try {
        $task = $ReceiverRef.Value.ReceiveMessagesAsync($MaxMessages, $waitTime)
        $task.GetAwaiter().GetResult()
    }
    catch [Azure.Messaging.ServiceBus.ServiceBusException] {
        if ($_.Exception.IsTransient -or $ReceiverRef.Value.IsClosed) {
            Write-WorkerLog -Message "Transient Service Bus error, recreating receiver: $($_.Exception.Message)" -Severity Warning
            try {
                $ReceiverRef.Value = New-ServiceBusReceiver -Client $Client -TopicName $TopicName -WorkerId $WorkerId
            }
            catch {
                Write-WorkerLog -Message "Failed to recreate receiver: $($_.Exception.Message)" -Severity Error
            }
        }
        else {
            Write-WorkerLog -Message "Service Bus error: $($_.Exception.Message)" -Severity Error
        }
        return @()
    }
    catch {
        Write-WorkerLog -Message "Error receiving messages: $($_.Exception.Message)" -Severity Error
        return @()
    }
}

function Complete-ServiceBusMessage {
    <#
    .SYNOPSIS
        Completes (acknowledges) a Service Bus message.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $Receiver,

        [Parameter(Mandatory)]
        $Message
    )

    try {
        $task = $Receiver.CompleteMessageAsync($Message)
        $task.GetAwaiter().GetResult()
    }
    catch {
        Write-WorkerLog -Message "Error completing message: $($_.Exception.Message)" -Severity Error
    }
}

function Abandon-ServiceBusMessage {
    <#
    .SYNOPSIS
        Abandons a Service Bus message (returns to queue for reprocessing).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $Receiver,

        [Parameter(Mandatory)]
        $Message
    )

    try {
        $task = $Receiver.AbandonMessageAsync($Message)
        $task.GetAwaiter().GetResult()
    }
    catch {
        Write-WorkerLog -Message "Error abandoning message: $($_.Exception.Message)" -Severity Error
    }
}

function Send-ServiceBusResult {
    <#
    .SYNOPSIS
        Sends a job result message to the results topic.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $Sender,

        [Parameter(Mandatory)]
        [PSCustomObject]$Result
    )

    try {
        $jsonBody = $Result | ConvertTo-Json -Depth 10 -Compress
        $message = [Azure.Messaging.ServiceBus.ServiceBusMessage]::new($jsonBody)
        $message.ContentType = 'application/json'
        $message.Subject = $Result.FunctionName
        $message.ApplicationProperties['WorkerId'] = $Result.WorkerId
        $message.ApplicationProperties['JobId'] = $Result.JobId
        $message.ApplicationProperties['BatchId'] = $Result.BatchId
        $message.ApplicationProperties['Status'] = $Result.Status

        $task = $Sender.SendMessageAsync($message)
        $task.GetAwaiter().GetResult()

        Write-WorkerLog -Message "Result sent for job '$($Result.JobId)' - Status: $($Result.Status)" -Properties @{
            JobId        = $Result.JobId
            BatchId      = $Result.BatchId
            FunctionName = $Result.FunctionName
            Status       = $Result.Status
        }
    }
    catch {
        Write-WorkerLog -Message "Failed to send result for job '$($Result.JobId)': $($_.Exception.Message)" -Severity Error
        throw
    }
}

function ConvertFrom-ServiceBusMessage {
    <#
    .SYNOPSIS
        Deserializes a Service Bus message body into a job object.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $Message
    )

    try {
        $bodyString = $Message.Body.ToString()
        $job = $bodyString | ConvertFrom-Json
        return $job
    }
    catch {
        Write-WorkerLog -Message "Failed to deserialize message: $($_.Exception.Message)" -Severity Error
        throw
    }
}
