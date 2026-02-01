# Cross-Tenant User Migration Implementation Backlog

> **Created with AI. Pending verification by a human. Use with caution.**

This document contains the implementation backlog for cross-tenant user migration. Each backlog item includes description, conditions, effort estimate, dependencies, and implementation guidance. Validation test cases are maintained separately in [tests.md](tests.md).

## Dependencies on Other Playbook Sections

| Playbook Section | Required Deliverables |
|------------------|----------------------|
| **Coexistence** | Multi-Tenant Organization (MTO) established; Cross-Tenant Synchronization (XTS) configured for external member provisioning; Organization relationships configured for free/busy federation |
| **Sensitivity Labels** | Sensitivity labels created in target tenant with matching label IDs to preserve encryption on OneDrive documents during migration |

## Backlog Categories

- [Test Account Setup](#category-test-account-setup)
- [Migration Infrastructure (Source to Target)](#category-migration-infrastructure-source-to-target)
- [Reverse Migration Infrastructure (Target to Source)](#category-reverse-migration-infrastructure-target-to-source)
- [Target Environment Preparation](#category-target-environment-preparation)
- [Source Environment Preparation](#category-source-environment-preparation)
- [Automation Development](#category-automation-development)
- [Runbook Development](#category-runbook-development)
- [End-to-End Validation Testing](#category-end-to-end-validation-testing)

## Backlog Item Format

- **ID**: Category prefix + 2-digit number
- **Description**: What the item accomplishes
- **Conditions**: When this item is optional based on decisions or environment (None if always required)
- **Effort**: Low (1-3 days), Medium (3-5 days), High (1-2 weeks), Very High (2+ weeks)
- **Dependencies**: Prerequisites that must be completed first
- **Implementation Guidance**: Step-by-step instructions
- **Validation Tests**: Link to test cases in [tests.md](tests.md)

## Rollback Limitations

- **Migration Orchestrator**: Does not support reverse migration. All rollback migrations must use standalone cross-tenant migration tools.
- **Teams Chat and Meetings**: No reverse migration path exists. Teams chat migrated via orchestrator cannot be migrated back to source.
- **Identity Reversion**: Both target and source identity conversions can be reverted, but email address handling may require environment-specific intervention.

---

## Category: Test Account Setup

### TA-01: Provision Test Accounts for Validation Testing

**Description**

Provision test accounts in source and target tenants to support validation testing of all migration infrastructure and procedures.

**Conditions**: None

**Effort**: Medium

**Dependencies**

- Coexistence backlog: MTO and XTS baseline configuration complete
- Source tenant administrative access
- Target tenant administrative access (for hybrid topologies)

**Implementation Guidance**

1. Determine minimum test account count:
   - Minimum 5 accounts for basic migration testing
   - Additional accounts for topology variations (hybrid vs. cloud-only)
   - Additional accounts for hold/compliance testing
   - Additional accounts for iterative testing
   - Recommended: 10-15 test accounts total

2. Provision test accounts in source tenant:
   - Create test user accounts consistent with production provisioning
   - Assign Microsoft 365 licenses (E3/E5 or equivalent)
   - Ensure mailboxes contain test data
   - Ensure OneDrive sites contain test files
   - Include variety of configurations:
     - Standard mailbox (no archive)
     - Mailbox with archive enabled
     - Mailbox with litigation hold (for private preview testing)
     - OneDrive with shared files
     - OneDrive with sensitivity-labeled files

3. Provision test accounts in target tenant (hybrid topology):
   - Create corresponding accounts in target Active Directory
   - Place in staging OU excluded from Entra Connect sync scope
   - Derive and document immutable ID for each account
   - Do NOT sync to Entra ID until hard match process is tested

4. Provision test accounts in target tenant (cloud-only topology):
   - Rely on Cross-Tenant Synchronization to provision external members
   - Verify external members created correctly
   - Do NOT assign licenses until CTIM executed

5. Document test account inventory with source UPN, target UPN, mailbox GUID, and immutable ID

**Validation Tests**: [Test Account Validation](tests.md#test-account-validation)

---

## Category: Migration Infrastructure (Source to Target)

### MI-01: Configure Cross-Tenant Mailbox Migration Infrastructure

**Description**

Configure infrastructure for cross-tenant mailbox migration from source to target tenant, including migration application, trust relationships, migration endpoint, and organization relationship settings.

**Conditions**: None

**Effort**: Medium

**Dependencies**

- Coexistence backlog: Baseline organization relationships established
- Global Administrator access in both tenants
- Cross-Tenant User Data Migration licenses procured

**Implementation Guidance**

1. Create migration application in target tenant Azure AD:
   - Navigate to Azure AD > App Registrations > New registration
   - Add API permission: Office 365 Exchange Online > Application Permissions > Mailbox.Migration
   - Create client secret and securely store
   - Document Application (client) ID

2. Grant admin consent in target tenant:
   - Enterprise Applications > Migration app > API Permissions > Grant admin consent

3. Create consent URL and grant consent in source tenant:
   ```
   https://login.microsoftonline.com/[source-tenant].onmicrosoft.com/adminconsent?client_id=[app_id]&redirect_uri=https://office.com
   ```

4. Create mail-enabled security group in source tenant to scope eligible mailboxes

5. Configure organization relationship in target tenant (Inbound):
   ```powershell
   Connect-ExchangeOnline -UserPrincipalName admin@target.onmicrosoft.com
   Set-OrganizationRelationship "Source Tenant" -Enabled:$true -MailboxMoveEnabled:$true -MailboxMoveCapability Inbound
   ```

6. Configure organization relationship in source tenant (RemoteOutbound):
   ```powershell
   Connect-ExchangeOnline -UserPrincipalName admin@source.onmicrosoft.com
   Set-OrganizationRelationship "Target Tenant" -Enabled:$true -MailboxMoveEnabled:$true -MailboxMoveCapability RemoteOutbound -MailboxMovePublishedScopes "[SecurityGroupIdentity]"
   ```

7. Create migration endpoint in target tenant:
   ```powershell
   New-MigrationEndpoint -Name "CrossTenantEndpoint" -RemoteServer "outlook.office.com" -ApplicationId $AppId -AppSecretKeyVaultUrl "[key-vault-url]" -ExchangeRemoteMove:$true -RemoteTenant $SourceTenantId
   ```

8. Validate migration endpoint:
   ```powershell
   Test-MigrationServerAvailability -Endpoint "CrossTenantEndpoint"
   ```

**Validation Tests**: [MI-01 Tests](tests.md#mi-01-mailbox-migration-infrastructure)

---

### MI-02: Configure Cross-Tenant OneDrive Migration Infrastructure

**Description**

Configure infrastructure for cross-tenant OneDrive migration from source to target tenant.

**Conditions**: None

**Effort**: Medium

**Dependencies**

- Global Administrator access in both tenants
- SharePoint Administrator access in both tenants
- Cross-Tenant User Data Migration licenses procured

**Implementation Guidance**

1. Install required PowerShell modules:
   ```powershell
   Install-Module Microsoft.Online.SharePoint.PowerShell -Force
   Install-Module Microsoft.Graph -Force
   ```

2. Establish trust from target to source:
   ```powershell
   Connect-SPOService -Url https://[target-tenant]-admin.sharepoint.com
   Set-SPOCrossTenantRelationship -Scenario MnA -PartnerRole Source -PartnerCrossTenantHostUrl https://[source-tenant]-my.sharepoint.com
   ```

3. Establish trust from source to target:
   ```powershell
   Connect-SPOService -Url https://[source-tenant]-admin.sharepoint.com
   Set-SPOCrossTenantRelationship -Scenario MnA -PartnerRole Target -PartnerCrossTenantHostUrl https://[target-tenant]-my.sharepoint.com
   ```

4. Verify trust relationship:
   ```powershell
   Get-SPOCrossTenantRelationship -Scenario MnA
   ```

**Validation Tests**: [MI-02 Tests](tests.md#mi-02-onedrive-migration-infrastructure)

---

### MI-03: Configure Cross-Tenant Identity Mapping (CTIM)

**Description**

Configure CTIM to automate stamping of Exchange attributes (ExchangeGUID, ArchiveGUID, X500 addresses) on target MailUser objects.

**Conditions**: Required when using Migration Orchestrator; optional but recommended for standalone migration

**Effort**: Low

**Dependencies**

- Coexistence backlog: XTS provisioning external members
- Global Administrator access in both tenants
- Exchange Administrator role in both tenants

**Implementation Guidance**

1. Install required PowerShell modules:
   ```powershell
   Install-Module ExchangeOnlineManagement -Force
   Install-Module Microsoft.Graph -Force
   Install-Module CrossTenantIdentityMapping -AllowPrerelease -Force
   ```

2. Grant CTIM application permissions in source tenant:
   ```powershell
   Connect-MgGraph -Scopes "Application.ReadWrite.All","AppRoleAssignment.ReadWrite.All"
   # Grant CTIM service principal Exchange Administrator role
   # Grant CTIM service principal Exchange.ManageAsApp API permission
   ```

3. Grant CTIM application permissions in target tenant (repeat step 2)

4. Verify CTIM prerequisites:
   - Target users exist as MailUser objects (not mailboxes)
   - Target users do NOT have Exchange licenses assigned
   - Target users do NOT have email addresses from non-accepted domains

**Validation Tests**: [MI-03 Tests](tests.md#mi-03-ctim-configuration)

---

### MI-04: Configure Migration Orchestrator

**Description**

Configure the Migration Orchestrator for unified orchestration of mailbox, OneDrive, Teams chat, and Teams meeting migration.

**Conditions**: Only required if using Migration Orchestrator; skip if using standalone migration tools

**Effort**: Medium

**Dependencies**

- MI-01: Cross-Tenant Mailbox Migration Infrastructure
- MI-02: Cross-Tenant OneDrive Migration Infrastructure
- MI-03: Cross-Tenant Identity Mapping
- Microsoft 365 E3/E5 licenses in both tenants

**Implementation Guidance**

1. Verify licensing prerequisites:
   - Both tenants have Microsoft 365 E3/E5 or equivalent
   - Cross-Tenant User Data Migration add-on licenses available

2. Install Orchestrator PowerShell module:
   ```powershell
   Install-Module MigrationOrchestrator -AllowPrerelease -Force
   ```

3. Connect to Microsoft Graph:
   ```powershell
   Connect-MgGraph -Scopes "User.Read.All","CrossTenantUserDataMigration.ReadWrite.All"
   ```

4. Configure orchestrator tenant relationship in both tenants

5. Configure Teams migration prerequisites:
   ```powershell
   Connect-MicrosoftTeams
   Get-CsTenantFederationConfiguration  # Verify federated users allowed
   # Enable Exchange autoforwarding for meetings migration
   ```

6. Run standalone validation for test users

**Validation Tests**: [MI-04 Tests](tests.md#mi-04-migration-orchestrator)

---

### PP-01: Enable Private Preview for On-Hold Mailbox Migration

**Description**

Request and enable the private preview feature that allows migration of mailboxes with litigation holds, eDiscovery holds, or other compliance holds.

**Conditions**: Only required if source tenant has mailboxes on hold that must be migrated

**Effort**: Low (but may have lead time waiting for Microsoft response)

**Dependencies**

- MI-01: Cross-Tenant Mailbox Migration Infrastructure
- Organizational authorization to acknowledge private preview terms
- Contact with Microsoft account team or support

**Implementation Guidance**

1. Request private preview access:
   - Contact Microsoft account team or open support request
   - Request access to the private preview for cross-tenant mailbox migration with holds
   - Microsoft will provide acknowledgment language via email

2. Review and acknowledge preview terms:
   - Designated organizational representative reviews terms
   - Representative acknowledges terms via email to Microsoft
   - This authorizes Microsoft to enable the preview on your tenants

3. Wait for preview enablement:
   - Microsoft enables the private preview on source and target tenants
   - Confirmation provided via email
   - Lead time varies; plan accordingly

4. Verify preview is enabled by testing migration of held mailbox

5. Document preview limitations:
   - Active mailbox content migrates to target
   - Substrate folders (ComponentShared, SubstrateHolds) remain in source
   - Source MailUser must be retained for eDiscovery access

**Validation Tests**: [PP-01 Tests](tests.md#pp-01-private-preview-hold-migration)

---

## Category: Reverse Migration Infrastructure (Target to Source)

### RI-01: Configure Reverse Mailbox Migration Infrastructure

**Description**

Configure infrastructure to support mailbox migration from target back to source tenant for rollback scenarios using standalone cross-tenant migration tools.

**Conditions**: None

**Effort**: Medium

**Dependencies**

- MI-01: Cross-Tenant Mailbox Migration Infrastructure (forward direction)
- Global Administrator access in both tenants

**Implementation Guidance**

1. Create migration application in source tenant Azure AD (same process as MI-01 but reversed)

2. Grant admin consent in source tenant

3. Create consent URL for target tenant and grant consent

4. Create mail-enabled security group in target tenant for reverse migration scope

5. Configure organization relationship in source tenant (add Inbound for reverse):
   ```powershell
   Connect-ExchangeOnline -UserPrincipalName admin@source.onmicrosoft.com
   Set-OrganizationRelationship "Target Tenant" -MailboxMoveCapability @{Add="Inbound"}
   ```

6. Configure organization relationship in target tenant (add RemoteOutbound for reverse):
   ```powershell
   Connect-ExchangeOnline -UserPrincipalName admin@target.onmicrosoft.com
   Set-OrganizationRelationship "Source Tenant" -MailboxMoveCapability @{Add="RemoteOutbound"} -MailboxMovePublishedScopes "[SecurityGroupIdentity]"
   ```

7. Create reverse migration endpoint in source tenant

8. Validate reverse migration endpoint

**Validation Tests**: [RI-01 Tests](tests.md#ri-01-reverse-mailbox-migration)

---

### RI-02: Configure Reverse OneDrive Migration Infrastructure

**Description**

Configure infrastructure to support OneDrive migration from target back to source tenant for rollback scenarios.

**Conditions**: None

**Effort**: Medium

**Dependencies**

- MI-02: Cross-Tenant OneDrive Migration Infrastructure (forward direction)
- SharePoint Administrator access in both tenants

**Implementation Guidance**

1. Verify existing trust relationship supports bidirectional migration:
   ```powershell
   Get-SPOCrossTenantRelationship -Scenario MnA
   ```

2. If not already bidirectional, establish reverse trust:
   ```powershell
   # In source tenant (now acting as target for reverse)
   Set-SPOCrossTenantRelationship -Scenario MnA -PartnerRole Target -PartnerCrossTenantHostUrl https://[target-tenant]-my.sharepoint.com

   # In target tenant (now acting as source for reverse)
   Set-SPOCrossTenantRelationship -Scenario MnA -PartnerRole Source -PartnerCrossTenantHostUrl https://[source-tenant]-my.sharepoint.com
   ```

3. Verify bidirectional trust (both tenants show Source and Target relationships)

**Validation Tests**: [RI-02 Tests](tests.md#ri-02-reverse-onedrive-migration)

---

## Category: Target Environment Preparation

### TE-01: Configure Target Entra Connect for Hard Match

**Description**

Configure Entra Connect filtering in target tenant to support hard match process for provisioned AD accounts.

**Conditions**: Only required for hybrid target topologies

**Effort**: High

**Dependencies**

- Target Active Directory infrastructure
- Target Entra Connect installed and operational

**Implementation Guidance**

1. Determine filtering approach:
   - **OU-based filtering**: Create staging OU; exclude from sync scope until ready
   - **Attribute-based filtering**: Use custom attribute to control sync eligibility

2. **Option A: OU-Based Filtering**
   - Create staging OU structure (e.g., OU=MigrationStaging)
   - Configure Entra Connect wizard to exclude staging OU
   - Process: Stamp immutable ID on cloud account → Move AD account to production OU → Hard match occurs

3. **Option B: Attribute-Based Filtering**
   - Select attribute (e.g., extensionAttribute15 = "MigrationStaging")
   - Create custom sync rule with scoping filter
   - Process: Stamp immutable ID → Clear attribute → Hard match occurs

4. Test filtering with test account

**Validation Tests**: [TE-01 Tests](tests.md#te-01-entra-connect-hard-match)

---

### TE-02: Configure Target IGA/JML Integration for Hybrid Topology

**Description**

Work with target IGA/JML system owners to adjust provisioning to create MailUser objects instead of remote mailboxes for migrating users.

**Conditions**: Only required for hybrid target topologies with IGA/JML systems

**Effort**: Very High

**Dependencies**

- TE-01: Target Entra Connect Configuration
- Understanding of existing IGA/JML provisioning workflows
- Access to IGA/JML system administrators

**Implementation Guidance**

1. Document current IGA/JML provisioning workflow

2. Identify required modifications:
   - Migration users must NOT have remote mailboxes provisioned
   - License assignment must be deferred until after CTIM execution

3. Design modified provisioning workflow (separate path for migration users or conditional logic)

4. Implement and test with migration engineers and IGA/JML administrators

5. Document handoff process for post-migration authority transition

**Validation Tests**: [TE-02 Tests](tests.md#te-02-iga-jml-hybrid)

---

### TE-03: Configure Target IGA/JML Integration for Cloud-Only Topology

**Description**

Establish process for target IGA/JML systems to assume lifecycle management authority over external members provisioned by Cross-Tenant Synchronization.

**Conditions**: Only required for cloud-only target topologies with IGA/JML systems

**Effort**: High

**Dependencies**

- Coexistence backlog: XTS provisioning external members
- Understanding of target IGA/JML system capabilities

**Implementation Guidance**

1. Document current state (external members provisioned by XTS)

2. Determine handoff timing:
   - Pre-migration, at-migration, or post-migration (post-migration typically safest)

3. Design IGA onboarding process:
   - How will IGA discover converted internal members?
   - What identity correlation will be used?
   - How will group memberships be handled?

4. Document XTS descoping requirements (see TE-04)

5. Implement IGA onboarding workflow and test

**Validation Tests**: [TE-03 Tests](tests.md#te-03-iga-jml-cloud)

---

### TE-04: Configure Cross-Tenant Sync Descoping and Account Restoration

**Description**

Establish process for removing migrated source accounts from XTS scope and restoring soft-deleted external members in target tenant.

**Conditions**: None

**Effort**: Medium

**Dependencies**

- Coexistence backlog: XTS configuration
- Target Entra ID administrative access

**Implementation Guidance**

1. Understand XTS descoping behavior:
   - Removal from scope soft-deletes target external member
   - Soft-deleted users remain for 30 days
   - Must restore before permanent deletion

2. Document XTS scoping filter and removal method

3. Design descoping and restoration sequence:

   **For Hybrid Target:**
   1. Stamp immutable ID on target external member
   2. Remove source user from XTS scope
   3. XTS soft-deletes target external member
   4. Restore external member from deleted items
   5. Move target AD account into Entra Connect scope
   6. Hard match occurs

   **For Cloud-Only Target:**
   1. Complete user migration
   2. Remove source user from XTS scope
   3. Restore internal member from deleted items
   4. Initiate IGA onboarding

4. Create restoration script:
   ```powershell
   Get-MgDirectoryDeletedItem -DirectoryObjectId [object-id]
   Restore-MgDirectoryDeletedItem -DirectoryObjectId [object-id]
   ```

**Validation Tests**: [TE-04 Tests](tests.md#te-04-xts-descoping)

---

### TE-05: Configure Target License Assignment Strategy

**Description**

Review and modify target organization's M365 license assignment to ensure licenses are not assigned until CTIM has stamped ExchangeGUID.

**Conditions**: None

**Effort**: Medium

**Dependencies**

- MI-03: Cross-Tenant Identity Mapping
- Understanding of existing license assignment processes

**Implementation Guidance**

1. Document current license assignment approach (group-based, direct, IGA-driven)

2. Identify risk points where Exchange licenses could be assigned prematurely

3. Design license staging approach:
   - Create migration-specific license groups without Exchange components
   - Or exclude migration users from automatic assignment

4. Create staging license group:
   ```powershell
   New-MgGroup -DisplayName "Migration-PreCTIM-Licenses" -MailEnabled:$false -SecurityEnabled:$true -MailNickname "migration-prectim"
   # Assign licenses WITHOUT Exchange service plans to this group
   ```

5. Document workflow: Pre-migration in staging group → After CTIM verification → Move to full license group

**Validation Tests**: [TE-05 Tests](tests.md#te-05-license-strategy)

---

### TE-06: Block Target OneDrive Provisioning for Staged Users

**Description**

Configure target tenant to prevent automatic OneDrive provisioning for licensed users staged prior to migration.

**Conditions**: None

**Effort**: Low

**Dependencies**

- SharePoint Administrator access in target tenant

**Implementation Guidance**

1. Create security group for migration staging users

2. Configure User Profile permissions:
   - SharePoint Admin Center > More Features > User Profiles
   - Manage User Permissions
   - Add staging group and remove "Create Personal Site" permission

3. Document process for enabling OneDrive after migration (remove from staging group)

**Validation Tests**: [TE-06 Tests](tests.md#te-06-onedrive-blocking)

---

## Category: Source Environment Preparation

### SE-01: Configure Source B2B Enablement Preparation

**Description**

Create license groups in source tenant with EXO and EXO add-on service plans disabled to support post-migration B2B enablement without triggering proxy scrubbing.

**Conditions**: None

**Effort**: Medium

**Dependencies**

- Understanding of current source license configuration
- Entra ID group management access

**Implementation Guidance**

1. Document service plans that trigger proxy scrubbing:
   - Exchange Online (EXCHANGE_S_ENTERPRISE, etc.)
   - Customer Lockbox
   - Information Barriers
   - Microsoft Defender for Office 365
   - Microsoft Information Governance
   - Office 365 Advanced eDiscovery

2. Get service plan GUIDs:
   ```powershell
   Get-MgSubscribedSku | ForEach-Object {
       $_.ServicePlans | Where-Object { $_.ServicePlanName -like "*EXCHANGE*" -or $_.ServicePlanName -like "*EXO*" }
   }
   ```

3. Create B2B license group with EXO plans disabled:
   ```powershell
   New-MgGroup -DisplayName "Source-B2B-NoEXO" -MailEnabled:$false -SecurityEnabled:$true -MailNickname "source-b2b-noexo"
   # Configure group-based license with EXO service plans toggled off
   ```

4. Document license transition: Post-migration → Remove from standard group → Add to B2B group

**Validation Tests**: [SE-01 Tests](tests.md#se-01-b2b-preparation)

---

## Category: Automation Development

### AD-01: Develop Target Account Conversion Scripts

**Description**

Develop PowerShell scripts to convert target external member accounts to internal members at migration cutover.

**Conditions**: None

**Effort**: High

**Dependencies**

- Understanding of Convert-ExternalToInternalMemberUser API/cmdlet
- Access to Entra ID with appropriate permissions
- Test accounts for validation

**Implementation Guidance**

1. Review Microsoft conversion feature capabilities

2. Design script requirements:
   - Accept list of users (CSV, array)
   - Validate user exists and is external member
   - Perform conversion
   - Handle errors gracefully
   - Log all actions
   - Support dry-run mode

3. Create conversion script template (customize for environment):
   ```powershell
   param(
       [Parameter(Mandatory=$true)]
       [string]$UserListPath,
       [switch]$DryRun
   )

   Connect-MgGraph -Scopes "User.ReadWrite.All"
   $users = Import-Csv $UserListPath

   foreach ($user in $users) {
       try {
           $targetUser = Get-MgUser -UserId $user.TargetUPN -Property UserType,Id
           if ($targetUser.UserType -ne "Member") {
               Write-Host "User $($user.TargetUPN) eligible for conversion"
               if (-not $DryRun) {
                   # Perform conversion
                   Write-Host "Converted $($user.TargetUPN)"
               }
           }
       } catch {
           Write-Error "Failed: $($user.TargetUPN): $_"
       }
   }
   ```

4. Reference toolkit sample scripts; customize for environment

**Validation Tests**: [AD-01 Tests](tests.md#ad-01-target-conversion-scripts)

---

### AD-02: Develop Source Account Conversion Scripts

**Description**

Develop PowerShell scripts to enable B2B collaboration on source internal member accounts after migration cutover.

**Conditions**: None

**Effort**: High

**Dependencies**

- SE-01: Source B2B Enablement Preparation
- Understanding of Invite-InternalUserToB2B API/cmdlet
- Test accounts for validation

**Implementation Guidance**

1. Review Microsoft B2B enablement feature capabilities

2. Design script requirements:
   - Accept list of users
   - Specify target tenant identity for linking
   - Perform B2B enablement
   - Handle email address preservation
   - Move user to B2B license group
   - Support dry-run mode

3. Create B2B enablement script template (customize for environment):
   ```powershell
   param(
       [Parameter(Mandatory=$true)]
       [string]$UserListPath,
       [Parameter(Mandatory=$true)]
       [string]$B2BLicenseGroupId,
       [switch]$DryRun
   )

   Connect-MgGraph -Scopes "User.ReadWrite.All","GroupMember.ReadWrite.All"
   $users = Import-Csv $UserListPath

   foreach ($user in $users) {
       try {
           $sourceUser = Get-MgUser -UserId $user.SourceUPN -Property Id,UserType
           if ($sourceUser.UserType -eq "Member") {
               Write-Host "User $($user.SourceUPN) eligible for B2B enablement"
               if (-not $DryRun) {
                   # Perform B2B enablement
                   # Move to B2B license group
                   Write-Host "Enabled B2B for $($user.SourceUPN)"
               }
           }
       } catch {
           Write-Error "Failed: $($user.SourceUPN): $_"
       }
   }
   ```

4. Handle email address complexities per environment

**Validation Tests**: [AD-02 Tests](tests.md#ad-02-source-conversion-scripts)

---

### AD-03: Develop Identity Rollback Scripts

**Description**

Develop PowerShell scripts to revert identity conversions for rollback scenarios.

**Conditions**: None

**Effort**: High

**Dependencies**

- AD-01: Target Account Conversion Scripts
- AD-02: Source Account Conversion Scripts

**Implementation Guidance**

1. Understand reversal capabilities:
   - **Target Rollback (Internal → External):** Use "Invite internal users to B2B collaboration"
   - **Source Rollback (External → Internal):** Use "Convert external users to internal users"

2. Create target identity rollback script (internal → external, linked to source)

3. Create source identity rollback script (external → internal)

4. Document email address handling complications and workarounds

5. Document rollback sequence:
   1. Revert target identity
   2. Execute reverse mailbox migration
   3. Execute reverse OneDrive migration
   4. Revert source identity

**Validation Tests**: [AD-03 Tests](tests.md#ad-03-identity-rollback-scripts)

---

## Category: Runbook Development

### RD-01: Develop Production Migration Runbook

**Description**

Document end-to-end migration procedures including all steps, timing, dependencies, and verification checkpoints.

**Conditions**: None

**Effort**: High

**Dependencies**

- All infrastructure backlog items complete
- All automation scripts developed and tested
- End-to-end validation testing complete

**Implementation Guidance**

1. Define runbook structure:
   - Pre-migration preparation (T-7 to T-1)
   - Day-of preparation
   - Migration execution (cutover window)
   - Post-migration validation
   - Post-migration cleanup

2. Document each phase with specific steps, timing, and verification checkpoints

3. Document escalation procedures for failures

4. Create verification checklists

5. Integrate with toolkit runbook if applicable

**Validation Tests**: [RD-01 Tests](tests.md#rd-01-production-runbook)

---

### RD-02: Develop Rollback Runbook

**Description**

Document rollback and reverse migration procedures including triggers, decision criteria, and step-by-step recovery.

**Conditions**: None

**Effort**: Medium

**Dependencies**

- RI-01: Reverse Mailbox Migration Infrastructure
- RI-02: Reverse OneDrive Migration Infrastructure
- AD-03: Identity Rollback Scripts
- RD-01: Production Migration Runbook

**Implementation Guidance**

1. Define rollback triggers and decision criteria

2. Document rollback limitations:
   - Teams chat/meetings cannot be reversed
   - Email address handling may require intervention

3. Document partial failure handling

4. Document full rollback procedure:
   - Phase 1: Stop forward migration
   - Phase 2: Revert target identities
   - Phase 3: Reverse data migration
   - Phase 4: Revert source identities
   - Phase 5: Validation

5. Document escalation path for rollback failures

**Validation Tests**: [RD-02 Tests](tests.md#rd-02-rollback-runbook)

---

## Category: End-to-End Validation Testing

### E2E-01: End-to-End Validation - Cloud-Only Target without Orchestrator

**Description**

Execute complete end-to-end migration for test users in cloud-only target topology using standalone migration tools.

**Conditions**: Required for cloud-only target topology not using orchestrator

**Effort**: High

**Dependencies**

- TA-01, MI-01, MI-02, MI-03, TE-03, TE-04, TE-05, TE-06, SE-01, AD-01, AD-02, RD-01

**Implementation Guidance**

1. Select 2-3 test users with varied configurations

2. Execute pre-migration steps per runbook

3. Execute migration cutover per runbook

4. Execute comprehensive post-migration validation

5. Execute post-migration cleanup

6. Document issues and resolutions

**Validation Tests**: [E2E-01 Tests](tests.md#e2e-01-cloud-only-without-orchestrator)

---

### E2E-02: End-to-End Validation - Cloud-Only Target with Orchestrator

**Description**

Execute complete end-to-end migration for test users in cloud-only target topology using Migration Orchestrator.

**Conditions**: Required for cloud-only target topology using orchestrator

**Effort**: High

**Dependencies**

- All E2E-01 dependencies plus MI-04

**Implementation Guidance**

1. Select test users including those with Teams chat history

2. Execute pre-migration steps

3. Run orchestrator standalone validation

4. Submit orchestrator migration batch

5. Execute post-migration steps

6. Validate all workloads including Teams chat and meetings

**Validation Tests**: [E2E-02 Tests](tests.md#e2e-02-cloud-only-with-orchestrator)

---

### E2E-03: End-to-End Validation - Hybrid Target without Orchestrator

**Description**

Execute complete end-to-end migration for test users in hybrid target topology using standalone migration tools.

**Conditions**: Required for hybrid target topology not using orchestrator

**Effort**: Very High

**Dependencies**

- All E2E-01 dependencies plus TE-01, TE-02

**Implementation Guidance**

1. Select test users with corresponding AD accounts

2. Execute pre-migration steps including immutable ID stamping

3. Execute Entra Connect hard match sequence

4. Execute migration cutover

5. Validate hybrid identity attributes syncing correctly

**Validation Tests**: [E2E-03 Tests](tests.md#e2e-03-hybrid-without-orchestrator)

---

### E2E-04: End-to-End Validation - Hybrid Target with Orchestrator

**Description**

Execute complete end-to-end migration for test users in hybrid target topology using Migration Orchestrator.

**Conditions**: Required for hybrid target topology using orchestrator

**Effort**: Very High

**Dependencies**

- All E2E-03 dependencies plus MI-04

**Implementation Guidance**

1. Combine procedures from E2E-02 (orchestrator) and E2E-03 (hybrid)

2. Pay attention to sequencing: hard match timing relative to orchestrator pre-staging

3. Execute comprehensive validation

**Validation Tests**: [E2E-04 Tests](tests.md#e2e-04-hybrid-with-orchestrator)

---

## Sources

### Microsoft Documentation

- [Cross-Tenant Mailbox Migration](https://learn.microsoft.com/en-us/microsoft-365/enterprise/cross-tenant-mailbox-migration)
- [Cross-Tenant OneDrive Migration](https://learn.microsoft.com/en-us/microsoft-365/enterprise/cross-tenant-onedrive-migration)
- [Cross-Tenant Identity Mapping](https://learn.microsoft.com/en-us/microsoft-365/enterprise/cross-tenant-identity-mapping)
- [Migration Orchestrator Overview](https://learn.microsoft.com/en-us/microsoft-365/enterprise/migration-orchestrator-1-overview)
- [Convert External Users to Internal Users](https://learn.microsoft.com/en-us/entra/identity/users/convert-external-users-internal)
- [Invite Internal Users to B2B Collaboration](https://learn.microsoft.com/en-us/entra/external-id/invite-internal-users)
- [Microsoft Entra Connect Sync: Configure Filtering](https://learn.microsoft.com/en-us/entra/identity/hybrid/connect/how-to-connect-sync-configure-filtering)

### Related Documents

- [Overview](index.md)
- [Test Cases](tests.md)
