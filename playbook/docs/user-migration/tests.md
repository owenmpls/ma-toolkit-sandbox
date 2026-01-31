# Cross-Tenant User Migration Test Cases

> **Created with AI. Pending verification by a human. Use with caution.**

This document provides test cases to validate cross-tenant user migration setup and functionality. Execute these tests with pilot users before production migration.

## Test Categories

- [Tenant Configuration Tests](#tenant-configuration-tests)
- [Identity Mapping Tests](#identity-mapping-tests)
- [Pre-Migration Coexistence Tests](#pre-migration-coexistence-tests)
- [Migration Execution Tests](#migration-execution-tests)
- [Post-Migration Coexistence Tests](#post-migration-coexistence-tests)
- [Application Access Tests](#application-access-tests)
- [Edge Case Tests](#edge-case-tests)

---

## Tenant Configuration Tests

### TC-01: Cross-Tenant Access Settings

**Objective:** Verify cross-tenant access settings are correctly configured for B2B collaboration.

**Steps:**

1. Sign in to the target tenant as a source user (external member)
2. Access Microsoft 365 apps (Teams, SharePoint, Outlook)
3. Verify MFA is not re-prompted (if MFA trust is configured)
4. Verify device compliance is recognized (if device trust is configured)

**Expected Results:**

- Source user can authenticate to target tenant using source credentials
- MFA challenge is satisfied by source tenant MFA (single challenge)
- Device compliance status is recognized across tenant boundary
- User has member-level permissions in target tenant

**Troubleshooting:**

- If MFA is re-prompted: Verify inboundTrust settings include MFA
- If access is denied: Verify user is external member (not guest)

---

### TC-02: Organization Relationship for Free/Busy

**Objective:** Verify organization relationships enable free/busy lookups between tenants.

**Steps:**

1. From source tenant Outlook, schedule a meeting with a target tenant user
2. Open Scheduling Assistant
3. Observe free/busy information for the target user
4. Repeat from target tenant looking up source user

**Expected Results:**

- Free/busy information displays correctly in both directions
- Calendar shows busy times, not just "No information available"
- Scheduling Assistant can identify available meeting times

**Troubleshooting:**

- If free/busy fails: Run `Get-OrganizationRelationship` and verify DomainNames includes the other tenant
- Verify AvailabilityAddressSpace is configured if using hybrid
- Check targetAddress on MailUser objects

---

### TC-03: Migration Endpoint Connectivity

**Objective:** Verify migration endpoints are correctly configured and accessible.

**Steps:**

1. Run migration endpoint test from target tenant:

   ```powershell
   Test-MigrationServerAvailability -Endpoint <EndpointName>
   ```

2. Verify organization relationship allows mailbox moves:

   ```powershell
   Get-OrganizationRelationship | Select-Object Name, MailboxMoveEnabled, MailboxMoveCapability
   ```

**Expected Results:**

- Migration endpoint test succeeds
- MailboxMoveEnabled is True
- MailboxMoveCapability shows correct direction (Inbound/Outbound)

---

## Identity Mapping Tests

### TC-04: Cross-Tenant Identity Mapping Attributes

**Objective:** Verify CTIM correctly stamps Exchange attributes on target MailUser objects.

**Steps:**

1. Run identity mapping for test user
2. Verify attributes in Exchange Online (target tenant):

   ```powershell
   Get-MailUser -Identity "user@target.com" | Select-Object ExchangeGuid, ArchiveGuid, LegacyExchangeDN, EmailAddresses
   ```

3. Compare with source mailbox:

   ```powershell
   Get-Mailbox -Identity "user@source.com" | Select-Object ExchangeGuid, ArchiveGuid, LegacyExchangeDN, EmailAddresses
   ```

**Expected Results:**

- ExchangeGuid matches between source and target
- ArchiveGuid matches (if archive-enabled)
- LegacyExchangeDN from source appears as X500 proxy address on target
- All source X500 addresses are present on target

**Troubleshooting:**

- If ExchangeGuid is empty: User may have been licensed before CTIM ran
- If attributes don't match: Re-run identity mapping

---

### TC-05: Target User License State

**Objective:** Verify target users are not licensed before identity mapping.

**Steps:**

1. Check license assignment for target user:

   ```powershell
   Get-MgUserLicenseDetail -UserId "user@target.com"
   ```

2. Check recipient type:

   ```powershell
   Get-Recipient -Identity "user@target.com" | Select-Object RecipientType, RecipientTypeDetails
   ```

**Expected Results:**

- No EXO-related licenses assigned before CTIM
- RecipientType is MailUser (not UserMailbox)
- RecipientTypeDetails is MailUser

**Troubleshooting:**

- If user has a mailbox: License was applied before CTIM; remove license, delete mailbox, wait for soft-delete, run CTIM again

---

## Pre-Migration Coexistence Tests

### TC-06: Mail Flow Source to Target

**Objective:** Verify mail routes correctly from source to target users.

**Steps:**

1. Send email from source mailbox to target MailUser
2. Verify email arrives at target MailUser's external address

**Expected Results:**

- Email is delivered to the target tenant
- Mail routing follows targetAddress on target MailUser

**Note:** This tests the pre-migration state where target users don't yet have mailboxes.

---

### TC-07: Free/Busy Pre-Migration (Target to Source)

**Objective:** Verify target users can see source mailbox free/busy.

**Steps:**

1. Sign in to target tenant as external member
2. Schedule a meeting with source tenant user (who has a mailbox)
3. Open Scheduling Assistant

**Expected Results:**

- Free/busy shows source user's calendar availability
- MailUser targetAddress correctly redirects lookup to source

---

### TC-08: Teams Collaboration Pre-Migration

**Objective:** Verify Teams collaboration works for external members in target tenant.

**Steps:**

1. As source user (external member in target), access Teams in target tenant
2. Chat with target tenant users
3. Join a Teams meeting in target tenant
4. Access a Teams channel (if shared channels are configured)

**Expected Results:**

- Chat functions correctly
- Meeting join works with audio/video
- Channel access works (if configured)

---

## Migration Execution Tests

### TC-09: Mailbox Migration

**Objective:** Verify mailbox migration completes successfully.

**Steps:**

1. Start migration batch for test user
2. Monitor migration status:

   ```powershell
   Get-MigrationUser -Identity "user@source.com" | Select-Object Status, StatusSummary
   ```

3. After completion, verify mailbox in target:

   ```powershell
   Get-Mailbox -Identity "user@target.com"
   ```

4. Verify MailUser in source:

   ```powershell
   Get-MailUser -Identity "user@source.com" | Select-Object ExternalEmailAddress
   ```

**Expected Results:**

- Migration status shows Completed
- Target has full mailbox with migrated content
- Source has MailUser with targetAddress pointing to target

---

### TC-10: OneDrive Migration

**Objective:** Verify OneDrive migration completes successfully.

**Steps:**

1. Start OneDrive migration for test user
2. Monitor migration status
3. After completion, verify content in target OneDrive
4. Verify redirect in source (navigate to old OneDrive URL)

**Expected Results:**

- All OneDrive content appears in target
- Old OneDrive URL redirects to target location
- Permissions are preserved

---

### TC-11: Teams Chat Migration

**Objective:** Verify Teams chat history migrates correctly.

**Steps:**

1. Before migration: Document existing 1:1 and group chat history
2. Run migration including Teams chat
3. After migration: Verify chat history in target tenant Teams

**Expected Results:**

- 1:1 chat history is visible in target
- Group chat history is visible in target
- Chat participants are correctly mapped

---

### TC-12: Teams Meeting Migration

**Objective:** Verify Teams meetings are rescheduled in target.

**Steps:**

1. Before migration: Note existing meetings organized by test user
2. Run migration including Teams meetings
3. After migration: Check calendar in target tenant

**Expected Results:**

- Source meetings are canceled
- Equivalent meetings are created in target
- Attendees receive updated meeting invites

---

## Post-Migration Coexistence Tests

### TC-13: Mail Flow Target to Source

**Objective:** Verify mail routes correctly from target (new mailbox) to source (MailUser → target).

**Steps:**

1. Send email from migrated user's target mailbox to a source recipient
2. Verify delivery

**Expected Results:**

- Email is delivered successfully
- Mail routing works via source MailUser

---

### TC-14: Mail Flow External to Source

**Objective:** Verify external mail addressed to source addresses is delivered to target mailbox.

**Steps:**

1. Send email from external sender to user's source email address
2. Verify delivery to target mailbox

**Expected Results:**

- Email arrives at target mailbox
- Source MailUser correctly routes via targetAddress

---

### TC-15: Free/Busy Post-Migration (Source to Target)

**Objective:** Verify source users can see migrated user's calendar in target.

**Steps:**

1. From source tenant Outlook, schedule meeting with migrated user
2. Open Scheduling Assistant

**Expected Results:**

- Free/busy shows migrated user's calendar from target tenant
- MailUser targetAddress correctly redirects lookup

---

### TC-16: Inbound Attribution and Sender Authorization

**Objective:** Verify inbound attribution works for mail from migrated users to source restricted recipients.

**Steps:**

1. Ensure source MailUser has target email address in proxy addresses
2. From target tenant, send email to a restricted distribution list in source tenant
3. Verify delivery

**Expected Results:**

- Email is accepted by the restricted DL
- Sender authorization recognizes the MailUser via attribution

**Troubleshooting:**

- If rejected: Verify target email address is in source MailUser's proxyAddresses
- Verify no EXO licenses are assigned that would trigger proxy scrubbing

---

### TC-17: Profile Resolution

**Objective:** Verify source recipients can view migrated user's profile.

**Steps:**

1. From source tenant Outlook, receive email from migrated user
2. Click on sender name to view profile

**Expected Results:**

- Profile card displays MailUser information from source GAL
- Photo, title, and contact information are visible

---

## Application Access Tests

### TC-18: Target Conversion (External to Internal)

**Objective:** Verify external member converts to internal member correctly.

**Steps:**

1. Convert test user from external member to internal member
2. Verify account state:

   ```powershell
   Get-MgUser -UserId "user@target.com" | Select-Object UserType, ExternalUserState
   ```

3. Test access to target tenant applications

**Expected Results:**

- UserType is Member
- ExternalUserState is null (internal user)
- Applications recognize user as internal member

---

### TC-19: Source B2B Enablement

**Objective:** Verify source account is correctly enabled for B2B reach back.

**Steps:**

1. Change source user's email address to target address
2. Enable B2B collaboration for source user
3. Verify B2B state and revert email address
4. Test reach back access from target credentials

**Expected Results:**

- Source account is linked to target identity
- User can access source resources with target credentials
- Original email address is restored after B2B enablement

---

### TC-20: Reach Back Access to Source Applications

**Objective:** Verify migrated users can access source applications via B2B.

**Steps:**

1. Sign in with target credentials
2. Access source tenant SharePoint site
3. Access source tenant Teams (if applicable)
4. Access source tenant custom applications

**Expected Results:**

- Access works using target credentials
- Permissions are preserved via B2B identity linking
- No additional sign-in prompts (with XTAS trust configured)

---

### TC-21: Viva Engage Access (Target)

**Objective:** Verify Viva Engage access in target tenant after migration.

**Steps:**

1. Access Viva Engage in target tenant
2. Verify user is recognized as internal member (not B2B)
3. Test posting, commenting, and community access

**Expected Results:**

- User has full Viva Engage access as internal member
- No B2B restrictions apply

**Known Issues:**

- If user accessed Viva Engage as external member before migration, they may remain in B2B state
- Administrative removal and reprovisioning may be required
- Test thoroughly if Viva Engage coexistence was used pre-migration

---

### TC-22: Viva Engage Reach Back (Source)

**Objective:** Verify reach back access to source Viva Engage network.

**Steps:**

1. Access source Viva Engage using deep link (e.g., https://engage.cloud.microsoft/main/org/source.com)
2. Verify access to source network content

**Expected Results:**

- Access works via deep link
- User can view source network content

**Known Issues:**

- Network switching from target to source may not work
- Deep links are the supported method for reach back access

---

## Edge Case Tests

### TC-23: Mailbox on Hold Migration (Private Preview)

**Objective:** Verify mailbox on hold migrates correctly with substrate folders retained.

**Prerequisite:** Enrolled in private preview for hold migration

**Steps:**

1. Verify mailbox is on hold
2. Run migration
3. After migration, verify target mailbox content
4. Verify source substrate folders are searchable via eDiscovery

**Expected Results:**

- Active mailbox content and recoverable items migrate to target
- Source retains substrate folders (ComponentShared, SubstrateHolds)
- eDiscovery can search source substrate content by email address
- Hold is preserved on source substrate content

---

### TC-24: Hybrid Identity Hard Match

**Objective:** Verify cloud account hard matches with on-premises AD account.

**Steps:**

1. Provision AD account in target forest (excluded from sync scope)
2. Derive and assign immutable ID to cloud account
3. Move AD account into Entra Connect sync scope
4. Wait for synchronization

**Expected Results:**

- Cloud account is linked with AD account
- No duplicate objects created
- Source of authority transitions to on-premises

---

### TC-25: License Group Transition

**Objective:** Verify license transition without service disruption.

**Steps:**

1. Add source user to new license group (with EXO plans disabled)
2. Remove user from old license group
3. Verify license state and service availability

**Expected Results:**

- License remains assigned throughout transition
- Non-EXO services remain functional
- No proxy scrubbing occurs on MailUser

**Special Consideration:** Test with grace-period licenses if VL remap has occurred.

---

### TC-26: Cross-Tenant Sync Scope Removal

**Objective:** Verify behavior when removing user from cross-tenant sync scope after hard match.

**Steps:**

1. After user is hard matched with AD, remove from cross-tenant sync scope
2. Monitor provisioning logs
3. Verify target account state

**Expected Results:**

- Provisioning errors appear in logs (expected)
- Target account is NOT soft-deleted
- Target account remains functional

**Note:** To avoid provisioning errors, remove from scope before hard match, allow soft-delete, then restore during hard match process. This approach is not fully tested.

---

### TC-27: Connected Device Reconfiguration

**Objective:** Verify Outlook and OneDrive clients can be reconfigured after migration.

**Steps:**

1. Before migration: Note Outlook profile and OneDrive sync state
2. After migration: Observe client behavior (disconnection expected)
3. Reconfigure Outlook with target account
4. Reconfigure OneDrive sync with target account

**Expected Results:**

- Clients disconnect immediately after cutover
- Reconfiguration with target credentials succeeds
- Email and files are accessible from target tenant

---

### TC-28: Target UPN Update

**Objective:** Verify target external member UPN can be updated to desired target-state value and propagates correctly.

**Steps:**

1. Update UPN on target external member to desired target-state UPN
2. Wait for propagation (allow time for async updates)
3. Verify UPN change in Entra ID:

   ```powershell
   Get-MgUser -UserId "<objectId>" | Select-Object UserPrincipalName
   ```

4. Verify UPN change in Exchange Online:

   ```powershell
   Get-MailUser -Identity "user@target.com" | Select-Object UserPrincipalName
   ```

5. Verify identity mapping and migration tools reference the new UPN correctly

**Expected Results:**

- UPN is updated successfully on external member
- UPN propagates to Exchange Online MailUser
- Identity maps and migration tools can reference the updated UPN
- No duplicate objects or conflicts created

---

### TC-29: Unverified Address Removal Timing

**Objective:** Determine when unverified addresses must be removed from target MailUser objects for mailbox migration to succeed.

**Steps:**

1. Identify unverified addresses on target MailUser (source domain addresses)
2. Submit migration batch for pre-staging WITH unverified addresses still present
3. Monitor pre-staging status:
   - If pre-staging fails: Record that removal is required before batch submission
   - If pre-staging succeeds: Proceed to step 4
4. Attempt cutover WITH unverified addresses still present
5. Monitor cutover status:
   - If cutover fails: Record that removal is required before cutover (but can remain during pre-staging)
   - If cutover succeeds: Record that removal may not be required (unexpected)

**Expected Results:**

- Pre-staging likely fails if unverified addresses are present (requires validation)
- Document the earliest point at which removal is required
- Document the pre-staging window duration (this determines the attribution gap length)

**Note:** This is a critical validation test. Results determine the duration of the pre-migration attribution gap for production batches.

---

### TC-30: Pre-Migration Attribution Gap

**Objective:** Verify the impact of unverified address removal on inbound attribution during the pre-staging period.

**Prerequisite:** Unverified addresses have been removed from target MailUser

**Steps:**

1. From source mailbox, send email to a target recipient
2. Target recipient views the email and clicks on sender
3. From source mailbox, send email to a restricted distribution list in target tenant

**Expected Results:**

- Email is delivered successfully (mail routing still works via organization relationship)
- Profile resolution fails: Target recipient cannot view sender profile from GAL
- Sender authorization fails: Email to restricted DL is rejected

**Post-Cutover Verification:**

After mailbox cutover completes:

1. Repeat profile resolution test
2. Repeat sender authorization test

**Expected Results (Post-Cutover):**

- Profile resolution works: User is now internal member with mailbox
- Sender authorization works: User is recognized as internal member

---

## Test Execution Tracking

| Test ID | Description | Status | Date | Notes |
|---------|-------------|--------|------|-------|
| TC-01 | Cross-Tenant Access Settings | | | |
| TC-02 | Organization Relationship | | | |
| TC-03 | Migration Endpoint | | | |
| TC-04 | Identity Mapping Attributes | | | |
| TC-05 | Target User License State | | | |
| TC-06 | Mail Flow Source to Target | | | |
| TC-07 | Free/Busy Pre-Migration | | | |
| TC-08 | Teams Collaboration Pre-Migration | | | |
| TC-09 | Mailbox Migration | | | |
| TC-10 | OneDrive Migration | | | |
| TC-11 | Teams Chat Migration | | | |
| TC-12 | Teams Meeting Migration | | | |
| TC-13 | Mail Flow Target to Source | | | |
| TC-14 | Mail Flow External to Source | | | |
| TC-15 | Free/Busy Post-Migration | | | |
| TC-16 | Inbound Attribution | | | |
| TC-17 | Profile Resolution | | | |
| TC-18 | Target Conversion | | | |
| TC-19 | Source B2B Enablement | | | |
| TC-20 | Reach Back Access | | | |
| TC-21 | Viva Engage Target | | | |
| TC-22 | Viva Engage Reach Back | | | |
| TC-23 | Mailbox on Hold | | | |
| TC-24 | Hybrid Identity Hard Match | | | |
| TC-25 | License Group Transition | | | |
| TC-26 | Cross-Tenant Sync Scope | | | |
| TC-27 | Connected Device Reconfiguration | | | |
| TC-28 | Target UPN Update | | | |
| TC-29 | Unverified Address Removal Timing | | | |
| TC-30 | Pre-Migration Attribution Gap | | | |

---

## Related Topics

- [Overview](index.md)
- [Setup Checklist](setup.md)
