# Coexistence Implementation Backlog

> **Created with AI. Pending verification by a human. Use with caution.**

This backlog contains implementation tasks for establishing cross-tenant coexistence. Items are organized by category and should be implemented in the order presented within each phase.

## Categories

| Category | Description |
|----------|-------------|
| **LT** | Low-Trust Integration — Establishing baseline collaboration with effective segmentation |
| **HT** | High-Trust Integration — Enabling enhanced collaboration with external member access |
| **MF** | Mail Flow — Configuring cross-tenant mail routing and GAL resolution |
| **HY** | Hybrid Integration — Extending coexistence to on-premises resources |
| **MC** | Migration Coexistence — Supporting cross-tenant migration scenarios |
| **IR** | Identity Rationalization — Managing identity lifecycle and cleanup |

---

## Low-Trust Integration (LT)

### LT-01: Configure Organization Relationships for Free/Busy Sharing

**Objective:** Enable calendar availability lookups between tenants.

**Prerequisites:**
- Exchange Administrator role in both tenants
- Verified domains for each tenant

**Implementation:**

```powershell
# Connect to Exchange Online
Connect-ExchangeOnline -UserPrincipalName admin@contoso.com

# Create organization relationship for partner tenant
New-OrganizationRelationship -Name "Fabrikam Partnership" `
    -DomainNames "fabrikam.com","fabrikam.onmicrosoft.com" `
    -FreeBusyAccessEnabled $true `
    -FreeBusyAccessLevel LimitedDetails `
    -TargetAutodiscoverEpr "https://autodiscover-s.outlook.com/autodiscover/autodiscover.svc/WSSecurity" `
    -TargetApplicationUri "outlook.com"
```

**Validation:**
- [ ] Organization relationship created in both tenants
- [ ] Free/busy lookups return availability data
- [ ] Calendar assistant shows partner users in scheduling assistant

**Notes:**
- For hybrid Exchange, configure separate organization relationships per domain to route lookups correctly
- Free/busy lookups do not chase redirects; targetAddress must point directly to mailbox location

---

### LT-02: Configure Cross-Tenant Access Settings for B2B Collaboration

**Objective:** Enable B2B guest collaboration with partner tenant.

**Prerequisites:**
- Global Administrator or Security Administrator role
- Partner tenant ID

**Implementation:**

```powershell
# Connect to Microsoft Graph
Connect-MgGraph -Scopes "Policy.ReadWrite.CrossTenantAccess"

# Add partner organization to cross-tenant access settings
$params = @{
    tenantId = "fabrikam-tenant-id-guid"
}
New-MgPolicyCrossTenantAccessPolicyPartner -BodyParameter $params

# Configure inbound settings (what partner users can do in your tenant)
$inboundParams = @{
    b2bCollaborationInbound = @{
        usersAndGroups = @{
            accessType = "allowed"
            targets = @(
                @{
                    target = "AllUsers"
                    targetType = "user"
                }
            )
        }
        applications = @{
            accessType = "allowed"
            targets = @(
                @{
                    target = "AllApplications"
                    targetType = "application"
                }
            )
        }
    }
    automaticUserConsentSettings = @{
        inboundAllowed = $true
    }
}
Update-MgPolicyCrossTenantAccessPolicyPartner -CrossTenantAccessPolicyConfigurationPartnerTenantId "fabrikam-tenant-id-guid" -BodyParameter $inboundParams
```

**Validation:**
- [ ] Partner organization appears in cross-tenant access settings
- [ ] Inbound B2B collaboration enabled for partner users
- [ ] Automatic redemption configured (suppresses consent prompt)

---

### LT-03: Configure Teams External Access

**Objective:** Enable Teams federation for chat, calling, and meetings with partner tenant.

**Prerequisites:**
- Teams Administrator role
- Partner tenant domain

**Implementation:**

```powershell
# Connect to Microsoft Teams
Connect-MicrosoftTeams

# Add partner domain to allowed external access list
New-CsTenantFederationConfiguration -AllowedDomains @{Add="fabrikam.com"}

# Or configure to allow all external domains (if appropriate)
Set-CsTenantFederationConfiguration -AllowFederatedUsers $true

