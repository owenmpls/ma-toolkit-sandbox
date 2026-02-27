#Requires -RunAsAdministrator
#Requires -Version 7.4

<#
.SYNOPSIS
    Installs the MA Toolkit Hybrid Worker as a Scheduled Task.
.DESCRIPTION
    Deploys the hybrid-worker to C:\ProgramData\MaToolkit\HybridWorker\,
    registers a Scheduled Task that runs Start-HybridWorker.ps1 on a
    configurable interval (default 5 minutes). No .NET SDK required.
.PARAMETER ConfigPath
    Path to a pre-filled worker-config.json. If not provided, copies the
    example and prompts for edits.
.PARAMETER CertificatePath
    Path to a PFX file to import into the local machine cert store.
.PARAMETER CertificatePassword
    Password for the PFX file.
.PARAMETER ServiceAccount
    Account to run the task under. Supports:
      - gMSA: 'DOMAIN\gmsaAccount$' (recommended for production)
      - Standard: 'DOMAIN\serviceAccount' (requires -ServiceAccountPassword)
      - SYSTEM: omit this parameter (default, not recommended for production)
.PARAMETER ServiceAccountPassword
    Password for a standard service account. Not needed for gMSA.
.PARAMETER IntervalMinutes
    How often the scheduled task fires (default: 5 minutes).
#>
param(
    [string]$ConfigPath,
    [string]$CertificatePath,
    [SecureString]$CertificatePassword,
    [string]$ServiceAccount,
    [SecureString]$ServiceAccountPassword,
    [int]$IntervalMinutes = 5
)

$ErrorActionPreference = 'Stop'
$installBase = 'C:\ProgramData\MaToolkit\HybridWorker'
$taskName = 'MaToolkitHybridWorker'
$taskDescription = 'Migration Automation Toolkit - Hybrid Worker (runs every {0} min, processes migration jobs from Service Bus)' -f $IntervalMinutes

# --- 1. Verify prerequisites ---
$pwshVersion = $PSVersionTable.PSVersion
if ($pwshVersion -lt [Version]'7.4') { throw "PowerShell 7.4+ required. Got: $pwshVersion" }

$winps = Get-Command powershell.exe -ErrorAction SilentlyContinue
if (-not $winps) { throw 'Windows PowerShell 5.1 (powershell.exe) not found.' }

$pwshExe = (Get-Command pwsh.exe -ErrorAction SilentlyContinue).Source
if (-not $pwshExe) { throw 'pwsh.exe not found in PATH.' }

# Verify WinRM is running (needed for PSSession pool)
$winrm = Get-Service WinRM -ErrorAction SilentlyContinue
if (-not $winrm -or $winrm.Status -ne 'Running') {
    Write-Host 'Enabling PSRemoting...'
    Enable-PSRemoting -Force -SkipNetworkProfileCheck
}

# Check for existing task
$existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existingTask) {
    throw "Scheduled task '$taskName' already exists. Run Uninstall-HybridWorkerTask.ps1 first."
}

# --- 2. Create directory structure ---
$dirs = @('current', 'staging', 'previous', 'config', 'logs')
foreach ($dir in $dirs) {
    $path = Join-Path $installBase $dir
    if (-not (Test-Path $path)) { New-Item -Path $path -ItemType Directory -Force | Out-Null }
}

# --- 3. Copy worker files to current\ ---
$sourceDir = $PSScriptRoot  # hybrid-worker/ root
$itemsToCopy = @('src', 'modules', 'dotnet-libs', 'version.txt', 'Start-HybridWorker.ps1')
foreach ($item in $itemsToCopy) {
    $src = Join-Path $sourceDir $item
    $dst = Join-Path $installBase "current\$item"
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination $dst -Recurse -Force
    }
}

# --- 4. Configuration ---
if ($ConfigPath -and (Test-Path $ConfigPath)) {
    Copy-Item $ConfigPath -Destination (Join-Path $installBase 'config\worker-config.json') -Force
}
elseif (-not (Test-Path (Join-Path $installBase 'config\worker-config.json'))) {
    $exampleConfig = Join-Path $sourceDir 'config\worker-config.example.json'
    Copy-Item $exampleConfig -Destination (Join-Path $installBase 'config\worker-config.json')
    Write-Host 'IMPORTANT: Edit config\worker-config.json before enabling the task.' -ForegroundColor Yellow
}

