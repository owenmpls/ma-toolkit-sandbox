<#
.SYNOPSIS
    AD forest configuration manager for the Hybrid Worker.
.DESCRIPTION
    Validates forest configuration entries and orchestrates credential
    pre-fetching for multi-forest Active Directory environments.
#>

function Initialize-ForestManager {
    <#
    .SYNOPSIS
        Validates forest configurations from the worker config.
    .DESCRIPTION
        Checks for duplicate forest names, required fields (name, server,
        credentialSecret), and returns the validated forest config array.
    .PARAMETER Forests
        Array of forest config objects from serviceConnections.activeDirectory.forests.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [array]$Forests
    )

    if ($Forests.Count -eq 0) {
        throw 'activeDirectory is enabled but no forests are configured.'
    }

    # Check for duplicate names
    $names = @($Forests | ForEach-Object { $_.name })
    $duplicates = $names | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name }
    if ($duplicates) {
        throw "Duplicate forest names: $($duplicates -join ', ')"
    }

    # Validate required fields per forest
    foreach ($forest in $Forests) {
        $forestName = $forest.name
        if ([string]::IsNullOrWhiteSpace($forestName)) {
            throw 'Forest entry is missing required field: name'
        }
        if ([string]::IsNullOrWhiteSpace($forest.server)) {
            throw "Forest '$forestName' is missing required field: server"
        }
        if ([string]::IsNullOrWhiteSpace($forest.credentialSecret)) {
            throw "Forest '$forestName' is missing required field: credentialSecret"
        }
    }

    Write-WorkerLog -Message "Forest manager initialized: $($Forests.Count) forest(s) configured ($($names -join ', '))." -Properties @{
        ForestCount = $Forests.Count
        ForestNames = ($names -join ', ')
    }

    return $Forests
}