# Verify configuration
Get-CsTenantFederationConfiguration | Select-Object AllowFederatedUsers, AllowedDomains
```

**Validation:**
- [ ] External access enabled for partner domain
- [ ] Users can search for and chat with partner users
- [ ] Federated meetings can be scheduled with partner attendees

---

### LT-04: Configure B2B Direct Connect for Shared Channels

**Objective:** Enable Teams shared channels with partner tenant.

**Prerequisites:**
- Cross-tenant access settings configured (LT-02)
- Teams Administrator role

**Implementation:**

```powershell
# Configure B2B direct connect in cross-tenant access settings
$directConnectParams = @{
    b2bDirectConnectInbound = @{
        usersAndGroups = @{
            accessType = "allowed"
            targets = @(
                @{
                    target = "AllUsers"
                    targetType = "user"
                }
            )
        }
        applications = @{
            accessType = "allowed"
            targets = @(
                @{
                    target = "Office365"
                    targetType = "application"
                }
            )
        }
    }
    b2bDirectConnectOutbound = @{
        usersAndGroups = @{
            accessType = "allowed"
            targets = @(
                @{
                    target = "AllUsers"
                    targetType = "user"
                }
            )
        }
        applications = @{
            accessType = "allowed"
            targets = @(
                @{
                    target = "Office365"
                    targetType = "application"
                }
            )
        }
    }
}
Update-MgPolicyCrossTenantAccessPolicyPartner -CrossTenantAccessPolicyConfigurationPartnerTenantId "fabrikam-tenant-id-guid" -BodyParameter $directConnectParams
```

**Validation:**
- [ ] B2B direct connect enabled for inbound and outbound
- [ ] Users can be added to shared channels in partner tenant
- [ ] Shared channel access works without tenant switching

---

### LT-05: Deploy Cross-Tenant Synchronization

**Objective:** Provision B2B guest accounts for unified people search and GAL.

**Prerequisites:**
- Entra ID P1 licensing
- Scoping group created for users to synchronize
- Cross-tenant access settings configured (LT-02)

**Implementation:**

```powershell
# Connect to Microsoft Graph
Connect-MgGraph -Scopes "Application.ReadWrite.All","Directory.ReadWrite.All"

# Create cross-tenant synchronization configuration
# Step 1: Create service principal for cross-tenant sync app
$servicePrincipal = New-MgServicePrincipal -AppId "7f1f8d3b-2c2e-4b7a-9c5a-6d5c3f4a5e6d"

# Step 2: Create synchronization job
$syncJobParams = @{
    templateId = "Azure2Azure"
}
New-MgServicePrincipalSynchronizationJob -ServicePrincipalId $servicePrincipal.Id -BodyParameter $syncJobParams

# Step 3: Configure attribute mappings (use Graph API or Entra admin center)
# Key mapping: Ensure userType is set to "Guest" for low-trust integration

# Step 4: Configure scoping filter with group-based filter (recommended)
# Use Entra admin center Provisioning blade for scoping configuration
```

**Validation:**
- [ ] Cross-tenant sync configuration created
- [ ] Scoping group membership determines sync scope
- [ ] Provisioned users appear as guests in target tenant
- [ ] Users appear in people search and GAL

**Notes:**
- Product group recommends group-based scoping filters over attribute-based filtering
- If existing guests match on altSecID, XTS assumes management but does not change userType by default

---

### LT-06: Implement Conditional Access Mitigation for External Members

**Objective:** Block external members to maintain low-trust segmentation.

**Prerequisites:**
- Entra ID P1 licensing
- Conditional Access Administrator role

**Implementation:**

```powershell
# Connect to Microsoft Graph
Connect-MgGraph -Scopes "Policy.ReadWrite.ConditionalAccess"

