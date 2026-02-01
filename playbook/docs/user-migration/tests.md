# Cross-Tenant User Migration Test Cases

> **Created with AI. Pending verification by a human. Use with caution.**

This document contains validation test cases for cross-tenant user migration implementation. Tests are organized by backlog item and should be executed during validation testing with test accounts before production pilot migration.

## Test Categories

- [Test Account Validation](#test-account-validation)
- [MI-01: Mailbox Migration Infrastructure](#mi-01-mailbox-migration-infrastructure)
- [MI-02: OneDrive Migration Infrastructure](#mi-02-onedrive-migration-infrastructure)
- [MI-03: CTIM Configuration](#mi-03-ctim-configuration)
- [MI-04: Migration Orchestrator](#mi-04-migration-orchestrator)
- [PP-01: Private Preview Hold Migration](#pp-01-private-preview-hold-migration)
- [RI-01: Reverse Mailbox Migration](#ri-01-reverse-mailbox-migration)
- [RI-02: Reverse OneDrive Migration](#ri-02-reverse-onedrive-migration)
- [TE-01: Entra Connect Hard Match](#te-01-entra-connect-hard-match)
- [TE-02: IGA/JML Hybrid](#te-02-iga-jml-hybrid)
- [TE-03: IGA/JML Cloud](#te-03-iga-jml-cloud)
- [TE-04: XTS Descoping](#te-04-xts-descoping)
- [TE-05: License Strategy](#te-05-license-strategy)
- [TE-06: OneDrive Blocking](#te-06-onedrive-blocking)
- [SE-01: B2B Preparation](#se-01-b2b-preparation)
- [AD-01: Target Conversion Scripts](#ad-01-target-conversion-scripts)
- [AD-02: Source Conversion Scripts](#ad-02-source-conversion-scripts)
- [AD-03: Identity Rollback Scripts](#ad-03-identity-rollback-scripts)
- [RD-01: Production Runbook](#rd-01-production-runbook)
- [RD-02: Rollback Runbook](#rd-02-rollback-runbook)
- [E2E-01: Cloud-Only without Orchestrator](#e2e-01-cloud-only-without-orchestrator)
- [E2E-02: Cloud-Only with Orchestrator](#e2e-02-cloud-only-with-orchestrator)
- [E2E-03: Hybrid without Orchestrator](#e2e-03-hybrid-without-orchestrator)
- [E2E-04: Hybrid with Orchestrator](#e2e-04-hybrid-with-orchestrator)

---

## Test Account Validation

**Backlog Item:** [TA-01](backlog.md#ta-01-provision-test-accounts-for-validation-testing)

### TA-01-T01: Verify Source Test Accounts

**Objective:** Confirm test accounts exist in source tenant with provisioned mailboxes.

**Steps:**

1. Run the following command in source tenant:
   ```powershell
   Connect-ExchangeOnline -UserPrincipalName admin@source.onmicrosoft.com
   Get-EXOMailbox -Identity "testuser@source.com" | Select-Object DisplayName, PrimarySmtpAddress, ExchangeGuid, ArchiveStatus
   ```

2. Verify each test account has a mailbox with content

**Expected Results:**

- All test accounts appear in Exchange Online
- ExchangeGuid is populated
- Mailboxes contain test data

---

### TA-01-T02: Verify Source OneDrive Sites

**Objective:** Confirm test accounts have OneDrive sites provisioned with test files.

**Steps:**

1. Access OneDrive admin center or use PowerShell:
   ```powershell
   Connect-SPOService -Url https://[source-tenant]-admin.sharepoint.com
   Get-SPOSite -Filter "Url -like '-my.sharepoint.com/personal/'" | Where-Object { $_.Owner -like "*testuser*" }
   ```

2. Verify each test account has OneDrive with files

**Expected Results:**

- OneDrive sites exist for all test accounts
- Sites contain test files including shared files

---

### TA-01-T03: Verify Target External Members (Cloud-Only)

**Objective:** Confirm XTS has provisioned external member accounts in target tenant.

**Steps:**

1. Run the following in target tenant:
   ```powershell
   Connect-MgGraph -Scopes "User.Read.All"
   Get-MgUser -UserId "testuser@target.com" -Property UserType, ExternalUserState, Id
   ```

2. Verify user type and external state

**Expected Results:**

- UserType is "Member" (external member, not guest)
- ExternalUserState is "Accepted"
- Object ID is documented for later verification

---

### TA-01-T04: Verify Target AD Accounts (Hybrid)

**Objective:** Confirm target AD accounts exist in staging OU and are excluded from sync.

**Steps:**

1. In target AD, verify accounts exist in staging OU
2. Derive immutable ID:
   ```powershell
   $user = Get-ADUser -Identity "testuser" -Properties objectGUID, msDS-ConsistencyGuid
   [System.Convert]::ToBase64String($user.objectGUID.ToByteArray())
   ```
3. Verify account does NOT appear in Entra ID (excluded from sync)

**Expected Results:**

- AD accounts exist in staging OU
- Immutable ID is documented
- Accounts are not synced to Entra ID

---

### TA-01-T05: Document Test Account Inventory

**Objective:** Complete test account inventory documentation.

**Steps:**

1. Create inventory table with:
   - Source UPN
   - Target UPN
   - Source mailbox ExchangeGuid
   - Immutable ID (hybrid only)
   - Configuration type (standard, archive, hold, etc.)
   - Assigned test scenarios

**Expected Results:**

- Complete inventory documented
- All required data captured for each account

---

## MI-01: Mailbox Migration Infrastructure

**Backlog Item:** [MI-01](backlog.md#mi-01-configure-cross-tenant-mailbox-migration-infrastructure)

### MI-01-T01: Verify Migration Application

**Objective:** Confirm migration application exists with correct permissions.

**Steps:**

1. In target tenant Azure AD, navigate to App Registrations
2. Locate migration application
3. Verify API permissions include Mailbox.Migration

**Expected Results:**

- Application exists in App Registrations
- Mailbox.Migration permission is granted
- Admin consent is granted

---

### MI-01-T02: Verify Source Tenant Consent

**Objective:** Confirm migration application consent granted in source tenant.

**Steps:**

1. In source tenant, navigate to Enterprise Applications
2. Locate migration application by client ID
3. Verify consented permissions

**Expected Results:**

- Application appears in Enterprise Applications
- Permissions show as consented

---

### MI-01-T03: Verify Organization Relationship (Target)

**Objective:** Confirm target organization relationship configured for inbound migration.

**Steps:**

```powershell
Connect-ExchangeOnline -UserPrincipalName admin@target.onmicrosoft.com
Get-OrganizationRelationship | Where-Object { $_.DomainNames -like "*source*" } | Select-Object Name, MailboxMoveEnabled, MailboxMoveCapability
```

**Expected Results:**

- MailboxMoveEnabled is True
- MailboxMoveCapability includes "Inbound"

---

### MI-01-T04: Verify Organization Relationship (Source)

**Objective:** Confirm source organization relationship configured for outbound migration.

**Steps:**

```powershell
Connect-ExchangeOnline -UserPrincipalName admin@source.onmicrosoft.com
Get-OrganizationRelationship | Where-Object { $_.DomainNames -like "*target*" } | Select-Object Name, MailboxMoveEnabled, MailboxMoveCapability, MailboxMovePublishedScopes
```

**Expected Results:**

- MailboxMoveEnabled is True
- MailboxMoveCapability includes "RemoteOutbound"
- MailboxMovePublishedScopes contains security group

---

### MI-01-T05: Verify Migration Endpoint

**Objective:** Confirm migration endpoint connectivity.

**Steps:**

```powershell
Connect-ExchangeOnline -UserPrincipalName admin@target.onmicrosoft.com
Test-MigrationServerAvailability -Endpoint "CrossTenantEndpoint"
```

**Expected Results:**

- Test returns success
- No connectivity errors

---

### MI-01-T06: Execute Test Mailbox Migration

**Objective:** Verify mailbox migration completes successfully for test user.

**Steps:**

1. Ensure test user is in source migration scope group
2. Create migration batch:
   ```powershell
   New-MigrationBatch -Name "TestMigration" -SourceEndpoint "CrossTenantEndpoint" -CSVData ([System.IO.File]::ReadAllBytes("users.csv")) -TargetDeliveryDomain "target.onmicrosoft.com"
   Start-MigrationBatch -Identity "TestMigration"
   ```
3. Monitor progress:
   ```powershell
   Get-MigrationUser -Identity "testuser@source.com" | Select-Object Status, StatusSummary
   ```
4. After completion, verify target mailbox:
   ```powershell
   Get-EXOMailbox -Identity "testuser@target.com"
   ```
5. Verify source MailUser:
   ```powershell
   Get-EXOMailUser -Identity "testuser@source.com" | Select-Object ExternalEmailAddress
   ```

**Expected Results:**

- Migration status shows Completed
- Target has full mailbox with migrated content
- Source has MailUser with targetAddress pointing to target

---

## MI-02: OneDrive Migration Infrastructure

**Backlog Item:** [MI-02](backlog.md#mi-02-configure-cross-tenant-onedrive-migration-infrastructure)

### MI-02-T01: Verify Cross-Tenant Trust (Target)

**Objective:** Confirm target tenant trust relationship to source.

**Steps:**

```powershell
Connect-SPOService -Url https://[target-tenant]-admin.sharepoint.com
Get-SPOCrossTenantRelationship -Scenario MnA
```

**Expected Results:**

- Relationship shows Source partner role
- Partner host URL points to source tenant

---

### MI-02-T02: Verify Cross-Tenant Trust (Source)

**Objective:** Confirm source tenant trust relationship to target.

**Steps:**

```powershell
Connect-SPOService -Url https://[source-tenant]-admin.sharepoint.com
Get-SPOCrossTenantRelationship -Scenario MnA
```

**Expected Results:**

- Relationship shows Target partner role
- Partner host URL points to target tenant

---

### MI-02-T03: Execute Test OneDrive Migration

**Objective:** Verify OneDrive migration completes successfully.

**Steps:**

1. Start OneDrive migration:
   ```powershell
   Start-SPOCrossTenantUserContentMove -SourceUserPrincipalName "testuser@source.com" -TargetUserPrincipalName "testuser@target.com" -TargetCrossTenantHostUrl "https://[target-tenant]-my.sharepoint.com"
   ```
2. Monitor progress:
   ```powershell
   Get-SPOCrossTenantUserContentMoveState -PartnerCrossTenantHostUrl "https://[target-tenant]-my.sharepoint.com"
   ```
3. After completion, verify content in target OneDrive
4. Navigate to old source OneDrive URL and verify redirect

**Expected Results:**

- All OneDrive content appears in target
- Old OneDrive URL redirects to target location
- Permissions are preserved

---

## MI-03: CTIM Configuration

**Backlog Item:** [MI-03](backlog.md#mi-03-configure-cross-tenant-identity-mapping-ctim)

### MI-03-T01: Verify CTIM Permissions (Source)

**Objective:** Confirm CTIM application has required permissions in source tenant.

**Steps:**

1. Verify CTIM service principal has Exchange Administrator role
2. Verify Exchange.ManageAsApp API permission is granted

**Expected Results:**

- CTIM service principal has Exchange Administrator role
- API permissions are consented

---

### MI-03-T02: Verify CTIM Permissions (Target)

**Objective:** Confirm CTIM application has required permissions in target tenant.

**Steps:**

Same as MI-03-T01 but in target tenant

**Expected Results:**

- CTIM service principal has Exchange Administrator role
- API permissions are consented

---

### MI-03-T03: Verify Target User is MailUser

**Objective:** Confirm target user is MailUser before CTIM execution.

**Steps:**

```powershell
Connect-ExchangeOnline -UserPrincipalName admin@target.onmicrosoft.com
Get-EXORecipient -Identity "testuser@target.com" | Select-Object RecipientType, RecipientTypeDetails
```

**Expected Results:**

- RecipientType is MailUser
- RecipientTypeDetails is MailUser

---

### MI-03-T04: Execute CTIM and Verify Attributes

**Objective:** Verify CTIM stamps Exchange attributes correctly.

**Steps:**

1. Run CTIM for test user
2. Verify target MailUser attributes:
   ```powershell
   Get-EXOMailUser -Identity "testuser@target.com" | Select-Object ExchangeGuid, ArchiveGuid, LegacyExchangeDN, EmailAddresses
   ```
3. Compare with source mailbox:
   ```powershell
   Get-EXOMailbox -Identity "testuser@source.com" | Select-Object ExchangeGuid, ArchiveGuid, LegacyExchangeDN, EmailAddresses
   ```

**Expected Results:**

- ExchangeGuid matches between source and target
- ArchiveGuid matches (if archive-enabled)
- LegacyExchangeDN from source appears as X500 proxy address on target
- All source X500 addresses present on target

**Troubleshooting:**

- If ExchangeGuid is empty: User may have been licensed before CTIM ran
- If attributes don't match: Re-run identity mapping

---

## MI-04: Migration Orchestrator

**Backlog Item:** [MI-04](backlog.md#mi-04-configure-migration-orchestrator)

### MI-04-T01: Verify Orchestrator Module

**Objective:** Confirm orchestrator module installed and connected.

**Steps:**

```powershell
Import-Module MigrationOrchestrator
Connect-MgGraph -Scopes "User.Read.All","CrossTenantUserDataMigration.ReadWrite.All"
```

**Expected Results:**

- Module imports without error
- Graph connection succeeds

---

### MI-04-T02: Verify Teams Federation

**Objective:** Confirm Teams federation enabled for cross-tenant collaboration.

**Steps:**

```powershell
Connect-MicrosoftTeams
Get-CsTenantFederationConfiguration | Select-Object AllowFederatedUsers, AllowedDomains
```

**Expected Results:**

- AllowFederatedUsers is True
- Federation policy allows partner tenant

---

### MI-04-T03: Run Standalone Validation

**Objective:** Verify orchestrator prerequisites pass for test user.

**Steps:**

Run orchestrator standalone validation command for test user

**Expected Results:**

- All tenant-level prerequisites pass
- All user-level prerequisites pass

---

### MI-04-T04: Submit Test Migration Batch

**Objective:** Verify orchestrator can submit and process migration batch.

**Steps:**

1. Submit migration batch via orchestrator
2. Monitor progress through stages
3. Verify completion of all workloads

**Expected Results:**

- Batch submits successfully
- Batch progresses through stages
- Mailbox, OneDrive, Teams chat, meetings all complete

---

### MI-04-T05: Verify Teams Chat Migration

**Objective:** Confirm Teams chat history migrated.

**Steps:**

1. Sign in to target tenant Teams as migrated user
2. Navigate to Chat
3. Verify 1:1 and group chat history present

**Expected Results:**

- Chat history visible in target
- Chat participants correctly mapped

---

### MI-04-T06: Verify Teams Meetings Migration

**Objective:** Confirm Teams meetings rescheduled in target.

**Steps:**

1. Check calendar in target tenant
2. Verify meetings organized by user

**Expected Results:**

- Source meetings canceled
- Equivalent meetings created in target
- Meeting details preserved

---

## PP-01: Private Preview Hold Migration

**Backlog Item:** [PP-01](backlog.md#pp-01-enable-private-preview-for-on-hold-mailbox-migration)

### PP-01-T01: Verify Preview Enablement

**Objective:** Confirm private preview enabled on both tenants.

**Steps:**

Verify Microsoft has confirmed enablement via email

**Expected Results:**

- Confirmation received for source tenant
- Confirmation received for target tenant

---

### PP-01-T02: Verify Test Mailbox On Hold

**Objective:** Confirm test mailbox has litigation hold enabled.

**Steps:**

```powershell
Connect-ExchangeOnline -UserPrincipalName admin@source.onmicrosoft.com
Get-EXOMailbox -Identity "testuser-hold@source.com" | Select-Object LitigationHoldEnabled, InPlaceHolds
```

**Expected Results:**

- LitigationHoldEnabled is True or InPlaceHolds contains values

---

### PP-01-T03: Execute Held Mailbox Migration

**Objective:** Verify held mailbox migrates successfully.

**Steps:**

1. Submit migration for held mailbox
2. Monitor for completion (should not fail due to hold)
3. Verify completion status

**Expected Results:**

- Migration does not fail due to hold status
- Migration completes successfully

---

### PP-01-T04: Verify Substrate Folders Remain

**Objective:** Confirm substrate folders remain in source for eDiscovery.

**Steps:**

1. After migration, verify source MailUser exists
2. Use eDiscovery to search substrate content by email address

**Expected Results:**

- Source MailUser exists
- ComponentShared, SubstrateHolds folders present
- eDiscovery can search substrate content

---

### PP-01-T05: Verify Active Content Migrated

**Objective:** Confirm active mailbox content migrated to target.

**Steps:**

1. Sign in to target mailbox
2. Verify mail, calendar, contacts accessible
3. Verify recoverable items present

**Expected Results:**

- All active content accessible in target
- Recoverable items migrated

---

## RI-01: Reverse Mailbox Migration

**Backlog Item:** [RI-01](backlog.md#ri-01-configure-reverse-mailbox-migration-infrastructure)

### RI-01-T01: Verify Bidirectional Organization Relationship

**Objective:** Confirm organization relationships support both directions.

**Steps:**

```powershell
# Source tenant
Get-OrganizationRelationship | Select-Object Name, MailboxMoveCapability

# Target tenant
Get-OrganizationRelationship | Select-Object Name, MailboxMoveCapability
```

**Expected Results:**

- Both tenants show Inbound and RemoteOutbound capabilities

---

### RI-01-T02: Verify Reverse Migration Endpoint

**Objective:** Confirm reverse migration endpoint connectivity.

**Steps:**

```powershell
Connect-ExchangeOnline -UserPrincipalName admin@source.onmicrosoft.com
Test-MigrationServerAvailability -Endpoint "ReverseCrossTenantEndpoint"
```

**Expected Results:**

- Test returns success

---

### RI-01-T03: Execute Test Reverse Mailbox Migration

**Objective:** Verify mailbox can be migrated from target back to source.

**Steps:**

1. Migrate test mailbox from target to source
2. Monitor completion
3. Verify mailbox accessible in source

**Expected Results:**

- Migration completes successfully
- Mailbox accessible in source

---

## RI-02: Reverse OneDrive Migration

**Backlog Item:** [RI-02](backlog.md#ri-02-configure-reverse-onedrive-migration-infrastructure)

### RI-02-T01: Verify Bidirectional Trust

**Objective:** Confirm OneDrive trust supports both directions.

**Steps:**

```powershell
# Both tenants should show both Source and Target relationships
Get-SPOCrossTenantRelationship -Scenario MnA
```

**Expected Results:**

- Both tenants show Source and Target partner roles

---

### RI-02-T02: Execute Test Reverse OneDrive Migration

**Objective:** Verify OneDrive can be migrated from target back to source.

**Steps:**

1. Migrate test OneDrive from target to source
2. Monitor completion
3. Verify content accessible in source
4. Verify redirect in target

**Expected Results:**

- Migration completes successfully
- Content accessible in source
- Target URL redirects to source

---

## TE-01: Entra Connect Hard Match

**Backlog Item:** [TE-01](backlog.md#te-01-configure-target-entra-connect-for-hard-match)

### TE-01-T01: Verify Staging Exclusion

**Objective:** Confirm accounts in staging OU/with staging attribute do not sync.

**Steps:**

1. Create test AD account in staging location
2. Run Entra Connect sync cycle
3. Verify account does NOT appear in Entra ID

**Expected Results:**

- Account not synced to Entra ID

---

### TE-01-T02: Execute Hard Match

**Objective:** Verify hard match process works correctly.

**Steps:**

1. Stamp immutable ID on cloud external member:
   ```powershell
   $immutableId = "[base64-encoded-id]"
   Update-MgUser -UserId "[cloud-user-object-id]" -OnPremisesImmutableId $immutableId
   ```
2. Move AD account to production OU (or clear staging attribute)
3. Run Entra Connect sync cycle
4. Verify hard match occurred

**Expected Results:**

- Cloud account linked with AD account
- Object ID preserved (same as before hard match)
- No duplicate objects created

---

### TE-01-T03: Verify Attribute Flow

**Objective:** Confirm attributes flow correctly after hard match.

**Steps:**

```powershell
Get-MgUser -UserId "testuser@target.com" -Property UserPrincipalName, Mail, ProxyAddresses, OnPremisesSyncEnabled
```

**Expected Results:**

- OnPremisesSyncEnabled is True
- UPN, mail, proxyAddresses match AD values

---

## TE-02: IGA/JML Hybrid

**Backlog Item:** [TE-02](backlog.md#te-02-configure-target-igajml-integration-for-hybrid-topology)

### TE-02-T01: Verify Modified Provisioning

**Objective:** Confirm IGA provisions migration users without remote mailbox.

**Steps:**

1. Provision test user via modified IGA workflow
2. Verify AD account created
3. Verify no remote mailbox provisioned

**Expected Results:**

- AD account created without remote mailbox

---

### TE-02-T02: Verify MailUser State

**Objective:** Confirm provisioned user is MailUser after sync.

**Steps:**

```powershell
Get-EXORecipient -Identity "testuser@target.com" | Select-Object RecipientType, RecipientTypeDetails
```

**Expected Results:**

- RecipientType is MailUser
- Not UserMailbox

---

### TE-02-T03: Verify CTIM Compatibility

**Objective:** Confirm CTIM can stamp attributes on IGA-provisioned user.

**Steps:**

Run CTIM for IGA-provisioned user

**Expected Results:**

- CTIM execution succeeds
- Exchange attributes stamped

---

### TE-02-T04: Verify Post-Migration Handoff

**Objective:** Confirm IGA can manage user after migration.

**Steps:**

1. Complete migration for test user
2. Verify IGA can update user attributes
3. Verify IGA lifecycle operations work

**Expected Results:**

- IGA can manage migrated user

---

## TE-03: IGA/JML Cloud

**Backlog Item:** [TE-03](backlog.md#te-03-configure-target-igajml-integration-for-cloud-only-topology)

### TE-03-T01: Verify IGA Discovery

**Objective:** Confirm IGA can discover converted internal member.

**Steps:**

1. Convert test external member to internal
2. Verify IGA identifies user for onboarding

**Expected Results:**

- IGA discovers migrated user

---

### TE-03-T02: Verify IGA Management

**Objective:** Confirm IGA can manage user without conflicts.

**Steps:**

1. IGA updates user attributes
2. Verify updates apply successfully
3. Verify no conflicts with other systems

**Expected Results:**

- IGA can manage user
- No attribute conflicts

---

## TE-04: XTS Descoping

**Backlog Item:** [TE-04](backlog.md#te-04-configure-cross-tenant-sync-descoping-and-account-restoration)

### TE-04-T01: Verify Soft-Delete on Descope

**Objective:** Confirm XTS soft-deletes target user when removed from scope.

**Steps:**

1. Remove test user from XTS scope
2. Wait for sync cycle
3. Check Entra ID deleted items:
   ```powershell
   Get-MgDirectoryDeletedItem -DirectoryObjectId "[user-object-id]"
   ```

**Expected Results:**

- User appears in deleted items

---

### TE-04-T02: Verify Restoration

**Objective:** Confirm soft-deleted user can be restored.

**Steps:**

```powershell
Restore-MgDirectoryDeletedItem -DirectoryObjectId "[user-object-id]"
Get-MgUser -UserId "[user-object-id]"
```

**Expected Results:**

- User restored successfully
- Same object ID preserved
- Attributes intact

---

### TE-04-T03: Verify Hard Match After Restoration (Hybrid)

**Objective:** Confirm Entra Connect hard match works after restoration.

**Steps:**

1. After restoration, move AD account into sync scope
2. Run Entra Connect sync
3. Verify hard match

**Expected Results:**

- Hard match succeeds with restored user

---

## TE-05: License Strategy

**Backlog Item:** [TE-05](backlog.md#te-05-configure-target-license-assignment-strategy)

### TE-05-T01: Verify Staging Group Excludes Exchange

**Objective:** Confirm staging license group does not include Exchange plans.

**Steps:**

1. Review group license assignment in Entra ID
2. Verify Exchange service plans are toggled off

**Expected Results:**

- No Exchange service plans assigned via staging group

---

### TE-05-T02: Verify No Mailbox Provisioned

**Objective:** Confirm user in staging group remains MailUser.

**Steps:**

1. Add test user to staging license group
2. Wait for license assignment
3. Check recipient type:
   ```powershell
   Get-EXORecipient -Identity "testuser@target.com" | Select-Object RecipientType
   ```

**Expected Results:**

- RecipientType remains MailUser
- No mailbox provisioned

---

### TE-05-T03: Verify CTIM After Staging License

**Objective:** Confirm CTIM works for user with staging license.

**Steps:**

1. Run CTIM for user with staging license
2. Verify ExchangeGuid stamped

**Expected Results:**

- CTIM succeeds
- ExchangeGuid present on MailUser

---

### TE-05-T04: Verify Full License After Migration

**Objective:** Confirm migration works and user gets full license.

**Steps:**

1. Complete migration for test user
2. Move user to full license group
3. Verify mailbox accessible

**Expected Results:**

- Migrated mailbox accessible
- Full license assigned

---

## TE-06: OneDrive Blocking

**Backlog Item:** [TE-06](backlog.md#te-06-block-target-onedrive-provisioning-for-staged-users)

### TE-06-T01: Verify Staging Group Configured

**Objective:** Confirm staging group has OneDrive creation blocked.

**Steps:**

1. Verify staging group exists
2. Verify "Create Personal Site" permission removed

**Expected Results:**

- Group configured with OneDrive blocking

---

### TE-06-T02: Verify OneDrive Blocked

**Objective:** Confirm user cannot create OneDrive.

**Steps:**

1. Add test user to staging group
2. Attempt to access OneDrive as test user

**Expected Results:**

- User cannot create OneDrive
- Appropriate error displayed

---

### TE-06-T03: Verify OneDrive After Migration

**Objective:** Confirm OneDrive accessible after migration and staging removal.

**Steps:**

1. Complete OneDrive migration for test user
2. Remove user from staging group
3. Access OneDrive

**Expected Results:**

- Migrated OneDrive accessible

---

## SE-01: B2B Preparation

**Backlog Item:** [SE-01](backlog.md#se-01-configure-source-b2b-enablement-preparation)

### SE-01-T01: Verify B2B License Group

**Objective:** Confirm B2B license group excludes EXO plans.

**Steps:**

1. Review group license assignment in source Entra ID
2. Verify EXO service plans toggled off

**Expected Results:**

- No EXO service plans in B2B group license

---

### SE-01-T02: Verify Proxy Addresses Preserved

**Objective:** Confirm proxy addresses not scrubbed after B2B license.

**Steps:**

1. After migration, move test MailUser to B2B license group
2. Remove from standard license group
3. Check proxy addresses:
   ```powershell
   Get-EXOMailUser -Identity "testuser@source.com" | Select-Object EmailAddresses
   ```

**Expected Results:**

- All proxy addresses preserved
- Target domain address still present

---

### SE-01-T03: Verify Mail Routing

**Objective:** Confirm mail routing works after B2B license transition.

**Steps:**

1. Send email to source address
2. Verify delivery to target mailbox via targetAddress

**Expected Results:**

- Mail routes correctly to target

---

## AD-01: Target Conversion Scripts

**Backlog Item:** [AD-01](backlog.md#ad-01-develop-target-account-conversion-scripts)

### AD-01-T01: Verify Dry-Run Mode

**Objective:** Confirm script dry-run reports eligible users without changes.

**Steps:**

1. Run conversion script with -DryRun flag
2. Review output

**Expected Results:**

- Script reports users eligible for conversion
- No actual changes made

---

### AD-01-T02: Execute Conversion

**Objective:** Verify script converts external member to internal.

**Steps:**

1. Run conversion script for test user
2. Verify user type:
   ```powershell
   Get-MgUser -UserId "testuser@target.com" -Property UserType
   ```

**Expected Results:**

- UserType changed to "Member" (internal)

---

### AD-01-T03: Verify Object ID Preserved

**Objective:** Confirm object ID unchanged after conversion.

**Steps:**

Compare object ID before and after conversion

**Expected Results:**

- Object ID is identical

---

### AD-01-T04: Verify Group Memberships

**Objective:** Confirm group memberships preserved after conversion.

**Steps:**

```powershell
Get-MgUserMemberOf -UserId "testuser@target.com"
```

**Expected Results:**

- All group memberships preserved

---

### AD-01-T05: Verify Error Handling

**Objective:** Confirm script handles errors gracefully.

**Steps:**

1. Run script with non-existent user
2. Run script with already-converted user

**Expected Results:**

- Errors logged appropriately
- Script continues with next user

---

## AD-02: Source Conversion Scripts

**Backlog Item:** [AD-02](backlog.md#ad-02-develop-source-account-conversion-scripts)

### AD-02-T01: Verify Dry-Run Mode

**Objective:** Confirm script dry-run reports eligible users.

**Steps:**

Run B2B enablement script with -DryRun flag

**Expected Results:**

- Script reports eligible users
- No changes made

---

### AD-02-T02: Execute B2B Enablement

**Objective:** Verify script enables B2B correctly.

**Steps:**

1. Run B2B enablement script for test user
2. Verify user type changed to external member

**Expected Results:**

- User converted to external member
- Linked to target identity

---

### AD-02-T03: Verify License Group Transition

**Objective:** Confirm script moves user to B2B license group.

**Steps:**

Verify group membership after script execution

**Expected Results:**

- User removed from standard group
- User added to B2B group

---

### AD-02-T04: Verify B2B Reach Back

**Objective:** Confirm user can access source with target credentials.

**Steps:**

1. Sign in with target credentials
2. Access source tenant application

**Expected Results:**

- Access works with target credentials

---

### AD-02-T05: Verify Dual Account Fallback

**Objective:** Confirm user can still use source credentials.

**Steps:**

1. Sign in with source credentials
2. Access source tenant application

**Expected Results:**

- Access works with source credentials (fallback)

---

## AD-03: Identity Rollback Scripts

**Backlog Item:** [AD-03](backlog.md#ad-03-develop-identity-rollback-scripts)

### AD-03-T01: Execute Target Rollback

**Objective:** Verify target internal member can revert to external.

**Steps:**

1. Run target rollback script
2. Verify user becomes external member linked to source

**Expected Results:**

- User converted to external member
- Linked to source identity

---

### AD-03-T02: Execute Source Rollback

**Objective:** Verify source external member can revert to internal.

**Steps:**

1. Run source rollback script
2. Verify user becomes internal member

**Expected Results:**

- User converted to internal member

---

### AD-03-T03: Verify Full Rollback Sequence

**Objective:** Confirm complete rollback restores pre-migration state.

**Steps:**

1. Execute full rollback sequence per runbook
2. Verify source mailbox accessible
3. Verify source OneDrive accessible

**Expected Results:**

- User fully restored to pre-migration state
- Services accessible in source

---

## RD-01: Production Runbook

**Backlog Item:** [RD-01](backlog.md#rd-01-develop-production-migration-runbook)

### RD-01-T01: Review Runbook Completeness

**Objective:** Confirm runbook covers all required steps.

**Steps:**

Review runbook with operations team

**Expected Results:**

- All steps documented
- Timing estimates included
- Verification checkpoints defined

---

### RD-01-T02: Execute Runbook for E2E Test

**Objective:** Verify runbook can be executed without ambiguity.

**Steps:**

Execute runbook for end-to-end validation test

**Expected Results:**

- Runbook completes without ambiguity
- All steps executable as documented

---

## RD-02: Rollback Runbook

**Backlog Item:** [RD-02](backlog.md#rd-02-develop-rollback-runbook)

### RD-02-T01: Review Rollback Runbook

**Objective:** Confirm rollback procedures are clear.

**Steps:**

Review runbook with operations team

**Expected Results:**

- Triggers defined
- Decision criteria documented
- Steps clear and executable

---

### RD-02-T02: Execute Rollback for Test User

**Objective:** Verify rollback completes successfully.

**Steps:**

Execute rollback runbook for test user

**Expected Results:**

- Rollback completes successfully
- Timing matches estimates

---

## E2E-01: Cloud-Only without Orchestrator

**Backlog Item:** [E2E-01](backlog.md#e2e-01-end-to-end-validation---cloud-only-target-without-orchestrator)

### E2E-01-T01: Complete E2E Migration

**Objective:** Execute complete migration for cloud-only topology without orchestrator.

**Steps:**

Execute full migration per runbook for 2-3 test users

**Expected Results:**

- All users migrated successfully

---

### E2E-01-T02: Verify Mail Routing

**Objective:** Confirm mail flows correctly in both directions.

**Steps:**

1. Send mail from source to target user
2. Send mail from target to source user
3. Send mail from external to source address

**Expected Results:**

- All mail delivered correctly

---

### E2E-01-T03: Verify Free/Busy

**Objective:** Confirm calendar availability visible.

**Steps:**

1. From source, view target user calendar
2. From target, view source user calendar

**Expected Results:**

- Free/busy visible in both directions

---

### E2E-01-T04: Verify Inbound Attribution

**Objective:** Confirm sender attribution works.

**Steps:**

1. From target, send to source restricted DL
2. Verify delivery

**Expected Results:**

- Mail accepted by restricted DL
- Sender recognized via MailUser

---

### E2E-01-T05: Verify OneDrive Access

**Objective:** Confirm OneDrive content accessible and redirect works.

**Steps:**

1. Access migrated OneDrive in target
2. Navigate to old source URL

**Expected Results:**

- Content accessible in target
- Source URL redirects

---

### E2E-01-T06: Verify Application Access

**Objective:** Confirm SSO to target apps works.

**Steps:**

Sign in to target apps

**Expected Results:**

- SSO works
- No additional prompts

---

### E2E-01-T07: Verify B2B Reach Back

**Objective:** Confirm reach back to source works.

**Steps:**

1. With target credentials, access source apps
2. Verify permissions

**Expected Results:**

- Access works via B2B
- Original permissions preserved

---

### E2E-01-T08: Verify Dual Account Fallback

**Objective:** Confirm source credentials still work.

**Steps:**

Sign in to source with source credentials

**Expected Results:**

- Fallback authentication works

---

## E2E-02: Cloud-Only with Orchestrator

**Backlog Item:** [E2E-02](backlog.md#e2e-02-end-to-end-validation---cloud-only-target-with-orchestrator)

### E2E-02-T01: Complete E2E Orchestrated Migration

**Objective:** Execute complete orchestrated migration.

**Steps:**

Execute orchestrator migration for test users including Teams data

**Expected Results:**

- All workloads migrate successfully

---

### E2E-02-T02: Verify Teams Chat

**Objective:** Confirm Teams chat history migrated.

**Steps:**

Access Teams chat in target

**Expected Results:**

- Chat history visible

---

### E2E-02-T03: Verify Teams Meetings

**Objective:** Confirm meetings rescheduled.

**Steps:**

Check calendar for meetings

**Expected Results:**

- Meetings present in target

---

### E2E-02-T04: All E2E-01 Tests

**Objective:** Complete all cloud-only validation tests.

**Steps:**

Execute tests E2E-01-T02 through E2E-01-T08

**Expected Results:**

- All tests pass

---

## E2E-03: Hybrid without Orchestrator

**Backlog Item:** [E2E-03](backlog.md#e2e-03-end-to-end-validation---hybrid-target-without-orchestrator)

### E2E-03-T01: Execute Hard Match Sequence

**Objective:** Complete hard match for hybrid migration.

**Steps:**

Execute hard match sequence per runbook

**Expected Results:**

- Hard match successful
- Object ID preserved

---

### E2E-03-T02: Complete E2E Hybrid Migration

**Objective:** Execute complete hybrid migration.

**Steps:**

Execute full migration including hybrid identity

**Expected Results:**

- All steps complete
- Hybrid identity syncing

---

### E2E-03-T03: Verify Hybrid Attributes

**Objective:** Confirm hybrid identity attributes correct.

**Steps:**

```powershell
Get-MgUser -UserId "testuser@target.com" -Property OnPremisesSyncEnabled, OnPremisesImmutableId
```

**Expected Results:**

- OnPremisesSyncEnabled is True
- Attributes syncing from AD

---

### E2E-03-T04: All E2E-01 Tests

**Objective:** Complete all baseline validation tests.

**Steps:**

Execute tests E2E-01-T02 through E2E-01-T08

**Expected Results:**

- All tests pass

---

## E2E-04: Hybrid with Orchestrator

**Backlog Item:** [E2E-04](backlog.md#e2e-04-end-to-end-validation---hybrid-target-with-orchestrator)

### E2E-04-T01: Execute Combined Procedure

**Objective:** Complete hybrid orchestrated migration.

**Steps:**

Execute combined hybrid + orchestrator procedure

**Expected Results:**

- All steps complete without conflicts

---

### E2E-04-T02: All E2E-02 and E2E-03 Tests

**Objective:** Complete all validation tests.

**Steps:**

Execute all tests from E2E-02 and E2E-03

**Expected Results:**

- All tests pass

---

## Test Execution Tracking

| Test ID | Description | Backlog ID | Test Account(s) | Status | Date | Tester | Notes |
|---------|-------------|------------|-----------------|--------|------|--------|-------|
| TA-01-T01 | Verify Source Test Accounts | TA-01 | | | | | |
| TA-01-T02 | Verify Source OneDrive Sites | TA-01 | | | | | |
| TA-01-T03 | Verify Target External Members | TA-01 | | | | | |
| TA-01-T04 | Verify Target AD Accounts | TA-01 | | | | | |
| TA-01-T05 | Document Test Account Inventory | TA-01 | | | | | |
| MI-01-T01 | Verify Migration Application | MI-01 | | | | | |
| MI-01-T02 | Verify Source Tenant Consent | MI-01 | | | | | |
| MI-01-T03 | Verify Org Relationship (Target) | MI-01 | | | | | |
| MI-01-T04 | Verify Org Relationship (Source) | MI-01 | | | | | |
| MI-01-T05 | Verify Migration Endpoint | MI-01 | | | | | |
| MI-01-T06 | Execute Test Mailbox Migration | MI-01 | | | | | |
| MI-02-T01 | Verify Cross-Tenant Trust (Target) | MI-02 | | | | | |
| MI-02-T02 | Verify Cross-Tenant Trust (Source) | MI-02 | | | | | |
| MI-02-T03 | Execute Test OneDrive Migration | MI-02 | | | | | |
| MI-03-T01 | Verify CTIM Permissions (Source) | MI-03 | | | | | |
| MI-03-T02 | Verify CTIM Permissions (Target) | MI-03 | | | | | |
| MI-03-T03 | Verify Target User is MailUser | MI-03 | | | | | |
| MI-03-T04 | Execute CTIM and Verify Attributes | MI-03 | | | | | |
| MI-04-T01 | Verify Orchestrator Module | MI-04 | | | | | |
| MI-04-T02 | Verify Teams Federation | MI-04 | | | | | |
| MI-04-T03 | Run Standalone Validation | MI-04 | | | | | |
| MI-04-T04 | Submit Test Migration Batch | MI-04 | | | | | |
| MI-04-T05 | Verify Teams Chat Migration | MI-04 | | | | | |
| MI-04-T06 | Verify Teams Meetings Migration | MI-04 | | | | | |
| PP-01-T01 | Verify Preview Enablement | PP-01 | | | | | |
| PP-01-T02 | Verify Test Mailbox On Hold | PP-01 | | | | | |
| PP-01-T03 | Execute Held Mailbox Migration | PP-01 | | | | | |
| PP-01-T04 | Verify Substrate Folders Remain | PP-01 | | | | | |
| PP-01-T05 | Verify Active Content Migrated | PP-01 | | | | | |
| RI-01-T01 | Verify Bidirectional Org Relationship | RI-01 | | | | | |
| RI-01-T02 | Verify Reverse Migration Endpoint | RI-01 | | | | | |
| RI-01-T03 | Execute Test Reverse Mailbox Migration | RI-01 | | | | | |
| RI-02-T01 | Verify Bidirectional Trust | RI-02 | | | | | |
| RI-02-T02 | Execute Test Reverse OneDrive Migration | RI-02 | | | | | |
| TE-01-T01 | Verify Staging Exclusion | TE-01 | | | | | |
| TE-01-T02 | Execute Hard Match | TE-01 | | | | | |
| TE-01-T03 | Verify Attribute Flow | TE-01 | | | | | |
| TE-02-T01 | Verify Modified Provisioning | TE-02 | | | | | |
| TE-02-T02 | Verify MailUser State | TE-02 | | | | | |
| TE-02-T03 | Verify CTIM Compatibility | TE-02 | | | | | |
| TE-02-T04 | Verify Post-Migration Handoff | TE-02 | | | | | |
| TE-03-T01 | Verify IGA Discovery | TE-03 | | | | | |
| TE-03-T02 | Verify IGA Management | TE-03 | | | | | |
| TE-04-T01 | Verify Soft-Delete on Descope | TE-04 | | | | | |
| TE-04-T02 | Verify Restoration | TE-04 | | | | | |
| TE-04-T03 | Verify Hard Match After Restoration | TE-04 | | | | | |
| TE-05-T01 | Verify Staging Group Excludes Exchange | TE-05 | | | | | |
| TE-05-T02 | Verify No Mailbox Provisioned | TE-05 | | | | | |
| TE-05-T03 | Verify CTIM After Staging License | TE-05 | | | | | |
| TE-05-T04 | Verify Full License After Migration | TE-05 | | | | | |
| TE-06-T01 | Verify Staging Group Configured | TE-06 | | | | | |
| TE-06-T02 | Verify OneDrive Blocked | TE-06 | | | | | |
| TE-06-T03 | Verify OneDrive After Migration | TE-06 | | | | | |
| SE-01-T01 | Verify B2B License Group | SE-01 | | | | | |
| SE-01-T02 | Verify Proxy Addresses Preserved | SE-01 | | | | | |
| SE-01-T03 | Verify Mail Routing | SE-01 | | | | | |
| AD-01-T01 | Verify Dry-Run Mode | AD-01 | | | | | |
| AD-01-T02 | Execute Conversion | AD-01 | | | | | |
| AD-01-T03 | Verify Object ID Preserved | AD-01 | | | | | |
| AD-01-T04 | Verify Group Memberships | AD-01 | | | | | |
| AD-01-T05 | Verify Error Handling | AD-01 | | | | | |
| AD-02-T01 | Verify Dry-Run Mode | AD-02 | | | | | |
| AD-02-T02 | Execute B2B Enablement | AD-02 | | | | | |
| AD-02-T03 | Verify License Group Transition | AD-02 | | | | | |
| AD-02-T04 | Verify B2B Reach Back | AD-02 | | | | | |
| AD-02-T05 | Verify Dual Account Fallback | AD-02 | | | | | |
| AD-03-T01 | Execute Target Rollback | AD-03 | | | | | |
| AD-03-T02 | Execute Source Rollback | AD-03 | | | | | |
| AD-03-T03 | Verify Full Rollback Sequence | AD-03 | | | | | |
| RD-01-T01 | Review Runbook Completeness | RD-01 | | | | | |
| RD-01-T02 | Execute Runbook for E2E Test | RD-01 | | | | | |
| RD-02-T01 | Review Rollback Runbook | RD-02 | | | | | |
| RD-02-T02 | Execute Rollback for Test User | RD-02 | | | | | |
| E2E-01-T01 | Complete E2E Migration | E2E-01 | | | | | |
| E2E-01-T02 | Verify Mail Routing | E2E-01 | | | | | |
| E2E-01-T03 | Verify Free/Busy | E2E-01 | | | | | |
| E2E-01-T04 | Verify Inbound Attribution | E2E-01 | | | | | |
| E2E-01-T05 | Verify OneDrive Access | E2E-01 | | | | | |
| E2E-01-T06 | Verify Application Access | E2E-01 | | | | | |
| E2E-01-T07 | Verify B2B Reach Back | E2E-01 | | | | | |
| E2E-01-T08 | Verify Dual Account Fallback | E2E-01 | | | | | |
| E2E-02-T01 | Complete E2E Orchestrated Migration | E2E-02 | | | | | |
| E2E-02-T02 | Verify Teams Chat | E2E-02 | | | | | |
| E2E-02-T03 | Verify Teams Meetings | E2E-02 | | | | | |
| E2E-02-T04 | All E2E-01 Tests | E2E-02 | | | | | |
| E2E-03-T01 | Execute Hard Match Sequence | E2E-03 | | | | | |
| E2E-03-T02 | Complete E2E Hybrid Migration | E2E-03 | | | | | |
| E2E-03-T03 | Verify Hybrid Attributes | E2E-03 | | | | | |
| E2E-03-T04 | All E2E-01 Tests | E2E-03 | | | | | |
| E2E-04-T01 | Execute Combined Procedure | E2E-04 | | | | | |
| E2E-04-T02 | All E2E-02 and E2E-03 Tests | E2E-04 | | | | | |

---

## Related Documents

- [Overview](index.md)
- [Implementation Backlog](backlog.md)
