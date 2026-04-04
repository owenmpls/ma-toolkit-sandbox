function Get-EntityConfig {
    return @{
        Name         = 'entra_users'
        ScheduleTier = 'core'
        Phase1       = $true
        Phase2       = $false
        ApiSource    = 'graph'
        OutputFile   = 'entra_users'
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
    $select = @(
        # Identity
        'id','userPrincipalName','mail','displayName','givenName','surname','mailNickname'
        # Organization
        'jobTitle','department','officeLocation','city','state','country','companyName'
        'streetAddress','postalCode','usageLocation','preferredLanguage','preferredDataLocation'
        # Contact
        'businessPhones','mobilePhone','faxNumber','otherMails'
        # Employee
        'employeeId','employeeType','employeeHireDate','employeeOrgData'
        # Account status
        'accountEnabled','userType','creationType','createdDateTime'
        'lastPasswordChangeDateTime','passwordPolicies'
        'deletedDateTime','securityIdentifier'
        # Guest / external
        'externalUserState','externalUserStateChangeDateTime','identities'
        # Licensing & sync
        'assignedLicenses','proxyAddresses'
        'onPremisesSyncEnabled','onPremisesLastSyncDateTime','onPremisesDomainName'
        'onPremisesDistinguishedName','onPremisesExtensionAttributes','onPremisesImmutableId'
        'onPremisesProvisioningErrors','onPremisesSamAccountName','onPremisesSecurityIdentifier'
        'onPremisesUserPrincipalName','serviceProvisioningErrors'
        # Activity (requires AuditLog.Read.All on the app registration)
        'signInActivity'
        # OneDrive
        'mySite'
    ) -join ','
    $uri = "/v1.0/users?`$select=$select&`$top=999"

    do {
        $response = Invoke-MgGraphRequest -Method GET -Uri $uri -ErrorAction Stop
        foreach ($user in $response.value) {
            $Writer.WriteLine(($user | ConvertTo-Json -Compress -Depth 5))
            $EntityIds.Add($user.id)
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
