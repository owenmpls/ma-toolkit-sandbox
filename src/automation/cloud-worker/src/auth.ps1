<#
.SYNOPSIS
    Authentication management for the PowerShell Cloud Worker.
.DESCRIPTION
    Handles Azure managed identity login, Key Vault secret retrieval,
    Microsoft Graph, and Exchange Online authentication.
#>

function Connect-WorkerAzure {
    <#
    .SYNOPSIS
        Connects to Azure using managed identity for Key Vault access.
    #>
    [CmdletBinding()]
    param()

    Write-WorkerLog -Message 'Connecting to Azure with managed identity...'

    try {
        # In Azure Container Apps, system-assigned managed identity is available automatically.
        # For local development, falls back to environment variables (AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET).
        Connect-AzAccount -Identity -ErrorAction Stop | Out-Null
        Write-WorkerLog -Message 'Successfully connected to Azure with managed identity.'
    }
    catch {
        Write-WorkerLog -Message "Managed identity auth failed, attempting environment credential: $($_.Exception.Message)" -Severity Warning
        try {
            if ($env:AZURE_TENANT_ID -and $env:AZURE_CLIENT_ID -and $env:AZURE_CLIENT_SECRET) {
                $secureSecret = ConvertTo-SecureString $env:AZURE_CLIENT_SECRET -AsPlainText -Force
                $credential = [System.Management.Automation.PSCredential]::new($env:AZURE_CLIENT_ID, $secureSecret)
                Connect-AzAccount -ServicePrincipal -Credential $credential -Tenant $env:AZURE_TENANT_ID -ErrorAction Stop | Out-Null
                Write-WorkerLog -Message 'Successfully connected to Azure with service principal credentials.'
            }
            else {
                throw 'No environment credentials available (AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET).'
            }
        }
        catch {
            throw "Failed to connect to Azure: $($_.Exception.Message)"
        }
    }
}

function Get-WorkerAppSecret {
    <#
    .SYNOPSIS
        Retrieves the application client secret from Azure Key Vault.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$KeyVaultName,

        [Parameter(Mandatory)]
        [string]$SecretName
    )

    Write-WorkerLog -Message "Retrieving secret '$SecretName' from Key Vault '$KeyVaultName'..."

    try {
        $secret = Get-AzKeyVaultSecret -VaultName $KeyVaultName -Name $SecretName -ErrorAction Stop
        $plainText = $secret.SecretValue | ConvertFrom-SecureString -AsPlainText
        Write-WorkerLog -Message 'Successfully retrieved app secret from Key Vault.'
        return $plainText
    }
    catch {
        throw "Failed to retrieve secret '$SecretName' from Key Vault '$KeyVaultName': $($_.Exception.Message)"
    }
}

function Get-ExchangeOnlineAccessToken {
    <#
    .SYNOPSIS
        Obtains an OAuth access token for Exchange Online using client credentials.
    .DESCRIPTION
        Uses direct HTTP OAuth token endpoint since EXO module app-only auth
        natively requires certificate auth. We obtain a token with client secret
        and pass it to Connect-ExchangeOnline -AccessToken.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$TenantId,

        [Parameter(Mandatory)]
        [string]$AppId,

        [Parameter(Mandatory)]
        [string]$AppSecret
    )

    $tokenUrl = 'https://login.microsoftonline.com/{0}/oauth2/v2.0/token' -f $TenantId
    $body = @{
        client_id     = $AppId
        client_secret = $AppSecret
        scope         = 'https://outlook.office365.com/.default'
        grant_type    = 'client_credentials'
    }

    try {
        $response = Invoke-RestMethod -Uri $tokenUrl -Method Post -Body $body -ContentType 'application/x-www-form-urlencoded' -ErrorAction Stop
        return $response.access_token
    }
    catch {
        throw "Failed to obtain Exchange Online access token: $($_.Exception.Message)"
    }
}

