#Requires -RunAsAdministrator
#Requires -Version 7.4

<#
.SYNOPSIS
    Installs the MA Toolkit Hybrid Worker as a native Windows Service.
.DESCRIPTION
    Deploys the hybrid-worker to C:\ProgramData\MaToolkit\HybridWorker\,
    builds the .NET service host, and registers a native Windows Service
    via New-Service. Supports Group Managed Service Accounts (gMSA) and
    configures service recovery for automatic restarts on failure.
.PARAMETER ConfigPath
    Path to a pre-filled worker-config.json. If not provided, copies the
    example and prompts for edits.
.PARAMETER CertificatePath
    Path to a PFX file to import into the local machine cert store.
.PARAMETER CertificatePassword
    Password for the PFX file.
.PARAMETER ServiceAccount
    Account to run the service under. Supports:
      - gMSA: 'DOMAIN\gmsaAccount$' (recommended for production)
      - Standard: 'DOMAIN\serviceAccount' (requires -ServiceAccountPassword)
      - LocalSystem: omit this parameter (default, not recommended for production)
.PARAMETER ServiceAccountPassword
    Password for a standard service account. Not needed for gMSA.
#>
param(
    [string]$ConfigPath,
    [string]$CertificatePath,
    [SecureString]$CertificatePassword,
    [string]$ServiceAccount,
    [SecureString]$ServiceAccountPassword
)

$ErrorActionPreference = 'Stop'
$installBase = 'C:\ProgramData\MaToolkit\HybridWorker'
$serviceName = 'MaToolkitHybridWorker'
$serviceDisplayName = 'MA Toolkit Hybrid Worker'
$serviceDescription = 'Migration Automation Toolkit - Hybrid Worker Service'

# --- 1. Verify prerequisites ---
$pwshVersion = $PSVersionTable.PSVersion
if ($pwshVersion -lt [Version]'7.4') { throw "PowerShell 7.4+ required. Got: $pwshVersion" }

$winps = Get-Command powershell.exe -ErrorAction SilentlyContinue
if (-not $winps) { throw 'Windows PowerShell 5.1 (powershell.exe) not found.' }

$dotnetSdk = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetSdk) { throw '.NET SDK required to build the service host. Install from https://dot.net' }

# Verify WinRM is running (needed for PSSession pool)
$winrm = Get-Service WinRM -ErrorAction SilentlyContinue
if (-not $winrm -or $winrm.Status -ne 'Running') {
    Write-Host 'Enabling PSRemoting...'
    Enable-PSRemoting -Force -SkipNetworkProfileCheck
}

# Check for existing service
$existingService = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($existingService) {
    throw "Service '$serviceName' already exists. Run Uninstall-HybridWorker.ps1 first or stop and update manually."
}

# --- 2. Create directory structure ---
$dirs = @('current', 'staging', 'previous', 'config', 'logs')
foreach ($dir in $dirs) {
    $path = Join-Path $installBase $dir
    if (-not (Test-Path $path)) { New-Item -Path $path -ItemType Directory -Force | Out-Null }
}

# --- 3. Copy worker files to current\ ---
$sourceDir = Split-Path -Parent $PSScriptRoot  # hybrid-worker/ root
$itemsToCopy = @('src', 'modules', 'dotnet-libs', 'version.txt')
foreach ($item in $itemsToCopy) {
    $src = Join-Path $sourceDir $item
    $dst = Join-Path $installBase "current\$item"
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination $dst -Recurse -Force
    }
}

# --- 4. Build and publish the .NET service host ---
Write-Host 'Building .NET service host...'
$serviceHostProject = Join-Path $sourceDir 'service-host'
$publishOutput = Join-Path $installBase 'current\service-host'

dotnet publish $serviceHostProject `
    -c Release `
    -r win-x64 `
    --self-contained `
    -o $publishOutput `
    --nologo

if ($LASTEXITCODE -ne 0) { throw 'Failed to build .NET service host.' }
Write-Host 'Service host built successfully.'

# --- 5. Configuration ---
if ($ConfigPath -and (Test-Path $ConfigPath)) {
    Copy-Item $ConfigPath -Destination (Join-Path $installBase 'config\worker-config.json') -Force
}
elseif (-not (Test-Path (Join-Path $installBase 'config\worker-config.json'))) {
    $exampleConfig = Join-Path $sourceDir 'config\worker-config.example.json'
    Copy-Item $exampleConfig -Destination (Join-Path $installBase 'config\worker-config.json')
    Write-Host 'IMPORTANT: Edit config\worker-config.json before starting the service.' -ForegroundColor Yellow
}

# --- 6. Import certificate if provided ---
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

# --- 7. Register Windows Service ---
$serviceHostExe = Join-Path $publishOutput 'HybridWorker.ServiceHost.exe'

$newServiceParams = @{
    Name           = $serviceName
    BinaryPathName = $serviceHostExe
    DisplayName    = $serviceDisplayName
    Description    = $serviceDescription
    StartupType    = 'Automatic'
}

# Configure service account
if ($ServiceAccount) {
    if ($ServiceAccount.EndsWith('$')) {
        # gMSA — no password needed
        $newServiceParams['Credential'] = [PSCredential]::new($ServiceAccount, (New-Object SecureString))
        Write-Host "Service will run as gMSA: $ServiceAccount"
    }
    elseif ($ServiceAccountPassword) {
        $newServiceParams['Credential'] = [PSCredential]::new($ServiceAccount, $ServiceAccountPassword)
        Write-Host "Service will run as: $ServiceAccount"
    }
    else {
        throw "ServiceAccountPassword required for non-gMSA account '$ServiceAccount'."
    }
}
else {
    Write-Host 'Service will run as LocalSystem (not recommended for production).' -ForegroundColor Yellow
}

New-Service @newServiceParams

# --- 8. Configure service recovery (restart on failure) ---
# sc.exe failure: restart after 10s, 30s, 60s; reset counter after 1 day
& sc.exe failure $serviceName reset= 86400 actions= restart/10000/restart/30000/restart/60000
& sc.exe failureflag $serviceName 1

# --- 9. Set directory permissions ---
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
Write-Host "Service '$serviceName' installed successfully." -ForegroundColor Green
Write-Host ''
Write-Host 'Next steps:' -ForegroundColor Cyan
Write-Host "  1. Edit configuration:  notepad $installBase\config\worker-config.json"
Write-Host "  2. Start the service:   Start-Service $serviceName"
Write-Host "  3. Check status:        Get-Service $serviceName"
Write-Host "  4. View logs:           Get-Content $installBase\logs\worker.log -Tail 50"
Write-Host "  5. Event Log:           Get-WinEvent -ProviderName $serviceName -MaxEvents 20"