# Create conditional access policy to block external members
$caParams = @{
    displayName = "Block External Members - Partner Tenants"
    state = "enabled"
    conditions = @{
        users = @{
            includeUsers = @()
            includeGuestsOrExternalUsers = @{
                guestOrExternalUserTypes = "b2bCollaborationMember"
                externalTenants = @{
                    membershipKind = "enumerated"
                    members = @("fabrikam-tenant-id-guid")
                }
            }
        }
        applications = @{
            includeApplications = @("All")
        }
    }
    grantControls = @{
        operator = "OR"
        builtInControls = @("block")
    }
}
New-MgIdentityConditionalAccessPolicy -BodyParameter $caParams
```

**Validation:**
- [ ] Policy created and enabled
- [ ] External members from partner tenant are blocked
- [ ] External guests from partner tenant can still access resources

---

## High-Trust Integration (HT)

### HT-01: Promote Synchronized Users to External Members

**Objective:** Grant member-level access to synchronized users.

**Prerequisites:**
- Low-trust integration complete
- Security assessment completed for high-trust
- Conditional access mitigation removed or updated

**Implementation:**

```powershell
# Update cross-tenant sync attribute mapping to provision members instead of guests
# In the XTS provisioning configuration, update the userType transformation:
#
# Expression: "Member"
# Target attribute: userType
#
# This can be configured via:
# 1. Entra admin center > Cross-tenant synchronization > Configuration > Provisioning > Mappings
# 2. Edit the userType mapping to use constant value "Member"

# For existing guests, update userType via Graph API
Connect-MgGraph -Scopes "User.ReadWrite.All"

# Get external users from partner tenant
$externalUsers = Get-MgUser -Filter "userType eq 'Guest'" -All | Where-Object {
    $_.ExternalUserState -eq "Accepted" -and
    $_.Mail -like "*@fabrikam.com"
}

# Update to member (use with caution - review data access implications first)
foreach ($user in $externalUsers) {
    Update-MgUser -UserId $user.Id -UserType "Member"
}
```

**Validation:**
- [ ] XTS configuration updated to provision members
- [ ] New synchronized users created as external members
- [ ] Existing users converted to external members
- [ ] Users can access org-wide shared content

**Notes:**
- Review data segmentation implications before promotion
- External members can access content shared with "Everyone except external users"

---

### HT-02: Establish Multitenant Organization

**Objective:** Enable enhanced Microsoft 365 collaboration features.

**Prerequisites:**
- External members configured (HT-01)
- Entra ID P1 licensing
- Global Administrator role

**Implementation:**

```powershell
# Connect to Microsoft Graph
Connect-MgGraph -Scopes "MultiTenantOrganization.ReadWrite.All"

# Create MTO (from owner tenant)
$mtoParams = @{
    displayName = "Contoso-Fabrikam MTO"
}
New-MgTenantRelationshipMultiTenantOrganization -BodyParameter $mtoParams

# Add partner tenant as member
$memberParams = @{
    tenantId = "fabrikam-tenant-id-guid"
    role = "member"
}
New-MgTenantRelationshipMultiTenantOrganizationTenant -BodyParameter $memberParams

# Partner tenant must accept the invitation
# In partner tenant:
# Update-MgTenantRelationshipMultiTenantOrganizationJoinRequest -BodyParameter @{ addedByTenantId = "contoso-tenant-id-guid" }
```

**Validation:**
- [ ] MTO created with owner tenant
- [ ] Partner tenant joined as member
- [ ] Teams shows improved people search across tenants
- [ ] Federated chat routing works correctly

**Notes:**
- Maximum 100 tenants per MTO (soft limit; Microsoft will raise upon request)
- A tenant can only participate in one MTO at a time

---

### HT-03: Enable Device Trust in Cross-Tenant Access Settings

**Objective:** Accept device compliance claims from partner tenant for conditional access.

**Prerequisites:**
- High-trust relationship established
- Intune enrollment in home tenant for compliant device claims

**Implementation:**

```powershell
# Connect to Microsoft Graph
Connect-MgGraph -Scopes "Policy.ReadWrite.CrossTenantAccess"

