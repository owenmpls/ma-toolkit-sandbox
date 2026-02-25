<#
.SYNOPSIS
    Microsoft Teams functions for the Hybrid Worker.
.DESCRIPTION
    Provides Teams provisioning functions.
    These run in PS 5.1 PSSessions with the MicrosoftTeams module loaded.
#>

function New-MigrationTeam {
    <#
    .SYNOPSIS
        Creates a new Microsoft Team for migration.
    .DESCRIPTION
        Wraps MicrosoftTeams\New-Team (fully qualified to avoid name collisions)
        to provision a new team and returns the team details.
    .PARAMETER DisplayName
        The display name for the new team.
    .PARAMETER Description
        Optional description for the team.
    .PARAMETER Owner
        The UPN of the team owner.
    .PARAMETER Visibility
        Team visibility: 'Private' or 'Public'. Defaults to 'Private'.
    .EXAMPLE
        $job = @{
            "FunctionName" = "New-MigrationTeam"
            "Parameters" = @{
                "DisplayName" = "Migrated Team"
                "Owner" = "admin@contoso.com"
                "Description" = "Team migrated from source tenant"
            }
        }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$DisplayName,
        [Parameter(Mandatory)] [string]$Owner,
        [string]$Description,
        [ValidateSet('Private', 'Public')]
        [string]$Visibility = 'Private'
    )

    $params = @{
        DisplayName = $DisplayName
        Owner       = $Owner
        Visibility  = $Visibility
        ErrorAction = 'Stop'
    }
    if ($Description) { $params['Description'] = $Description }

    # Fully qualified to avoid name collisions with other modules
    $team = MicrosoftTeams\New-Team @params

    return [PSCustomObject]@{
        GroupId     = $team.GroupId
        DisplayName = $team.DisplayName
        Description = $team.Description
        Visibility  = $team.Visibility
    }
}
