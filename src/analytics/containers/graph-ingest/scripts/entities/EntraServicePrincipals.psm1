function Get-EntityConfig {
    return @{
        Name         = 'entra_service_principals'
        Phase1       = $true
        Phase2       = $false
        ApiSource    = 'graph'
        OutputFile   = 'entra_service_principals'
        DetailType   = $null
    }
}

function Invoke-Phase1 {
    param(
        [Parameter(Mandatory)][System.IO.StreamWriter]$Writer,
        [Parameter(Mandatory)][ref]$RecordCount,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$EntityIds
    )

    $count = 0
    $select = 'id,appId,appDisplayName,displayName,servicePrincipalType,appOwnerOrganizationId,accountEnabled,appRoleAssignmentRequired,appRoles,oauth2PermissionScopes,tags,servicePrincipalNames,homepage,loginUrl,logoutUrl,replyUrls,keyCredentials,passwordCredentials,preferredSingleSignOnMode,samlSingleSignOnSettings,signInAudience,notes,notificationEmailAddresses,info,applicationTemplateId,verifiedPublisher,alternativeNames,tokenEncryptionKeyId,resourceSpecificApplicationPermissions,description,disabledByMicrosoftStatus,preferredTokenSigningKeyThumbprint'
    $uri = "/v1.0/servicePrincipals?`$select=$select&`$top=999"

    do {
        $response = Invoke-MgGraphRequest -Method GET -Uri $uri -ErrorAction Stop
        foreach ($sp in $response.value) {
            $Writer.WriteLine(($sp | ConvertTo-Json -Compress -Depth 5))
            $EntityIds.Add($sp.id)
            $count++
        }
        if ($count % 1000 -eq 0 -and $count -gt 0) { $Writer.Flush() }
        $uri = $response['@odata.nextLink']
    } while ($uri)

    $Writer.Flush()
    $RecordCount.Value = $count
}

function Invoke-Phase2 {
    param(
        [string[]]$EntityIds,
        [string]$OutputDirectory,
        [string]$RunId,
        [int]$PoolSize
    )
    return @{ RecordCount = 0; ChunkCount = 0 }
}

Export-ModuleMember -Function Get-EntityConfig, Invoke-Phase1, Invoke-Phase2