# Update trust settings to accept device claims
$trustParams = @{
    inboundTrust = @{
        isMfaAccepted = $true
        isCompliantDeviceAccepted = $true
        isHybridAzureADJoinedDeviceAccepted = $true
    }
}
Update-MgPolicyCrossTenantAccessPolicyPartner -CrossTenantAccessPolicyConfigurationPartnerTenantId "fabrikam-tenant-id-guid" -BodyParameter $trustParams
```

**Validation:**
- [ ] Trust settings updated for partner organization
- [ ] Partner users pass compliant device CA policies
- [ ] Partner users pass hybrid join CA policies

**Notes:**
- Device trust eliminates need for dual device enrollment or VDI
- This is a key advantage of MTO coexistence over dual account scenarios

---

### HT-04: Configure Viva Engage for MTO

**Objective:** Enable MTO features in Viva Engage including storyline and communities.

**Prerequisites:**
- MTO established (HT-02)
- Viva Engage licensing (Core Service for basic; Viva Suite for advanced features)

**Implementation:**

```powershell
# Viva Engage MTO configuration is performed via Yammer admin center
# 1. Navigate to Yammer admin center > Network Configuration
# 2. Enable multitenant organization features
# 3. Designate hub tenant for storyline announcements

# Verify MTO configuration via Graph API
Connect-MgGraph -Scopes "Group.Read.All"

# Check for MTO-enabled communities
Get-MgGroup -Filter "groupTypes/any(c:c eq 'Unified')" | Where-Object {
    $_.AdditionalProperties.isMTO -eq $true
}
```

**Validation:**
- [ ] Viva Engage MTO features enabled
- [ ] Hub tenant configured for storyline
- [ ] Users can view cross-tenant storyline posts
- [ ] MTO communities accessible to partner users

---

## Mail Flow (MF)

### MF-01: Configure Outbound Partner Connector

**Objective:** Route mail to partner tenant directly for GAL resolution.

**Prerequisites:**
- Exchange Administrator role
- Partner tenant EOP endpoint

**Implementation:**

```powershell
# Connect to Exchange Online
Connect-ExchangeOnline -UserPrincipalName admin@contoso.com

# Create outbound connector to partner tenant
New-OutboundConnector -Name "To Fabrikam" `
    -ConnectorType "Partner" `
    -RecipientDomains "fabrikam.com","fabrikam.onmicrosoft.com","fabrikam.mail.onmicrosoft.com" `
    -SmartHosts "fabrikam-com.mail.protection.outlook.com" `
    -TlsSettings "EncryptionOnly" `
    -UseMXRecord $false `
    -Enabled $true
```

**Validation:**
- [ ] Outbound connector created
- [ ] Test mail routes through connector
- [ ] Mail arrives at partner tenant without internet routing

---

### MF-02: Configure Inbound Partner Connector

**Objective:** Attribute inbound mail from partner for GAL resolution.

**Prerequisites:**
- Exchange Administrator role
- Partner tenant ID

**Implementation:**

```powershell
# Connect to Exchange Online
Connect-ExchangeOnline -UserPrincipalName admin@contoso.com

# Create inbound connector from partner tenant
New-InboundConnector -Name "From Fabrikam" `
    -ConnectorType "Partner" `
    -SenderDomains "fabrikam.com","fabrikam.onmicrosoft.com" `
    -RequireTls $true `
    -TrustedOrganizations "fabrikam.onmicrosoft.com" `
    -Enabled $true
```

**Validation:**
- [ ] Inbound connector created
- [ ] Inbound mail attributed to connector
- [ ] Sender resolves to GAL entry (MailUser)

**Notes:**
- TrustedOrganizations parameter must include partner MOERA for proper attribution
- Unified GAL via XTS required for sender resolution

---

### MF-03: Verify Cross-Tenant Mail Flow

**Objective:** Validate end-to-end mail flow and GAL resolution.

**Prerequisites:**
- Connectors configured (MF-01, MF-02)
- Cross-tenant synchronization deployed (LT-05)

**Implementation:**

```powershell
# Test outbound mail flow
Send-MailMessage -From "user@contoso.com" -To "user@fabrikam.com" -Subject "Test" -Body "Cross-tenant mail test" -SmtpServer "smtp.office365.com"

# Verify message tracking
Get-MessageTrace -SenderAddress "user@contoso.com" -RecipientAddress "user@fabrikam.com" -StartDate (Get-Date).AddHours(-1) -EndDate (Get-Date)

# Check connector usage in message headers
# Look for: X-MS-Exchange-Organization-AuthSource and connector routing headers
```

**Validation:**
- [ ] Outbound mail routes through partner connector
- [ ] Inbound mail attributed to partner connector
- [ ] Sender displays with GAL profile information
- [ ] Distribution group membership works cross-tenant

