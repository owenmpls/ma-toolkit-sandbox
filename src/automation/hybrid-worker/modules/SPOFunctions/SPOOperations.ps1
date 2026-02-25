<#
.SYNOPSIS
    SharePoint Online functions for the Hybrid Worker.
.DESCRIPTION
    Provides SharePoint Online site provisioning functions.
    These run in PS 5.1 PSSessions with the SPO Management Shell module loaded.
#>

function New-MigrationSPOSite {
    <#
    .SYNOPSIS
        Creates a new SharePoint Online site for migration.
    .DESCRIPTION
        Wraps New-SPOSite to provision a new site collection with the specified
        properties and returns the site details.
    .PARAMETER Url
        The URL for the new site collection.
    .PARAMETER Owner
        The UPN of the site owner.
    .PARAMETER Title
        The title for the site collection.
    .PARAMETER Template
        The site template (e.g., 'STS#3' for modern team site). Defaults to 'STS#3'.
    .PARAMETER StorageQuota
        Storage quota in MB. Defaults to 1024.
    .PARAMETER ResourceQuota
        Resource quota. Defaults to 0 (unlimited).
    .EXAMPLE
        $job = @{
            "FunctionName" = "New-MigrationSPOSite"
            "Parameters" = @{
                "Url" = "https://contoso.sharepoint.com/sites/migrated-team"
                "Owner" = "admin@contoso.com"
                "Title" = "Migrated Team Site"
            }
        }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Url,
        [Parameter(Mandatory)] [string]$Owner,
        [Parameter(Mandatory)] [string]$Title,
        [string]$Template = 'STS#3',
        [int]$StorageQuota = 1024,
        [int]$ResourceQuota = 0
    )

    New-SPOSite -Url $Url -Owner $Owner -Title $Title `
        -Template $Template -StorageQuota $StorageQuota `
        -ResourceQuota $ResourceQuota -ErrorAction Stop

    $site = Get-SPOSite -Identity $Url -ErrorAction Stop

    return [PSCustomObject]@{
        Url           = $site.Url
        Title         = $site.Title
        Owner         = $site.Owner
        Template      = $site.Template
        StorageQuota  = $site.StorageQuota
        Status        = $site.Status
    }
}
