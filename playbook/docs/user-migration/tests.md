# Cross-Tenant User Migration Test Cases

> **Created with AI. Pending verification by a human. Use with caution.**

This document contains validation test cases for cross-tenant user migration implementation. Tests are organized by backlog item and should be executed during validation testing with test accounts before production pilot migration.

## Test Categories

- [Test Account Setup](#ta-01-test-account-setup)
- [Migration Infrastructure](#migration-infrastructure-tests)
- [Private Preview Hold Migration](#pp-01-private-preview-hold-migration)
- [Reverse Migration Infrastructure](#reverse-migration-infrastructure-tests)
- [Target Environment Preparation](#target-environment-preparation-tests)
- [Source Environment Preparation](#se-01-source-b2b-preparation) (includes SE-02 for hybrid)
- [Automation Development](#automation-development-tests)
- [Runbook Development](#runbook-development-tests)
- [End-to-End Validation](#end-to-end-validation-tests)

---

## TA-01: Test Account Setup

**Backlog Item:** [TA-01](backlog.md#ta-01-provision-test-accounts-for-validation-testing)

### TA-01-T01: Verify Test Account Readiness

**Objective:** Confirm all test accounts are provisioned and ready for migration testing.

**Steps:**

1. Verify source test accounts have mailboxes with content:
   ```powershell
   Connect-ExchangeOnline -UserPrincipalName admin@source.onmicrosoft.com
   Get-EXOMailbox -Identity "testuser@source.com" | Select-Object DisplayName, PrimarySmtpAddress, ExchangeGuid, ArchiveStatus
   ```

2. Verify source OneDrive sites exist with test files:
   ```powershell
   Connect-SPOService -Url https://[source-tenant]-admin.sharepoint.com
   Get-SPOSite -Filter "Url -like '-my.sharepoint.com/personal/'" | Where-Object { $_.Owner -like "*testuser*" }
   ```

3. For cloud-only topology, verify target external members exist:
   ```powershell
   Connect-MgGraph -Scopes "User.Read.All"
   Get-MgUser -UserId "testuser@target.com" -Property UserType, ExternalUserState, Id
   ```

4. For hybrid topology, verify target AD accounts exist in staging OU and derive immutable IDs

5. Document complete test account inventory

**Expected Results:**

- All source test accounts have mailboxes with content
- OneDrive sites exist with test files
- Target accounts provisioned appropriately for topology
- Complete inventory documented with UPNs, GUIDs, and immutable IDs

---

## Migration Infrastructure Tests

### MI-01-T01: Validate Mailbox Migration

**Backlog Item:** [MI-01](backlog.md#mi-01-configure-cross-tenant-mailbox-migration-infrastructure)

**Objective:** Verify cross-tenant mailbox migration works end-to-end.

**Steps:**

1. Ensure test user is in source migration scope group
2. Create and start migration batch:
   ```powershell
   Connect-ExchangeOnline -UserPrincipalName admin@target.onmicrosoft.com
   New-MigrationBatch -Name "TestMigration" -SourceEndpoint "CrossTenantEndpoint" -CSVData ([System.IO.File]::ReadAllBytes("users.csv")) -TargetDeliveryDomain "target.onmicrosoft.com"
   Start-MigrationBatch -Identity "TestMigration"
   ```
3. Monitor until completion:
   ```powershell
   Get-MigrationUser -Identity "testuser@source.com" | Select-Object Status, StatusSummary
   ```
4. Verify target mailbox exists and source converted to MailUser:
   ```powershell
   # Target tenant
   Get-EXOMailbox -Identity "testuser@target.com"

   # Source tenant
   Get-EXOMailUser -Identity "testuser@source.com" | Select-Object ExternalEmailAddress
   ```

**Expected Results:**

- Migration completes successfully
- Target has full mailbox with migrated content
- Source has MailUser with targetAddress pointing to target

---

### MI-02-T01: Validate OneDrive Migration

**Backlog Item:** [MI-02](backlog.md#mi-02-configure-cross-tenant-onedrive-migration-infrastructure)

**Objective:** Verify cross-tenant OneDrive migration works end-to-end.

**Steps:**

1. Start OneDrive migration:
   ```powershell
   Connect-SPOService -Url https://[target-tenant]-admin.sharepoint.com
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

### MI-03-T01: Validate CTIM Attribute Stamping

**Backlog Item:** [MI-03](backlog.md#mi-03-configure-cross-tenant-identity-mapping-ctim)

**Objective:** Verify CTIM stamps Exchange attributes correctly on target MailUser.

**Prerequisites:** Target user must be MailUser (no Exchange license assigned)

**Steps:**

1. Verify target user is MailUser before CTIM:
   ```powershell
   Connect-ExchangeOnline -UserPrincipalName admin@target.onmicrosoft.com
   Get-EXORecipient -Identity "testuser@target.com" | Select-Object RecipientType, RecipientTypeDetails
   ```
2. Run CTIM for test user (refer to Microsoft documentation for current cmdlets)
3. Verify target MailUser attributes:
   ```powershell
   Get-EXOMailUser -Identity "testuser@target.com" | Select-Object ExchangeGuid, ArchiveGuid, LegacyExchangeDN, EmailAddresses
   ```
4. Compare with source mailbox:
   ```powershell
   Connect-ExchangeOnline -UserPrincipalName admin@source.onmicrosoft.com
   Get-EXOMailbox -Identity "testuser@source.com" | Select-Object ExchangeGuid, ArchiveGuid, LegacyExchangeDN, EmailAddresses
   ```

**Expected Results:**

- Target user is MailUser (not mailbox)
- ExchangeGuid matches between source and target
- ArchiveGuid matches (if archive-enabled)
- LegacyExchangeDN from source appears as X500 proxy address on target

**Troubleshooting:**

- If ExchangeGuid is empty: User may have been licensed before CTIM ran
- If attributes don't match: Re-run identity mapping

---

### MI-04-T01: Validate Migration Orchestrator

**Backlog Item:** [MI-04](backlog.md#mi-04-configure-migration-orchestrator)

**Objective:** Verify orchestrator can migrate all workloads (mailbox, OneDrive, Teams chat, meetings).

**Steps:**

1. Run orchestrator standalone validation for test user
2. Submit orchestrator migration batch
3. Monitor progress through all stages
4. After completion, verify:
   - Mailbox accessible in target
   - OneDrive content in target
   - Teams chat history visible in target
   - Meetings rescheduled in target calendar

**Expected Results:**

- Batch progresses through all stages without errors
- All workloads complete successfully
- Chat history and meetings visible in target tenant

---

## PP-01: Private Preview Hold Migration

**Backlog Item:** [PP-01](backlog.md#pp-01-enable-private-preview-for-on-hold-mailbox-migration)

### PP-01-T01: Validate Held Mailbox Migration

**Objective:** Verify mailbox on hold can be migrated with private preview enabled.

**Prerequisites:** Private preview enabled on both tenants; test mailbox with litigation hold

**Steps:**

1. Verify test mailbox has hold:
   ```powershell
   Connect-ExchangeOnline -UserPrincipalName admin@source.onmicrosoft.com
   Get-EXOMailbox -Identity "testuser-hold@source.com" | Select-Object LitigationHoldEnabled, InPlaceHolds
   ```
2. Submit migration for held mailbox
3. Monitor for completion (should not fail due to hold)
4. Verify active content accessible in target
5. Use eDiscovery to verify substrate content searchable in source

**Expected Results:**

- Migration completes successfully despite hold status
- Active mailbox content accessible in target
- Substrate folders (ComponentShared, SubstrateHolds) remain in source
- eDiscovery can search substrate content via source MailUser

---

## Reverse Migration Infrastructure Tests

### RI-01-T01: Validate Reverse Mailbox Migration

**Backlog Item:** [RI-01](backlog.md#ri-01-configure-reverse-mailbox-migration-infrastructure)

**Objective:** Verify mailbox can be migrated from target back to source.

**Steps:**

1. Verify bidirectional organization relationships:
   ```powershell
   # Source tenant
   Get-OrganizationRelationship | Select-Object Name, MailboxMoveCapability

   # Target tenant
   Get-OrganizationRelationship | Select-Object Name, MailboxMoveCapability
   ```
2. Execute reverse migration for test user
3. Verify mailbox accessible in source after migration

**Expected Results:**

- Both tenants show Inbound and RemoteOutbound capabilities
- Reverse migration completes successfully
- Mailbox accessible in source

---

### RI-02-T01: Validate Reverse OneDrive Migration

**Backlog Item:** [RI-02](backlog.md#ri-02-configure-reverse-onedrive-migration-infrastructure)

**Objective:** Verify OneDrive can be migrated from target back to source.

**Steps:**

1. Verify bidirectional trust:
   ```powershell
   Get-SPOCrossTenantRelationship -Scenario MnA
   ```
2. Execute reverse OneDrive migration for test user
3. Verify content accessible in source
4. Verify target URL redirects to source

**Expected Results:**

- Both tenants show Source and Target partner roles
- Reverse migration completes successfully
- Content accessible in source; target URL redirects

---

## Target Environment Preparation Tests

### TE-01-T01: Validate Hard Match Process

**Backlog Item:** [TE-01](backlog.md#te-01-configure-target-entra-connect-for-hard-match)

**Objective:** Verify hard match successfully links cloud account with AD account.

**Steps:**

1. Verify test AD account in staging OU does not sync to Entra ID
2. Stamp immutable ID on cloud external member:
   ```powershell
   $immutableId = "[base64-encoded-id]"
   Update-MgUser -UserId "[cloud-user-object-id]" -OnPremisesImmutableId $immutableId
   ```
3. Move AD account to production OU (or clear staging attribute)
4. Run Entra Connect sync cycle
5. Verify hard match:
   ```powershell
   Get-MgUser -UserId "testuser@target.com" -Property OnPremisesSyncEnabled, OnPremisesImmutableId
   ```

**Expected Results:**

- Object ID preserved (same as before hard match)
- OnPremisesSyncEnabled is True
- No duplicate objects created
- Attributes flow correctly from AD

---

### TE-02-T01: Validate IGA/JML Hybrid Integration

**Backlog Item:** [TE-02](backlog.md#te-02-configure-target-igajml-integration-for-hybrid-topology)

**Objective:** Verify IGA provisions migration users without remote mailbox and can manage post-migration.

**Steps:**

1. Provision test user via modified IGA workflow
2. Verify AD account created without remote mailbox
3. Verify user is MailUser after sync:
   ```powershell
   Get-EXORecipient -Identity "testuser@target.com" | Select-Object RecipientType
   ```
4. Run CTIM and verify compatibility
5. Complete migration and verify IGA can manage user post-migration

**Expected Results:**

- IGA provisions users as MailUser (not mailbox)
- CTIM can stamp attributes
- IGA assumes management authority after migration

---

### TE-03-T01: Validate IGA/JML Cloud Integration

**Backlog Item:** [TE-03](backlog.md#te-03-configure-target-igajml-integration-for-cloud-only-topology)

**Objective:** Verify IGA can discover and manage converted internal members.

**Steps:**

1. Convert test external member to internal
2. Verify IGA discovers migrated user
3. Verify IGA can update user attributes without conflicts

**Expected Results:**

- IGA discovers converted internal member
- IGA can manage user without attribute conflicts

---

### TE-04-T01: Validate XTS Descoping and Restoration

**Backlog Item:** [TE-04](backlog.md#te-04-configure-cross-tenant-sync-descoping-and-account-restoration)

**Objective:** Verify soft-deleted users can be restored after XTS descoping.

**Steps:**

1. Remove test user from XTS scope
2. Wait for sync cycle
3. Verify user soft-deleted:
   ```powershell
   Get-MgDirectoryDeletedItem -DirectoryObjectId "[user-object-id]"
   ```
4. Restore user:
   ```powershell
   Restore-MgDirectoryDeletedItem -DirectoryObjectId "[user-object-id]"
   ```
5. Verify user restored with same object ID

**Expected Results:**

- User appears in deleted items after descoping
- User restores successfully with same object ID
- For hybrid: hard match still works after restoration

---

### TE-05-T01: Validate License Staging Strategy

**Backlog Item:** [TE-05](backlog.md#te-05-configure-target-license-assignment-strategy)

**Objective:** Verify staging license group does not provision mailbox.

**Steps:**

1. Add test user to staging license group (no Exchange plans)
2. Wait for license assignment
3. Verify user remains MailUser:
   ```powershell
   Get-EXORecipient -Identity "testuser@target.com" | Select-Object RecipientType
   ```
4. Run CTIM and verify ExchangeGuid stamped
5. Complete migration and move to full license group

**Expected Results:**

- User remains MailUser with staging license
- CTIM succeeds with staging license
- Migration works; full license activates mailbox

---

### TE-06-T01: Validate OneDrive Blocking

**Backlog Item:** [TE-06](backlog.md#te-06-block-target-onedrive-provisioning-for-staged-users)

**Objective:** Verify staged users cannot create OneDrive before migration.

**Steps:**

1. Add test user to OneDrive blocking group
2. Attempt to access OneDrive as test user
3. Complete OneDrive migration
4. Remove from blocking group
5. Verify migrated OneDrive accessible

**Expected Results:**

- User cannot create OneDrive while in blocking group
- Migrated OneDrive accessible after group removal

---

### TE-07-T01: Validate Conditional Access Policy Compatibility

**Backlog Item:** [TE-07](backlog.md#te-07-review-and-adjust-conditional-access-policies)

**Objective:** Verify migrated users can access resources with device-based CA policies in place.

**Steps:**

1. Configure test user as migrated (target internal member, source B2B enabled)
2. From target-managed device, access target tenant resources
3. From target-managed device, access source tenant resources via B2B reach back
4. If dual account fallback required: attempt source credential login from target-managed device
5. Document any access blocks or MFA prompts

**Expected Results:**

- Target tenant access works from target-managed device
- B2B reach back access to source tenant works (or documented exception in place)
- Dual account fallback works if required (or documented exception in place)

---

## SE-01: Source B2B Preparation

**Backlog Item:** [SE-01](backlog.md#se-01-configure-source-b2b-enablement-preparation)

### SE-01-T01: Validate B2B License Group and Primary SMTP Update

**Objective:** Verify B2B license group preserves proxy addresses and primary SMTP can be updated to target domain.

**Steps:**

1. After migration, move test MailUser to B2B license group (no EXO plans)
2. Remove from standard license group
3. Verify proxy addresses preserved:
   ```powershell
   Get-EXOMailUser -Identity "testuser@source.com" | Select-Object EmailAddresses
   ```
4. Update primary SMTP to target domain:
   ```powershell
   Set-MailUser -Identity "testuser@source.com" -PrimarySmtpAddress "testuser@target.com"
   ```
5. Verify primary SMTP updated:
   ```powershell
   Get-EXOMailUser -Identity "testuser@source.com" | Select-Object PrimarySmtpAddress
   ```
6. Send email to source address and verify delivery to target

**Expected Results:**

- All proxy addresses preserved (no scrubbing)
- Primary SMTP successfully updated to target domain
- Mail routes correctly to target mailbox

---

## SE-02: Hybrid Source Remote Mailbox Conversion

**Backlog Item:** [SE-02](backlog.md#se-02-configure-hybrid-source-remote-mailbox-conversion)

### SE-02-T01: Validate Remote Mailbox to Mail User Conversion

**Objective:** Verify remote mailbox can be converted to mail user for hybrid source tenants.

**Prerequisites:** Hybrid source tenant with on-premises Exchange; test user migrated with remote mailbox object in on-premises AD

**Steps:**

1. Verify test user is remote mailbox in on-premises Exchange:
   ```powershell
   # On-premises Exchange Management Shell
   Get-RemoteMailbox -Identity "testuser@source.com"
   ```

2. Disable remote mailbox:
   ```powershell
   Disable-RemoteMailbox -Identity "testuser@source.com" -Confirm:$false
   ```

3. Enable as mail user with target external address:
   ```powershell
   Enable-MailUser -Identity "testuser@source.com" -ExternalEmailAddress "testuser@target.com"
   ```

4. Set primary SMTP to target domain:
   ```powershell
   Set-MailUser -Identity "testuser@source.com" -PrimarySmtpAddress "testuser@target.com"
   ```

5. Sync changes to Entra ID:
   ```powershell
   Start-ADSyncSyncCycle -PolicyType Delta
   ```

6. Verify cloud object is MailUser:
   ```powershell
   Connect-ExchangeOnline -UserPrincipalName admin@source.onmicrosoft.com
   Get-EXOMailUser -Identity "testuser@source.com" | Select-Object RecipientType, RecipientTypeDetails, PrimarySmtpAddress
   ```

7. Proceed with B2B enablement and verify success

**Expected Results:**

- Remote mailbox successfully converted to mail user
- Changes sync to Entra ID
- Cloud object shows as MailUser with correct primary SMTP
- B2B enablement succeeds after conversion

---

## Automation Development Tests

### AD-01-T01: Validate Target Conversion Scripts

**Backlog Item:** [AD-01](backlog.md#ad-01-develop-target-account-conversion-scripts)

**Objective:** Verify scripts convert external members to internal correctly.

**Steps:**

1. Run conversion script with -DryRun flag; verify eligible users reported
2. Execute conversion for test user
3. Verify:
   - UserType changed to internal Member
   - Object ID preserved
   - Group memberships preserved

**Expected Results:**

- Dry run reports eligible users without changes
- Conversion succeeds
- Object ID and group memberships preserved

---

### AD-02-T01: Validate Source Conversion Scripts

**Backlog Item:** [AD-02](backlog.md#ad-02-develop-source-account-conversion-scripts)

**Objective:** Verify scripts enable B2B collaboration correctly.

**Steps:**

1. Run B2B enablement script with -DryRun flag
2. Execute B2B enablement for test user
3. Verify user converted to external member linked to target
4. Verify B2B reach back: access source apps with target credentials
5. Verify fallback: access source apps with source credentials

**Expected Results:**

- User converted to external member
- B2B reach back works with target credentials
- Fallback works with source credentials

---

### AD-03-T01: Validate Identity Rollback Scripts

**Backlog Item:** [AD-03](backlog.md#ad-03-develop-identity-rollback-scripts)

**Objective:** Verify identity conversions can be reverted.

**Steps:**

1. Execute target rollback (internal → external linked to source)
2. Execute source rollback (external → internal)
3. Execute full rollback sequence per runbook
4. Verify user restored to pre-migration state

**Expected Results:**

- Target user reverts to external member
- Source user reverts to internal member
- Full rollback restores services to source

---

### AD-04-T01: Validate MTO External Member Rehoming

**Backlog Item:** [AD-04](backlog.md#ad-04-develop-mto-external-member-rehoming-scripts)

**Objective:** Verify external members in other MTO tenants can be rehomed to target identity.

**Prerequisites:** Larger MTO with at least one additional tenant beyond source and target

**Steps:**

1. Identify test user's external member in other MTO tenant
2. Remove test user from source XTS scope to other tenant
3. Wait for sync cycle; verify external member soft-deleted
4. Restore soft-deleted external member
5. Execute rehoming script (convert to internal, update email, invite to B2B)
6. Add test user to target XTS scope to other tenant
7. Verify XTS matches existing rehomed account (no duplicate)
8. Verify user can access resources in other tenant with target credentials

**Expected Results:**

- External member rehomed with same object ID
- User retains group memberships and app assignments in other tenant
- User can access other tenant resources with target credentials
- No duplicate accounts created

---

### AD-05-T01: Validate Mailbox Permission Export and Reapplication

**Backlog Item:** [AD-05](backlog.md#ad-05-develop-mailbox-permission-export-and-reapplication-scripts)

**Objective:** Verify mailbox permissions can be exported from source and reapplied in target.

**Steps:**

1. Export all permission types from source test mailbox:
   ```powershell
   Connect-ExchangeOnline -UserPrincipalName admin@source.onmicrosoft.com
   Get-EXOMailboxPermission -Identity "testuser@source.com" | Where-Object { $_.User -ne "NT AUTHORITY\SELF" }
   Get-EXORecipientPermission -Identity "testuser@source.com"
   Get-EXOMailbox -Identity "testuser@source.com" | Select-Object GrantSendOnBehalfTo
   ```
2. Migrate test mailbox to target
3. Run permission reapplication script with identity mapping
4. Verify permissions in target:
   ```powershell
   Connect-ExchangeOnline -UserPrincipalName admin@target.onmicrosoft.com
   Get-EXOMailboxPermission -Identity "testuser@target.com"
   Get-EXORecipientPermission -Identity "testuser@target.com"
   ```
5. Test delegate access in target tenant

**Expected Results:**

- All permissions exported correctly from source
- Permissions reapplied successfully in target using mapped identities
- Delegates can access mailbox with appropriate permissions

---

### AD-06-T01: Validate Resource Mailbox Post-Migration Configuration

**Backlog Item:** [AD-06](backlog.md#ad-06-develop-resource-mailbox-post-migration-configuration-scripts-formerly-ad-05)

**Objective:** Verify resource mailbox settings can be reconfigured after migration.

**Steps:**

1. Migrate test resource mailbox using standalone migration (not orchestrator)
2. Export settings from source before migration
3. Run post-migration configuration script
4. Verify booking policies applied:
   ```powershell
   Get-CalendarProcessing -Identity "room@target.com" | Select-Object AutomateProcessing, AllowConflicts, BookingWindowInDays
   ```
5. Test booking the resource from target tenant

**Expected Results:**

- All booking policies applied correctly
- Resource delegates configured
- Room appears in room finder (if room list recreated)
- Booking requests processed correctly

---

## Runbook Development Tests

### RD-01-T01: Validate Production Runbook

**Backlog Item:** [RD-01](backlog.md#rd-01-develop-production-migration-runbook)

**Objective:** Verify runbook can be executed without ambiguity.

**Steps:**

1. Review runbook with operations team for completeness
2. Execute runbook for E2E validation test
3. Document any gaps or ambiguities

**Expected Results:**

- All steps documented with clear instructions
- Timing estimates accurate
- Verification checkpoints defined
- Runbook executes without ambiguity

---

### RD-02-T01: Validate Rollback Runbook

**Backlog Item:** [RD-02](backlog.md#rd-02-develop-rollback-runbook)

**Objective:** Verify rollback runbook executes successfully.

**Steps:**

1. Review rollback runbook with operations team
2. Execute rollback for test user
3. Verify timing matches estimates

**Expected Results:**

- Triggers and decision criteria clear
- Rollback completes successfully
- Timing estimates accurate

---

## End-to-End Validation Tests

### E2E-01-T01: Cloud-Only Target without Orchestrator

**Backlog Item:** [E2E-01](backlog.md#e2e-01-end-to-end-validation---cloud-only-target-without-orchestrator)

**Objective:** Execute complete migration for cloud-only topology using standalone tools.

**Steps:**

1. Execute full migration per runbook for 2-3 test users
2. Verify all post-migration functionality:
   - Mail routing (both directions)
   - Free/busy visibility
   - Inbound attribution (send to restricted DL)
   - OneDrive access and redirect
   - Application SSO
   - B2B reach back to source
   - Dual account fallback

**Expected Results:**

- All users migrated successfully
- All coexistence scenarios work correctly

---

### E2E-02-T01: Cloud-Only Target with Orchestrator

**Backlog Item:** [E2E-02](backlog.md#e2e-02-end-to-end-validation---cloud-only-target-with-orchestrator)

**Objective:** Execute complete orchestrated migration for cloud-only topology.

**Steps:**

1. Execute orchestrator migration for test users including Teams data
2. Verify all E2E-01 functionality plus:
   - Teams chat history visible
   - Meetings rescheduled correctly

**Expected Results:**

- All workloads migrate successfully
- Teams chat and meetings accessible in target

---

### E2E-03-T01: Hybrid Target without Orchestrator

**Backlog Item:** [E2E-03](backlog.md#e2e-03-end-to-end-validation---hybrid-target-without-orchestrator)

**Objective:** Execute complete migration for hybrid topology using standalone tools.

**Steps:**

1. Execute hard match sequence per runbook
2. Execute full migration including hybrid identity
3. Verify all E2E-01 functionality plus:
   - OnPremisesSyncEnabled is True
   - Attributes syncing from AD

**Expected Results:**

- Hard match successful; object ID preserved
- Hybrid identity syncing correctly
- All coexistence scenarios work

---

### E2E-04-T01: Hybrid Target with Orchestrator

**Backlog Item:** [E2E-04](backlog.md#e2e-04-end-to-end-validation---hybrid-target-with-orchestrator)

**Objective:** Execute complete orchestrated migration for hybrid topology.

**Steps:**

1. Execute combined hybrid + orchestrator procedure
2. Pay attention to hard match timing relative to orchestrator pre-staging
3. Verify all E2E-02 and E2E-03 functionality

**Expected Results:**

- Combined procedure completes without conflicts
- All workloads and hybrid identity work correctly

---

## Test Execution Tracking

| Test ID | Description | Backlog ID | Test Account(s) | Status | Date | Tester | Notes |
|---------|-------------|------------|-----------------|--------|------|--------|-------|
| TA-01-T01 | Verify Test Account Readiness | TA-01 | | | | | |
| MI-01-T01 | Validate Mailbox Migration | MI-01 | | | | | |
| MI-02-T01 | Validate OneDrive Migration | MI-02 | | | | | |
| MI-03-T01 | Validate CTIM Attribute Stamping | MI-03 | | | | | |
| MI-04-T01 | Validate Migration Orchestrator | MI-04 | | | | | |
| PP-01-T01 | Validate Held Mailbox Migration | PP-01 | | | | | |
| RI-01-T01 | Validate Reverse Mailbox Migration | RI-01 | | | | | |
| RI-02-T01 | Validate Reverse OneDrive Migration | RI-02 | | | | | |
| TE-01-T01 | Validate Hard Match Process | TE-01 | | | | | |
| TE-02-T01 | Validate IGA/JML Hybrid Integration | TE-02 | | | | | |
| TE-03-T01 | Validate IGA/JML Cloud Integration | TE-03 | | | | | |
| TE-04-T01 | Validate XTS Descoping and Restoration | TE-04 | | | | | |
| TE-05-T01 | Validate License Staging Strategy | TE-05 | | | | | |
| TE-06-T01 | Validate OneDrive Blocking | TE-06 | | | | | |
| TE-07-T01 | Validate CA Policy Compatibility | TE-07 | | | | | |
| SE-01-T01 | Validate B2B License Group and Primary SMTP | SE-01 | | | | | |
| SE-02-T01 | Validate Remote Mailbox Conversion | SE-02 | | | | | |
| AD-01-T01 | Validate Target Conversion Scripts | AD-01 | | | | | |
| AD-02-T01 | Validate Source Conversion Scripts | AD-02 | | | | | |
| AD-03-T01 | Validate Identity Rollback Scripts | AD-03 | | | | | |
| AD-04-T01 | Validate MTO External Member Rehoming | AD-04 | | | | | |
| AD-05-T01 | Validate Mailbox Permission Scripts | AD-05 | | | | | |
| AD-06-T01 | Validate Resource Mailbox Post-Migration | AD-06 | | | | | |
| RD-01-T01 | Validate Production Runbook | RD-01 | | | | | |
| RD-02-T01 | Validate Rollback Runbook | RD-02 | | | | | |
| E2E-01-T01 | Cloud-Only without Orchestrator | E2E-01 | | | | | |
| E2E-02-T01 | Cloud-Only with Orchestrator | E2E-02 | | | | | |
| E2E-03-T01 | Hybrid without Orchestrator | E2E-03 | | | | | |
| E2E-04-T01 | Hybrid with Orchestrator | E2E-04 | | | | | |

---

## Related Documents

- [Overview](index.md)
- [Implementation Backlog](backlog.md)
