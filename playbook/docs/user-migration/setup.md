# Cross-Tenant User Migration Setup Checklist

> **Created with AI. Pending verification by a human. Use with caution.**

This document provides a sequential checklist of setup steps required for cross-tenant user migration using Microsoft's first-party tools.

## Prerequisites

Before beginning setup, confirm the following:

1. Microsoft 365 E3/E5 or equivalent licenses in both tenants
2. Cross-Tenant User Data Migration add-on licenses for each user being migrated
3. Global Administrator or appropriate delegated roles in both tenants
4. Decision on hold handling strategy (private preview enrollment if required)
5. Decision on Viva Engage coexistence approach

## Phase 1: Tenant Configuration

### 1.1 Configure Cross-Tenant Access Settings

Cross-tenant access settings establish trust between tenants for B2B collaboration, MFA, device compliance, and hybrid device join.

1. Configure organizational settings in both tenants
2. Enable automatic redemption of invitations
3. Configure MFA trust
4. Configure device compliance trust (if applicable)

**Reference:** [Configure cross-tenant access settings](https://learn.microsoft.com/en-us/entra/external-id/cross-tenant-access-settings-b2b-collaboration)

### 1.2 Configure Multi-Tenant Organization (Optional)

If MTO features are required for coexistence:

1. Create multi-tenant organization
2. Add member tenants
3. Configure MTO policies

**Reference:** [Multi-tenant organization overview](https://learn.microsoft.com/en-us/entra/identity/multi-tenant-organizations/multi-tenant-organization-overview)

### 1.3 Configure Organization Relationships for Free/Busy

Organization relationships enable calendar free/busy lookups between tenants.

1. Create organization relationship in source tenant pointing to target
2. Create organization relationship in target tenant pointing to source
3. Configure AvailabilityAddressSpace if required

**Reference:** [Organization relationships in Exchange Online](https://learn.microsoft.com/en-us/exchange/sharing/organization-relationships/organization-relationships)

### 1.4 Configure Migration Endpoints

1. Register application in source tenant for migration
2. Register application in target tenant for migration
3. Configure migration endpoints in both tenants
4. Configure organization relationships for mailbox move capability

**Reference:** [Preparing target tenant for cross-tenant migration](https://learn.microsoft.com/en-us/microsoft-365/enterprise/cross-tenant-mailbox-migration?view=o365-worldwide#prepare-the-target-destination-tenant-by-creating-the-exchange-online-migration-endpoint-and-organization-relationship)

### 1.5 Configure Cross-Tenant Synchronization

Cross-tenant synchronization provisions external members in the target tenant.

1. Configure provisioning in source tenant
2. Define attribute mappings
3. Set user scope (OU or attribute filtering)
4. Enable provisioning

**Reference:** [Configure cross-tenant synchronization](https://learn.microsoft.com/en-us/entra/identity/multi-tenant-organizations/cross-tenant-synchronization-configure)

## Phase 2: User Preparation

### 2.1 Provision External Members in Target

External members (not guests) are required for MTO features and migration.

1. Confirm cross-tenant sync is provisioning users as external members
2. Verify userType is "Member" (not "Guest")
3. Confirm users are NOT licensed (licensing breaks identity mapping)

### 2.2 Update Target UPNs

Target external member UPNs should be changed to the desired target-state UPN before any users are migrated. This allows identity maps in all migration tools to reference final target UPNs consistently, avoiding the need for constant updates if UPNs were changed just-in-time during orchestrated migration.

1. Determine target UPN naming convention
2. Update UPNs on all target external members to desired target-state values
3. Update identity maps and migration tool configurations to reference target UPNs
4. Verify UPN changes have propagated to all workloads (allow time for async propagation)

### 2.3 Run Cross-Tenant Identity Mapping

CTIM stamps required Exchange attributes on target MailUser objects.

1. Confirm target users exist as MailUsers (not mailboxes)
2. Run identity mapping
3. Verify ExchangeGUID is stamped
4. Verify ArchiveGUID is stamped (if applicable)
5. Verify LegacyExchangeDN is present as X500 proxy address
6. Verify all source X500 addresses are present

**Reference:** [Cross-Tenant Identity Mapping](https://learn.microsoft.com/en-us/microsoft-365/enterprise/cross-tenant-identity-mapping)

### 2.4 Prepare Hybrid Identities (If Applicable)

For organizations managing identities on-premises:

1. Provision accounts in target AD (excluded from Entra Connect scope)
2. Derive immutable ID for each account (see [Deriving Immutable ID](#deriving-immutable-id))
3. Assign immutable ID to corresponding cloud accounts
4. Move accounts into Entra Connect scope for hard match

### 2.5 Prepare Source Accounts for B2B Enablement

Source accounts must be prepared for conversion to external members after migration.

1. Identify EXO and EXO add-on service plans that trigger proxy scrubbing
2. Create license group with EXO/EXO add-on plans disabled (see [License Group Configuration](#license-group-configuration))
3. For synced users: Prepare on-prem attribute changes for mail user conversion (see [Remote Mailbox to Mail User Conversion](#remote-mailbox-to-mail-user-conversion))

### 2.6 Address Holds (If Applicable)

1. Identify mailboxes and OneDrive sites on hold
2. Enroll in private preview for mailbox hold migration (if required)
3. Plan third-party migration for OneDrive sites on hold
4. For holds that must be cleared: Set ElcProcessingDisabled, document holds, plan removal timing

## Phase 3: Migration Batch Configuration

### 3.1 Create Migration Batches

1. Group users into batches (max 2,000 per batch for orchestrator)
2. Schedule batches across migration windows
3. Consider dependencies (Power Automate solutions, shared resources)

**Reference:** [Preparing users for cross-tenant migration with orchestrator](https://learn.microsoft.com/en-us/microsoft-365/enterprise/migration-orchestrator-4-user-prep)

### 3.2 Remove Unverified Addresses from Target Accounts

Unverified email addresses (addresses from domains not verified in the target tenant) must be removed from target MailUser objects before mailbox migration. If unverified addresses remain, the migration will fail.

**Important:** Removing these addresses causes inbound attribution and sender authorization issues during the pre-staging period. Mail sent from the source mailbox to target recipients will not attribute to the target MailUser, breaking profile resolution and sender authorization for restricted recipients. Defer this step as late as possible to minimize the coexistence impact.

1. Identify unverified addresses on target MailUser objects (typically source domain addresses)
2. Validate timing requirement: Determine if removal is required before batch submission for pre-staging or only before cutover (see [Unverified Address Removal Timing](#unverified-address-removal-timing))
3. Remove unverified addresses from batch users at the latest possible time
4. Document the attribution gap period for affected users

### 3.3 Configure Pre-Migration Testing

1. Identify pilot users for each batch
2. Configure test batches
3. Document expected results and validation steps
4. Test unverified address removal timing to validate when removal is required

## Phase 4: Cutover Preparation

### 4.1 Prepare Target Identity Conversion

1. Document conversion sequence (immediately prior to data cutover)
2. Prepare PowerShell or Graph API scripts for bulk conversion
3. Test conversion with pilot users

**Reference:** [Convert external users to internal users](https://learn.microsoft.com/en-us/entra/identity/users/convert-external-users-internal)

### 4.2 Prepare Source B2B Enablement

1. Document B2B enablement sequence (after target conversion)
2. Prepare scripts for email address change and B2B invitation
3. Configure invitation to suppress email notifications
4. Test B2B enablement with pilot users

**Reference:** [Invite internal users to B2B collaboration](https://learn.microsoft.com/en-us/entra/external-id/invite-internal-users)

### 4.3 Prepare Device Reconfiguration

1. Prepare automation for managed Windows/Mac devices
2. Create self-service guides for mobile and unmanaged devices
3. Staff service desk for migration windows

### 4.4 Prepare Communications

1. Draft user communications with reconfiguration instructions
2. Schedule communications relative to migration windows
3. Prepare service desk scripts and escalation procedures

---

## Detailed Procedures

### Deriving Immutable ID

The immutable ID links cloud accounts with on-premises AD accounts for hard match during Entra Connect synchronization.

1. Identify the source attribute for immutable ID in your Entra Connect configuration:
   - Default: objectGUID
   - Alternative: msDS-ConsistencyGuid (if configured as source anchor)

2. For each AD account, retrieve the attribute value:

   ```powershell
   # For objectGUID
   $user = Get-ADUser -Identity "username" -Properties objectGUID
   $immutableId = [System.Convert]::ToBase64String($user.objectGUID.ToByteArray())

   # For msDS-ConsistencyGuid
   $user = Get-ADUser -Identity "username" -Properties "msDS-ConsistencyGuid"
   $immutableId = [System.Convert]::ToBase64String($user."msDS-ConsistencyGuid")
   ```

3. Assign the immutable ID to the corresponding cloud account:

   ```powershell
   Set-MgUser -UserId "user@domain.com" -OnPremisesImmutableId $immutableId
   ```

4. Move the AD account into Entra Connect sync scope and wait for synchronization.

### License Group Configuration

Create a license group that excludes EXO and EXO add-on service plans to prevent proxy scrubbing on source MailUser objects.

1. Create a new security group for migrated users requiring source licenses.

2. Assign the required license SKU to the group.

3. Disable the following service plans (validate current list during testing):
   - Exchange Online (EXCHANGE_S_ENTERPRISE or equivalent)
   - Customer Lockbox
   - Graph Connectors Search with Index
   - Information Barriers
   - Information Protection for Office 365 Premium
   - Information Protection for Office 365 Standard
   - Microsoft 365 Advanced Auditing
   - Microsoft 365 Audit Platform
   - Microsoft Communications DLP
   - Microsoft Customer Key
   - Microsoft Data Investigations
   - Microsoft Defender for Office 365
   - Microsoft Information Governance
   - Microsoft ML-Based Classification
   - Microsoft MyAnalytics
   - Microsoft Records Management
   - Office 365 Advanced eDiscovery
   - Office 365 Privileged Access Management
   - Office 365 SafeDocs

4. Add users to the new license group before removing from their existing license group to avoid license revocation.

**Important:** When licenses are in grace period due to VL remap, test this process carefully to confirm license transition works without service disruption.

### Remote Mailbox to Mail User Conversion

For synced users using cross-tenant mailbox migration, convert the remote mailbox to a mail user in on-premises AD after migration.

**Option 1: Direct Attribute Manipulation**

Set the following attributes on the on-premises user object:

| Attribute | Value |
|-----------|-------|
| msExchRecipientDisplayType | 6 |
| msExchRecipientTypeDetails | 128 |
| msExchRemoteRecipientType | NULL (clear the value) |
| targetAddress | smtp:user@target.mail.onmicrosoft.com |

**Option 2: Exchange PowerShell**

```powershell
# Capture existing attributes before running (will be removed)
$user = Get-RemoteMailbox -Identity "user@domain.com"
$proxyAddresses = $user.EmailAddresses
$extensionAttributes = @{
    ExtensionAttribute1 = $user.CustomAttribute1
    # ... capture all required extension attributes
}

# Disable remote mailbox
Disable-RemoteMailbox -Identity "user@domain.com" -Confirm:$false

# Enable as mail user with target routing address
Enable-MailUser -Identity "user@domain.com" -ExternalEmailAddress "user@target.mail.onmicrosoft.com"

# Restore email addresses and extension attributes
Set-MailUser -Identity "user@domain.com" -EmailAddresses $proxyAddresses
# Restore extension attributes as needed
```

**Important:** The targetAddress must point to the coexistence routing address in the target tenant. If this is not set correctly, mail routing will break.

### Unverified Address Removal Timing

Unverified email addresses must be removed from target MailUser objects before mailbox migration can proceed. The exact timing requirement needs validation during pilot testing.

**Test Procedure:**

1. Submit a migration batch for pre-staging with unverified addresses still present on target MailUsers
2. Monitor whether pre-staging proceeds or fails
3. If pre-staging fails: Unverified addresses must be removed before batch submission
4. If pre-staging succeeds: Attempt cutover with unverified addresses still present
5. If cutover fails: Unverified addresses can remain during pre-staging but must be removed before cutover

**Expected Result:** Removal is likely required before batch submission for pre-staging, but this should be validated.

**Impact Assessment:**

The period between address removal and cutover completion is the attribution gap. During this period:

- Mail from source mailbox to target recipients does not attribute to the target MailUser
- Profile resolution fails for target recipients viewing the sender
- Sender authorization fails for restricted recipients in target tenant

Document the expected gap duration for each batch based on the pre-staging window length.

### B2B Enablement Email Address Change

Before B2B enablement, both the mail attribute and primary SMTP address must be changed to match a valid target email address.

**For Synced Users (On-Premises):**

```powershell
# Change primary SMTP to target address
Set-ADUser -Identity "username" -Replace @{
    mail = "user@target.com"
    proxyAddresses = @("SMTP:user@target.com", "smtp:user@source.com")
}

# Wait for Entra Connect sync before proceeding with B2B enablement
```

**For Cloud Users (Exchange Online):**

```powershell
Set-MailUser -Identity "user@source.com" -WindowsEmailAddress "user@target.com"
```

**Important:** Immediately revert the primary SMTP address to the original value after B2B enablement to avoid breaking applications that depend on the source email address for profile mapping.

---

## Related Topics

- [Overview](index.md)
- [Test Cases](tests.md)