---

## Hybrid Integration (HY)

### HY-01: Configure Hybrid External Members

**Objective:** Create on-premises AD accounts joined to B2B external members.

**Prerequisites:**
- On-premises AD infrastructure
- Microsoft Identity Manager or equivalent sync tool
- Entra Connect configured

**Implementation:**

```powershell
# This requires custom identity synchronization (MIM or third-party tool)
# XTS is incompatible with hybrid external members

# Example: Create on-premises user account
New-ADUser -Name "John Smith (Fabrikam)" `
    -SamAccountName "jsmith_fabrikam" `
    -UserPrincipalName "jsmith_fabrikam@contoso.com" `
    -Path "OU=ExternalMembers,DC=contoso,DC=com" `
    -Enabled $true

# Configure Entra Connect to sync as external authentication
# In Entra Connect:
# 1. Configure user as federated/external authentication
# 2. Map to existing B2B account in Entra ID
# 3. Ensure mail attributes flow correctly for Exchange hybrid
```

**Validation:**
- [ ] On-premises user created and synced
- [ ] User appears as hybrid external member in Entra ID
- [ ] Kerberos SSO works via App Proxy
- [ ] Exchange hybrid mail flow functions correctly

**Notes:**
- Hybrid external members are incompatible with XTS
- Use MIM or third-party tools for provisioning

---

## Migration Coexistence (MC)

### MC-01: Configure Cross-Tenant Identity Mapping (CTIM)

**Objective:** Stamp Exchange attributes on target MailUsers for mailbox migration.

**Prerequisites:**
- Exchange Administrator role in both tenants
- Source mailbox GUIDs available
- Target MailUser objects exist (via XTS or manual creation)

**Implementation:**

```powershell
# Download and run CTIM tool from Microsoft
# https://learn.microsoft.com/en-us/microsoft-365/enterprise/cross-tenant-mailbox-migration

# CTIM stamps the following attributes on target MailUser:
# - ExchangeGUID (from source mailbox)
# - ArchiveGUID (if archive exists)
# - X500 addresses (for reply-to-old-mail scenarios)

# Example manual attribute stamping (if not using CTIM):
Connect-ExchangeOnline -UserPrincipalName admin@fabrikam.com

Set-MailUser -Identity "jsmith@fabrikam.com" `
    -ExchangeGuid "source-mailbox-guid" `
    -EmailAddresses @{Add="X500:/o=Contoso/ou=Exchange Administrative Group/cn=Recipients/cn=jsmith"}
```

**Validation:**
- [ ] Target MailUser has source ExchangeGUID
- [ ] X500 addresses added for legacy reply paths
- [ ] ArchiveGUID stamped if applicable

---

### MC-02: Configure Group Synchronization

**Objective:** Synchronize security and distribution groups between tenants.

**Prerequisites:**
- Cross-tenant synchronization deployed (LT-05)
- Group sync requirements defined

**Implementation:**

```powershell
# Cross-tenant sync can synchronize group memberships
# Configure in XTS provisioning settings

# For distribution groups, create mail-enabled groups in target:
Connect-ExchangeOnline -UserPrincipalName admin@fabrikam.com

New-DistributionGroup -Name "Contoso Engineering" `
    -Alias "contoso-engineering" `
    -PrimarySmtpAddress "contoso-engineering@fabrikam.com" `
    -Members @("jsmith@fabrikam.com","kjones@fabrikam.com")
```

**Validation:**
- [ ] Groups synchronized to target tenant
- [ ] Group membership accurate
- [ ] Mail-enabled groups receive cross-tenant mail

---

### MC-03: Implement Reach-Back Access

**Objective:** Enable migrated users to access source tenant resources.

**Prerequisites:**
- Users migrated to target tenant
- B2B accounts provisioned in source tenant

**Implementation:**

```powershell
# Reach-back access uses the same B2B collaboration infrastructure
# Migrated users are provisioned as B2B accounts in their former home tenant

# In source tenant, ensure:
# 1. Cross-tenant access settings allow the target tenant
# 2. Migrated users have B2B accounts in source
# 3. Appropriate permissions retained or reassigned