function Initialize-RunspaceAuth {
    <#
    .SYNOPSIS
        Initializes MgGraph and ExchangeOnline connections within a runspace.
    .DESCRIPTION
        Called during runspace initialization to establish authenticated sessions.
        Returns a scriptblock that can be invoked inside the runspace.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$TenantId,

        [Parameter(Mandatory)]
        [string]$AppId,

        [Parameter(Mandatory)]
        [string]$AppSecret,

        [Parameter(Mandatory)]
        [int]$RunspaceIndex
    )

    Write-WorkerLog -Message "Initializing auth for runspace $RunspaceIndex..." -Properties @{ RunspaceIndex = $RunspaceIndex }

    # Build MgGraph credential
    $secureSecret = ConvertTo-SecureString $AppSecret -AsPlainText -Force
    $clientCredential = [System.Management.Automation.PSCredential]::new($AppId, $secureSecret)

    # Connect to Microsoft Graph
    try {
        Connect-MgGraph -TenantId $TenantId -ClientSecretCredential $clientCredential -NoWelcome -ErrorAction Stop
        Write-WorkerLog -Message "Runspace ${RunspaceIndex}: MgGraph connected." -Properties @{ RunspaceIndex = $RunspaceIndex }
    }
    catch {
        throw "Runspace ${RunspaceIndex}: Failed to connect MgGraph: $($_.Exception.Message)"
    }

    # Connect to Exchange Online
    try {
        $exoToken = Get-ExchangeOnlineAccessToken -TenantId $TenantId -AppId $AppId -AppSecret $AppSecret
        $connectParams = @{
            AccessToken  = $exoToken
            Organization = $TenantId
            ShowBanner   = $false
            ErrorAction  = 'Stop'
        }
        Connect-ExchangeOnline @connectParams
        Write-WorkerLog -Message "Runspace ${RunspaceIndex}: Exchange Online connected." -Properties @{ RunspaceIndex = $RunspaceIndex }
    }
    catch {
        throw "Runspace ${RunspaceIndex}: Failed to connect Exchange Online: $($_.Exception.Message)"
    }
}

function Get-RunspaceAuthScriptBlock {
    <#
    .SYNOPSIS
        Returns a scriptblock that initializes auth sessions inside a runspace.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$TenantId,

        [Parameter(Mandatory)]
        [string]$AppId,

        [Parameter(Mandatory)]
        [string]$AppSecret
    )

    $authScript = {
        param($TenantId, $AppId, $AppSecret, $RunspaceIndex)

        # Connect MgGraph
        $secureSecret = ConvertTo-SecureString $AppSecret -AsPlainText -Force
        $clientCredential = [System.Management.Automation.PSCredential]::new($AppId, $secureSecret)
        Connect-MgGraph -TenantId $TenantId -ClientSecretCredential $clientCredential -NoWelcome -ErrorAction Stop

        # Get EXO token and connect
        $tokenUrl = 'https://login.microsoftonline.com/{0}/oauth2/v2.0/token' -f $TenantId
        $body = @{
            client_id     = $AppId
            client_secret = $AppSecret
            scope         = 'https://outlook.office365.com/.default'
            grant_type    = 'client_credentials'
        }
        $response = Invoke-RestMethod -Uri $tokenUrl -Method Post -Body $body -ContentType 'application/x-www-form-urlencoded' -ErrorAction Stop
        $exoParams = @{
            AccessToken  = $response.access_token
            Organization = $TenantId
            ShowBanner   = $false
            ErrorAction  = 'Stop'
        }
        Connect-ExchangeOnline @exoParams

        # Store auth config for mid-lifetime EXO token refresh
        $global:EXOAuthConfig = @{
            TenantId  = $TenantId
            AppId     = $AppId
            AppSecret = $AppSecret
        }
        $global:EXOAuthTime = [DateTime]::UtcNow
    }

    return $authScript
}