# --- 5. Import certificate if provided ---
if ($CertificatePath) {
    $certFlags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::MachineKeySet -bor
                 [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet
    $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $CertificatePath, $CertificatePassword, $certFlags)
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new('My', 'LocalMachine')
    $store.Open('ReadWrite')
    $store.Add($cert)
    $store.Close()
    Write-Host "Certificate imported. Thumbprint: $($cert.Thumbprint)"

    # Grant private key read access to the service account
    if ($ServiceAccount) {
        $keyName = $cert.PrivateKey.CspKeyContainerInfo.UniqueKeyContainerName
        $keyPath = "C:\ProgramData\Microsoft\Crypto\RSA\MachineKeys\$keyName"
        if (Test-Path $keyPath) {
            $acl = Get-Acl $keyPath
            $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
                $ServiceAccount, 'Read', 'Allow')
            $acl.AddAccessRule($rule)
            Set-Acl $keyPath $acl
            Write-Host "Granted private key access to $ServiceAccount."
        }
    }
}

# --- 6. Register Scheduled Task ---
$launcherScript = Join-Path $installBase 'current\Start-HybridWorker.ps1'

$taskAction = New-ScheduledTaskAction `
    -Execute $pwshExe `
    -Argument "-NoProfile -NonInteractive -File `"$launcherScript`"" `
    -WorkingDirectory (Join-Path $installBase 'current')

# Trigger: repeating every N minutes, indefinitely
$taskTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date) `
    -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes) `
    -RepetitionDuration ([TimeSpan]::Zero)  # Zero = indefinite

$taskSettings = New-ScheduledTaskSettingsSet `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2) `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -StartWhenAvailable `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries

# Configure principal (who the task runs as)
if ($ServiceAccount) {
    if ($ServiceAccount.EndsWith('$')) {
        # gMSA -- LogonType Password with empty password
        $taskPrincipal = New-ScheduledTaskPrincipal `
            -UserId $ServiceAccount `
            -LogonType Password `
            -RunLevel Highest
        Write-Host "Task will run as gMSA: $ServiceAccount"
    }
    else {
        $taskPrincipal = New-ScheduledTaskPrincipal `
            -UserId $ServiceAccount `
            -LogonType Password `
            -RunLevel Highest
        Write-Host "Task will run as: $ServiceAccount"
    }
}
else {
    $taskPrincipal = New-ScheduledTaskPrincipal `
        -UserId 'NT AUTHORITY\SYSTEM' `
        -LogonType ServiceAccount `
        -RunLevel Highest
    Write-Host 'Task will run as SYSTEM (not recommended for production).' -ForegroundColor Yellow
}

$registerParams = @{
    TaskName    = $taskName
    Action      = $taskAction
    Trigger     = $taskTrigger
    Settings    = $taskSettings
    Principal   = $taskPrincipal
    Description = $taskDescription
    Force       = $true
}

# For non-gMSA standard accounts, supply the password
if ($ServiceAccount -and -not $ServiceAccount.EndsWith('$') -and $ServiceAccountPassword) {
    $plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ServiceAccountPassword))
    $registerParams['User'] = $ServiceAccount
    $registerParams['Password'] = $plainPassword
    # Remove Principal when supplying User/Password directly
    $registerParams.Remove('Principal')
}

Register-ScheduledTask @registerParams | Out-Null

# --- 7. Set directory permissions ---
# Restrict config directory to service account + Administrators
$configDir = Join-Path $installBase 'config'
$acl = Get-Acl $configDir
$acl.SetAccessRuleProtection($true, $false)  # Disable inheritance
$adminRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
    'BUILTIN\Administrators', 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$acl.AddAccessRule($adminRule)
if ($ServiceAccount) {
    $svcRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $ServiceAccount, 'Read,ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($svcRule)
}
else {
    $systemRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        'NT AUTHORITY\SYSTEM', 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($systemRule)
}
Set-Acl $configDir $acl

Write-Host ''
Write-Host "Scheduled task '$taskName' installed successfully." -ForegroundColor Green
Write-Host ''
Write-Host 'Next steps:' -ForegroundColor Cyan
Write-Host "  1. Edit configuration:  notepad $installBase\config\worker-config.json"
Write-Host "  2. Enable the task:     Enable-ScheduledTask -TaskName $taskName"
Write-Host "  3. Run manually:        Start-ScheduledTask -TaskName $taskName"
Write-Host "  4. Check status:        Get-ScheduledTask -TaskName $taskName"
Write-Host "  5. View logs:           Get-Content $installBase\logs\worker.log -Tail 50"
Write-Host "  6. Task Scheduler GUI:  taskschd.msc"
