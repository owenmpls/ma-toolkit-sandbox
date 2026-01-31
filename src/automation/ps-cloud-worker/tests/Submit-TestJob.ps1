<#
.SYNOPSIS
    Test script for submitting jobs to the PowerShell Cloud Worker via Service Bus.
.DESCRIPTION
    Reads a CSV file and enqueues a job message for each row to the Service Bus
    jobs topic. Each message targets a specific worker and function.

    Uses interactive Azure authentication for simplicity.
.PARAMETER CsvPath
    Path to the CSV file containing job parameters. Each row becomes one job.
.PARAMETER ServiceBusNamespace
    The Service Bus namespace (e.g., 'your-namespace.servicebus.windows.net').
.PARAMETER TopicName
    The name of the jobs topic. Defaults to 'jobs'.
.PARAMETER WorkerId
    The target worker ID. Messages are tagged with this ID for subscription filtering.
.PARAMETER FunctionName
    The function to execute for each row.
.PARAMETER BatchId
    Optional batch ID to group jobs. Auto-generated if not specified.
.EXAMPLE
    ./Submit-TestJob.ps1 -CsvPath ./sample-jobs.csv -ServiceBusNamespace 'myns.servicebus.windows.net' -WorkerId 'worker-01' -FunctionName 'New-EntraUser'
.EXAMPLE
    ./Submit-TestJob.ps1 -CsvPath ./sample-jobs.csv -ServiceBusNamespace 'myns.servicebus.windows.net' -WorkerId 'worker-01' -FunctionName 'Add-EntraGroupMember' -BatchId 'batch-2026-01-29'
#>

#Requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CsvPath,

    [Parameter(Mandatory)]
    [string]$ServiceBusNamespace,

    [string]$TopicName = 'jobs',

    [Parameter(Mandatory)]
    [string]$WorkerId,

    [Parameter(Mandatory)]
    [string]$FunctionName,

    [string]$BatchId
)

$ErrorActionPreference = 'Stop'

# --- Validate CSV ---
if (-not (Test-Path $CsvPath)) {
    Write-Error "CSV file not found: $CsvPath"
    exit 1
}

$csvData = Import-Csv -Path $CsvPath
if ($csvData.Count -eq 0) {
    Write-Error "CSV file is empty: $CsvPath"
    exit 1
}

Write-Host "Loaded $($csvData.Count) row(s) from '$CsvPath'." -ForegroundColor Cyan

# --- Generate BatchId if not provided ---
if ([string]::IsNullOrWhiteSpace($BatchId)) {
    $BatchId = "batch-$(Get-Date -Format 'yyyyMMdd-HHmmss')-$([Guid]::NewGuid().ToString().Substring(0, 8))"
}
Write-Host "Batch ID: $BatchId"

# --- Authenticate to Azure ---
Write-Host 'Authenticating to Azure (interactive)...'
try {
    $context = Get-AzContext
    if (-not $context) {
        Connect-AzAccount -ErrorAction Stop | Out-Null
    }
    Write-Host "Authenticated as: $((Get-AzContext).Account.Id)" -ForegroundColor Green
}
catch {
    Write-Error "Azure authentication failed: $($_.Exception.Message)"
    exit 1
}

# --- Load Service Bus assemblies ---
Write-Host 'Loading Service Bus assemblies...'

$dotNetLibPath = $env:DOTNET_LIB_PATH
if (-not $dotNetLibPath) {
    # Try to find assemblies in common locations or use NuGet
    $possiblePaths = @(
        '/opt/dotnet-libs',
        (Join-Path $PSScriptRoot '..' 'lib'),
        (Join-Path $HOME '.nuget' 'packages' 'azure.messaging.servicebus')
    )

    foreach ($path in $possiblePaths) {
        if (Test-Path (Join-Path $path 'Azure.Messaging.ServiceBus.dll')) {
            $dotNetLibPath = $path
            break
        }
    }
}