# Example: Verify B2B account exists for migrated user
Connect-MgGraph -Scopes "User.Read.All"

Get-MgUser -Filter "mail eq 'jsmith@fabrikam.com' and userType eq 'Guest'" | Select-Object DisplayName, Mail, UserType
```

**Validation:**
- [ ] Migrated users can access source tenant resources
- [ ] Permissions function correctly
- [ ] SSO works without additional authentication

**Notes:**
- Reach-back access should be established immediately upon migration
- See user migration playbook for detailed reach-back configuration

---

## Identity Rationalization (IR)

### IR-01: B2B Account Conversion

**Objective:** Convert B2B accounts to internal accounts during consolidation.

**Prerequisites:**
- Migration complete
- B2B account conversion feature enabled

**Implementation:**

```powershell
# B2B account conversion is now GA
# Converts external user to internal while preserving:
# - Group memberships
# - App role assignments
# - Permissions (SharePoint, OneDrive)
# - Teams chat history

Connect-MgGraph -Scopes "User.ReadWrite.All"

# Convert user from guest/member to internal
# Note: This changes the user's authentication from home tenant to resource tenant
$conversionParams = @{
    userType = "Member"
    # Additional parameters as required by conversion process
}
Update-MgUser -UserId "user-object-id" -BodyParameter $conversionParams

# Full conversion requires:
# 1. User object update
# 2. Credential provisioning in resource tenant
# 3. Home tenant reference removal
```

**Validation:**
- [ ] User converted to internal account
- [ ] Group memberships preserved
- [ ] Permissions retained
- [ ] Teams chat history accessible

**Notes:**
- Known issues with Viva Engage (last verified 2024)
- Some applications may not fully support converted accounts

---

### IR-02: Stale Account Cleanup

**Objective:** Remove orphaned B2B accounts from disabled/deleted source users.

**Prerequisites:**
- Regular audit process established
- Source tenant user status accessible

**Implementation:**

```powershell
# XTS does not deprovision disabled/deleted users automatically
# Implement regular cleanup process

Connect-MgGraph -Scopes "User.ReadWrite.All","AuditLog.Read.All"

# Find B2B users that haven't signed in recently
$staleDate = (Get-Date).AddDays(-90)
$staleUsers = Get-MgUser -Filter "userType eq 'Guest'" -All | Where-Object {
    $_.SignInActivity.LastSignInDateTime -lt $staleDate -or
    $_.SignInActivity.LastSignInDateTime -eq $null
}

# Review and remove stale accounts
foreach ($user in $staleUsers) {
    # Verify user is truly stale before removal
    Write-Host "Stale user: $($user.DisplayName) - Last sign-in: $($user.SignInActivity.LastSignInDateTime)"

    # Remove-MgUser -UserId $user.Id -Confirm
}
```

**Validation:**
- [ ] Stale accounts identified
- [ ] Accounts verified as orphaned
- [ ] Orphaned accounts removed
- [ ] Audit log updated

---

## Implementation Tracking

| ID | Item | Status | Assigned | Notes |
|----|------|--------|----------|-------|
| LT-01 | Organization relationships | | | |
| LT-02 | Cross-tenant access settings | | | |
| LT-03 | Teams external access | | | |
| LT-04 | B2B direct connect | | | |
| LT-05 | Cross-tenant synchronization | | | |
| LT-06 | CA mitigation for external members | | | |
| HT-01 | Promote to external members | | | |
| HT-02 | Multitenant organization | | | |
| HT-03 | Device trust | | | |
| HT-04 | Viva Engage MTO | | | |
| MF-01 | Outbound partner connector | | | |
| MF-02 | Inbound partner connector | | | |
| MF-03 | Mail flow verification | | | |
| HY-01 | Hybrid external members | | | |
| MC-01 | CTIM configuration | | | |
| MC-02 | Group synchronization | | | |
| MC-03 | Reach-back access | | | |
| IR-01 | B2B account conversion | | | |
| IR-02 | Stale account cleanup | | | |

## Related Topics

- [Coexistence Overview](index.md)
- [Coexistence Test Cases](tests.md)
- [Cross-Tenant User Migration](../user-migration/index.md)
