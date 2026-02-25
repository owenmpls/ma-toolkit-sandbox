<#
.SYNOPSIS
    AD forest connection management for multi-forest environments.
.DESCRIPTION
    Provides lazy connection validation and caching for Active Directory forests.
    Forest configs ($global:ForestConfigs) are injected during session initialization
    by the session pool. Each forest entry contains Server and Credential.
#>

# Connection cache — tracks forests that have been validated in this session
if (-not $global:ForestConnectionCache) {
    $global:ForestConnectionCache = @{}
}

function Get-ADForestConnection {
    <#
    .SYNOPSIS
        Returns a validated connection for the specified AD forest.
    .DESCRIPTION
        Checks the connection cache first. On cache miss, validates connectivity
        with Get-ADDomain and caches the result. Forest configs are injected as
        $global:ForestConfigs during session initialization.
    .PARAMETER ForestName
        The forest name as configured in worker-config.json (e.g., 'corp.contoso.com').
    .EXAMPLE
        $conn = Get-ADForestConnection -ForestName 'corp.contoso.com'
        Get-ADUser -Filter {SamAccountName -eq 'jdoe'} -Server $conn.Server -Credential $conn.Credential
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ForestName
    )

    # Return cached connection if already validated
    if ($global:ForestConnectionCache.ContainsKey($ForestName) -and $global:ForestConnectionCache[$ForestName]) {
        return $global:ForestConnectionCache[$ForestName]
    }

    # Look up config (injected at session init)
    if (-not $global:ForestConfigs -or -not $global:ForestConfigs.ContainsKey($ForestName)) {
        $configured = if ($global:ForestConfigs) { $global:ForestConfigs.Keys -join ', ' } else { '(none)' }
        throw "Unknown forest '$ForestName'. Configured forests: $configured"
    }

    $config = $global:ForestConfigs[$ForestName]

    # Validate connection (lightweight -- Get-ADDomain)
    Get-ADDomain -Server $config.Server -Credential $config.Credential -ErrorAction Stop | Out-Null

    # Cache and return
    $connection = @{
        Server     = $config.Server
        Credential = $config.Credential
        ForestName = $ForestName
    }
    $global:ForestConnectionCache[$ForestName] = $connection
    return $connection
}

function Reset-ADForestConnection {
    <#
    .SYNOPSIS
        Clears the cached connection for a forest, forcing re-validation on next use.
    .PARAMETER ForestName
        The forest name to reset.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ForestName
    )

    if ($global:ForestConnectionCache.ContainsKey($ForestName)) {
        $global:ForestConnectionCache.Remove($ForestName)
    }
}