if (-not $dotNetLibPath) {
    Write-Host 'Service Bus assemblies not found locally. Installing via dotnet...' -ForegroundColor Yellow
    $tempProject = Join-Path ([System.IO.Path]::GetTempPath()) 'sb-test-project'
    New-Item -ItemType Directory -Path $tempProject -Force | Out-Null

    $csproj = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Azure.Messaging.ServiceBus" Version="7.18.1" />
    <PackageReference Include="Azure.Identity" Version="1.13.1" />
  </ItemGroup>
</Project>
'@
    $csproj | Out-File (Join-Path $tempProject 'project.csproj')
    Push-Location $tempProject
    dotnet restore 2>$null
    dotnet publish -o (Join-Path $tempProject 'out') 2>$null
    Pop-Location
    $dotNetLibPath = Join-Path $tempProject 'out'
}

$assemblies = @('Azure.Core', 'Azure.Identity', 'Azure.Messaging.ServiceBus', 'System.Memory.Data', 'System.ClientModel')
foreach ($asm in $assemblies) {
    $dllPath = Join-Path $dotNetLibPath "$asm.dll"
    if (Test-Path $dllPath) {
        try { Add-Type -Path $dllPath -ErrorAction SilentlyContinue } catch { }
    }
}

# --- Create Service Bus client and sender ---
Write-Host "Connecting to Service Bus: $ServiceBusNamespace..."
$credential = [Azure.Identity.DefaultAzureCredential]::new()
$client = [Azure.Messaging.ServiceBus.ServiceBusClient]::new($ServiceBusNamespace, $credential)
$sender = $client.CreateSender($TopicName)
Write-Host "Connected to topic '$TopicName'." -ForegroundColor Green

# --- Enqueue messages ---
$successCount = 0
$failCount = 0

foreach ($row in $csvData) {
    $jobId = [Guid]::NewGuid().ToString()

    # Convert CSV row to parameters hashtable
    $parameters = @{}
    foreach ($prop in $row.PSObject.Properties) {
        if (-not [string]::IsNullOrWhiteSpace($prop.Value)) {
            $parameters[$prop.Name] = $prop.Value
        }
    }

    $jobMessage = [PSCustomObject]@{
        JobId        = $jobId
        BatchId      = $BatchId
        WorkerId     = $WorkerId
        FunctionName = $FunctionName
        Parameters   = $parameters
    }

    $jsonBody = $jobMessage | ConvertTo-Json -Depth 5 -Compress

    try {
        $message = [Azure.Messaging.ServiceBus.ServiceBusMessage]::new($jsonBody)
        $message.ContentType = 'application/json'
        $message.Subject = $FunctionName
        $message.MessageId = $jobId
        $message.ApplicationProperties['WorkerId'] = $WorkerId
        $message.ApplicationProperties['BatchId'] = $BatchId

        $sender.SendMessageAsync($message).GetAwaiter().GetResult()
        $successCount++
        Write-Host "  [$successCount] Enqueued job $jobId" -ForegroundColor DarkGray
    }
    catch {
        $failCount++
        Write-Host "  [FAIL] Job $jobId : $($_.Exception.Message)" -ForegroundColor Red
    }
}

# --- Cleanup ---
$sender.DisposeAsync().GetAwaiter().GetResult()
$client.DisposeAsync().GetAwaiter().GetResult()

# --- Summary ---
Write-Host ''
Write-Host '--- Summary ---' -ForegroundColor Cyan
Write-Host "Batch ID:    $BatchId"
Write-Host "Worker ID:   $WorkerId"
Write-Host "Function:    $FunctionName"
Write-Host "Total rows:  $($csvData.Count)"
Write-Host "Enqueued:    $successCount" -ForegroundColor Green
if ($failCount -gt 0) {
    Write-Host "Failed:      $failCount" -ForegroundColor Red
}
Write-Host ''
