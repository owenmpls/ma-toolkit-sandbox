# Cross-Tenant User Migration

> **Created with AI. Pending verification by a human. Use with caution.**

This document provides an overview of cross-tenant user migration using Microsoft's first-party tools and techniques within a Multi-Tenant Organization (MTO) coexistence architecture. MTO enables seamless access to applications and resources across tenants, improved collaboration in Microsoft Teams, and unified people search during the coexistence period. Users are migrated with identity, mailbox, OneDrive, Teams chat, and meetings in a single orchestrated overnight event. Users are scheduled in batches and migrated throughout the week, at a velocity determined by the customer based on operational constraints and tool limitations.

## Prerequisites

Before beginning cross-tenant user migration, the following must be in place:

**Coexistence Infrastructure (Separate Playbook)**
- Multi-Tenant Organization (MTO) established between source and target tenants
- Cross-Tenant Synchronization (XTS) configured to provision external member accounts in the target tenant
- Organization relationships configured for free/busy federation between tenants
- Distribution lists and mail-enabled security groups recreated or synchronized to target tenant (critical for mail routing and sender authorization post-migration)
- External guest accounts recreated in target tenant if OneDrive/SharePoint sharing permissions must be preserved

**Licensing**
- Cross-Tenant User Data Migration licenses procured (one-time per-user fee)
- Microsoft 365 E3/E5 or equivalent licenses in both tenants (required for Migration Orchestrator)

**Administrative Access**
- Global Administrator access in both tenants
- Exchange Administrator role in both tenants
- SharePoint Administrator access in both tenants

**Target Environment Readiness**
- Target accounts provisioned as external members via XTS
- UPN strategy determined for target accounts
- License assignment strategy defined to prevent premature mailbox provisioning

## Migration Process Overview

Cross-tenant user migration follows this high-level sequence:

### Phase 1: Coexistence Period

Users exist as external members in target and work from source during this phase.

- Configure migration infrastructure (mailbox, OneDrive, CTIM)
- Configure reverse migration infrastructure (for rollback)
- Prepare target environment (licensing, IGA integration)
- Prepare source environment (B2B license groups)
- Develop and test automation scripts
- Develop and test runbooks
- Execute end-to-end validation with test accounts

### Phase 2: Pre-Staging (T-14 to T-1)

- Run CTIM to stamp Exchange attributes on target MailUsers
- Submit migration batch for pre-staging (orchestrator requires 2 weeks lead time)
- Remove unverified addresses from target accounts
- Pre-staging synchronization runs (no user impact)

### Phase 3: Cutover Window (T-0)

- Convert target external members to internal members
- Cutover mailbox and OneDrive (overnight)
- Cutover Teams chat and meetings (if using orchestrator)
- Transition source accounts to B2B license group (prevents proxy scrubbing)
- Update source account primary SMTP address (required for B2B enablement)
- Convert remote mailboxes to mail users (hybrid source tenants only)
- Enable B2B collaboration on source accounts
- Descope source accounts from XTS to all MTO tenants
- Restore and rehome external members in other MTO tenants
- Add target accounts to XTS scope for other MTO tenants
- Restore and finalize target accounts

### Phase 4: Post-Migration

Users work from target and access source via B2B reach back.

- Reapply mailbox permissions in target tenant
- Users reconfigure devices for target tenant
- Users complete MFA registration in target (if not pre-registered)
- IGA/JML assumes lifecycle management authority
- Monitor for issues, execute rollback if needed

**Key Timing Constraints:**
- Migration Orchestrator requires batch submission at least two weeks before cutover
- Identity conversions must be coordinated with data migration cutover (same day)
- XTS descoping causes soft-delete; restoration must occur within 30 days

## Key Concepts

### Multi-Tenant Organization (MTO)

A Multi-Tenant Organization (MTO) is a group of Microsoft Entra tenants that have established mutual trust relationships. MTO provides significant benefits during the coexistence period when users exist in both tenants:

**Seamless Cross-Tenant Access:** Users can access applications and resources in the opposite tenant without additional authentication prompts. This is achieved through automatic redemption of B2B invitations rather than traditional single sign-on. Automatic redemption eliminates consent dialogs, and MFA claims are trusted across tenant boundaries. Users still authenticate with their home tenant credentials; the seamless experience comes from suppressed consent prompts and automatic invitation acceptance.

**Improved Teams Collaboration:** MTO enables enhanced Microsoft Teams experiences including real-time cross-tenant notifications, the ability to join meetings without waiting in the lobby, and seamless chat across organizational boundaries. Users appear as members rather than guests in Teams, providing full collaboration capabilities.

**Unified People Search:** Users can search for and find people across all tenants in the MTO from Outlook, Teams, and other Microsoft 365 applications. People cards display complete profile information regardless of which tenant the user belongs to.

MTO requires users to exist as external members (not external guests) in the opposite tenant. Cross-tenant synchronization provisions these external member accounts and keeps attributes synchronized during coexistence.

### Migration Orchestrator (Optional)

The Migration Orchestrator (currently in public preview) provides a single orchestration layer for coordinating the migration of multiple workloads. Administrators submit and monitor migrations from a unified interface. The orchestrator intelligently sequences workload migrations to account for dependencies and minimize failure risk.

The orchestrator coordinates migration of:

- Exchange Online mailboxes
- OneDrive for Business sites
- Teams chat (1:1 and group chats)
- Teams meetings

Shared data such as Teams channels and SharePoint sites does not migrate with users and remains in the source tenant.

Use of the orchestrator is optional. Organizations may choose to use standalone cross-tenant migration features for mailbox and OneDrive, coordinating workloads manually.

**Important:** The orchestrator does not perform Entra identity conversions. Target account conversion (external member to internal member) and source account B2B enablement (internal member to external member) must be performed out of band and coordinated with user data migration.

#### Batch Size and Velocity Considerations

The orchestrator supports individual batch submissions of up to 100 users, with a recommended planning limit of 2,000 total users per cutover window. Batches must be submitted at least two weeks before the cutover date to allow for pre-staging synchronization. These constraints may limit average migration velocity compared to standalone migration features that support larger batch sizes and shorter lead times. Organizations should verify orchestrator behavior at scale to determine velocity impacts and operational fit.

#### Reverse Migration Limitations

The Migration Orchestrator does not support reverse migration (target to source). If rollback is required after migration:

- Mailbox and OneDrive rollback must use standalone cross-tenant migration tools
- Teams chat and meetings cannot be reversed; original content remains on source tenant in modified form, but there is no path to restore the target state to source
- Reverse migration infrastructure must be configured separately using standalone tools regardless of forward migration approach

Organizations requiring robust rollback capabilities should configure bidirectional migration infrastructure using standalone tools even when using the orchestrator for forward migration.

### Identity Migration

Cross-tenant user migration involves identity transformations in both tenants to enable seamless access and coexistence. These identity conversions are performed separately from user data migration and must be coordinated with the migration schedule.

#### Target Account Preparation

Before any users are migrated, target external member accounts should be prepared with the desired target-state UPN. Changing UPNs in advance allows identity maps in all migration tools to reference the final target UPNs consistently. If UPNs are changed just-in-time as part of orchestrated migration, identity maps would need constant updates across all tools, creating operational complexity and risk.

Additionally, unverified email addresses (addresses from domains not verified in the target tenant) must be removed from target accounts before mailbox migration. If unverified addresses remain, the migration will fail. However, removing these addresses causes the same inbound attribution and sender authorization issues in the target that occur in the source post-migration. During the period between address removal and mailbox cutover, mail sent from the user's source mailbox to target recipients will not attribute to the target MailUser, breaking profile resolution and sender authorization for restricted recipients.

This address removal should be deferred as late as possible to minimize the coexistence impact. The exact timing requirement needs validation: it is likely required before the migration batch is submitted for pre-staging (at least two weeks before cutover when using the orchestrator), but it may be possible to retain the address during pre-staging and remove it only before cutover.

#### Target Tenant: External Member to Internal Member

Prior to migration, users exist in the target tenant as external members. External members (not external guests) are required for Multi-Tenant Organization (MTO) features to function correctly. Using the Entra ID "Convert to Internal User" feature, external members are converted to internal members immediately prior to user data cutover.

The GA version of this feature supports:

- Synced user conversion with preservation of UPN (no change required during conversion)
- Preservation of synced password when Password Hash Sync is in use (no reset required)
- Preservation of object ID, group memberships, and application assignments

#### Source Tenant: Internal Member to External Member

After migration, source accounts are enabled for B2B collaboration using the "Invite Internal Users to B2B" feature. This converts the internal member to an external member, linking the source account with the target identity using an alternate security identifier. Users authenticate with their target credentials and access source resources with their original permissions, group memberships, and application profiles.

**Prerequisites for B2B Enablement:**

B2B enablement requires several preparatory steps executed in sequence:

1. **Transition to B2B license group:** Move source MailUser to a license group without Exchange Online service plans. This prevents proxy scrubbing when the mailbox is converted to a MailUser.

2. **Update primary SMTP address:** The source account's primary SMTP address must be updated to a domain verified in both tenants (typically the target tenant's domain) before B2B enablement can succeed. This is the primary reason for the license group change—it prevents proxy scrubbing that would remove the required address.

3. **Convert remote mailbox to mail user (hybrid source tenants only):** For source tenants with hybrid AD integration, the remote mailbox object must be converted to a mail user in on-premises AD before B2B enablement. This conversion also defeats proxy scrubbing and satisfies the primary SMTP address requirement.

For applications that do not work with B2B collaboration, users can fall back to their source credentials, which remain intact after B2B enablement.

#### Identity Conversion Automation

Identity conversions must be coordinated with data migration timing and executed for each user in the migration batch. At scale, manual conversion through the Entra ID portal is impractical. Organizations should develop PowerShell scripts or automation to:

- Convert target external members to internal members immediately before data migration cutover
- Enable B2B collaboration on source accounts immediately after data migration completes
- Handle batch processing with error handling and logging
- Support dry-run validation before execution

The migration toolkit provides sample scripts as a starting point. Organizations should customize these scripts for their environment, integrate with existing automation frameworks, and thoroughly test before production use. Identity rollback scripts should also be developed to support reverting conversions if migration rollback is required.

### Mailbox Migration

Cross-tenant mailbox migration uses move mechanics via the Mailbox Replication Service (MRS). The source mailbox is converted to a MailUser object upon completion.

#### What Moves to Target

- All IPM (user-visible) content: email, contacts, calendar, tasks, notes
- Recoverable Items folders (Deletions, Versions, Purges)
- Some mailbox delegation permissions stored in the mailbox (if both principal and delegate migrate to target)
- Microsoft To Do tasks and lists (stored in mailbox; available in target To Do app post-migration)
- Personal Planner tasks (stored in mailbox)

#### Permissions That Require Manual Reconfiguration

Mailbox permissions are stored in Active Directory and do not migrate automatically:

- **Full Access:** Must be reapplied in target tenant after migration
- **Send As:** Must be reapplied in target tenant after migration
- **Send on Behalf:** Must be reapplied using PowerShell (publicDelegates attribute)
- **Calendar delegate permissions:** Send on Behalf for calendar delegates requires manual reconfiguration

#### What Does NOT Migrate

- Server-side Outlook inbox rules (must be recreated manually or via export/import)
- OWA email signatures (must be recreated manually in target tenant)
- Mailbox audit logs

#### What Remains in Source

- The source mailbox is converted to a MailUser with targetAddress pointing to the target mailbox
- Non-IPM substrate folders remain, including:
  - ComponentShared mailbox (Teams chat compliance records)
  - SubstrateExtension mailbox (if present due to autosplitting)
  - SubstrateHolds folder

The MailUser object enables mail routing coexistence (via targetAddress), free/busy redirection (via organization relationship), and inbound attribution for sender authorization.

### Shared and Resource Mailbox Migration

Shared mailboxes and resource mailboxes (rooms and equipment) require special handling and are migrated separately from user mailboxes. These mailbox types:

- Are **NOT** subject to Entra identity conversions (no external-to-internal conversion)
- Do **NOT** have OneDrive content to migrate
- Are **NOT** included in Migration Orchestrator batches (standalone mailbox migration only)
- Require manual post-migration configuration

#### Shared Mailboxes

Shared mailboxes migrate using standalone cross-tenant mailbox migration. Key considerations:

- Shared mailboxes must have corresponding target accounts (typically provisioned via XTS or manual creation)
- CTIM must stamp Exchange attributes on target shared mailbox accounts
- Delegate permissions (Full Access, Send As, Send on Behalf) do NOT migrate automatically and must be reapplied in target
- Export permissions before migration and reapply after using PowerShell scripts (see Mailbox Permission Migration Strategy)

**Scheduling Strategy:** Migrate shared mailboxes in the same batch as their primary delegates, or migrate the shared mailbox last after all delegates have been migrated. Since shared mailboxes are not included in orchestrator batches, they must be scheduled separately using standalone mailbox migration.

#### Resource Mailboxes (Rooms and Equipment)

Resource mailboxes migrate using standalone cross-tenant mailbox migration. Key considerations:

- Resource mailboxes must have corresponding target accounts
- CTIM must stamp Exchange attributes on target resource mailbox accounts
- Booking policies and calendar processing settings do NOT migrate automatically
- Resource delegates must be reconfigured in the target tenant
- Room lists must be recreated in the target tenant

**Post-Migration Tasks:**
1. Reconfigure booking policies (AutoAccept, AllowConflicts, BookingWindowInDays, etc.)
2. Reconfigure resource delegates
3. Recreate room lists and add resource mailboxes
4. Update room finder configurations

**Microsoft Teams Rooms:** Room mailboxes connected to Microsoft Teams Rooms devices require additional device-side configuration after migration. Teams Room migration is covered in a separate playbook section.

#### Archive Mailboxes

Archive mailboxes migrate with primary mailboxes when archive is enabled. CTIM stamps both ExchangeGUID and ArchiveGUID on target MailUser objects.

**Auto-Expanded Archives:** Mailboxes with auto-expanded archives (auxiliary archive mailboxes) present additional complexity:

- Auto-expanded archives may contain multiple auxiliary mailboxes
- All auxiliary archive mailboxes must migrate with the primary archive
- Migration of auto-expanded archives should be validated with test accounts before production migration
- Monitor migration status carefully; auto-expanded archive migration may take longer than standard archives

**Verification:** After migration, verify archive access and ensure all archive content is accessible in the target tenant.

#### Mailbox Permission Migration Strategy

Since mailbox permissions (Full Access, Send As, Send on Behalf) do not migrate automatically, organizations must plan for post-migration permission reconfiguration:

**Pre-Migration:**
1. Export all mailbox permissions from source tenant using PowerShell
2. Document which permissions involve users being migrated vs. users remaining in source
3. Map source user identities to target user identities

**Post-Migration:**
1. Reapply permissions in target tenant using PowerShell scripts
2. For permissions involving delegates who also migrated, use target UPNs
3. For permissions involving users not migrated, determine if cross-tenant access is needed

**Permissions Requiring Special Handling:**
- Permissions granted to distribution lists: Recreate DLs in target and reapply permissions
- Permissions granted to users not being migrated: May require cross-tenant B2B access or alternative solution
- Folder-level permissions: Validate after migration and reapply as needed

### OneDrive Migration

Cross-tenant OneDrive migration uses move mechanics. Content moves to the target tenant while a redirect site is placed at the original URL in the source. The redirect enables end-user link continuity for bookmarks and shared links.

**Content Stored in OneDrive That Migrates:**
- Personal files and folders
- Personal OneNote notebooks
- Teams meeting recordings (stored in user's OneDrive); transcripts and metadata may not be available in target
- Stream videos (now stored in OneDrive/SharePoint)
- Microsoft Whiteboard files (stored in OneDrive since June 2022)
- Microsoft Lists (personal lists stored in OneDrive)
- Loop components stored in OneDrive

**OneDrive Content Limitations:**

- **Whiteboards created before June 2022:** Older whiteboard files stored in Azure blob storage (not OneDrive) will not migrate. Microsoft is migrating these to OneDrive automatically; files not migrated to OneDrive before user migration will be lost.
- **Loop component references:** While Loop files migrate, references to Loop components embedded in chats, emails, and documents will break. Users must locate migrated components and update links manually.
- **Personal Lists for held users:** Third-party tools that copy OneDrive (required for users on hold) may not support personal Lists. Users on hold may lose personal lists and need to recreate them manually via reach back.
- **External sharing permissions:** Sharing permissions granted to external guests can migrate if the external guest accounts are recreated in the target tenant prior to migration. This is a prerequisite similar to distribution lists and security groups—external guests must exist in target for permissions to resolve correctly after migration.

### Teams Chat and Meetings Migration

Teams chat and meetings are migrated via the Migration Orchestrator (currently in preview).

- 1:1 chats and group chats migrate to the target
- Meetings organized by the user are canceled and rescheduled in the target
- Teams Meeting migration depends on successful mailbox migration

### Cross-Tenant Identity Mapping

Cross-Tenant Identity Mapping (CTIM) automates the process of stamping required Exchange attributes on target MailUser objects prior to migration. CTIM is required for orchestrated migration and optional for standalone cross-tenant mailbox migration.

CTIM automatically configures:

- ExchangeGUID
- ArchiveGUID (if archive-enabled)
- LegacyExchangeDN as X500 proxy address
- All necessary X500 proxy addresses from source

Target users must not have licenses applied before CTIM runs. If licensed before ExchangeGUID is stamped, the target user receives a mailbox instead of remaining a MailUser, which breaks migration.

### Email Coexistence

#### Mail Routing

The MailUser's targetAddress routes inbound mail to the target mailbox. Transport rules, security, and compliance features run in each tenant the mail flows through.

#### Free/Busy Lookups

Organization relationships between tenants enable free/busy lookups. The MailUser's targetAddress redirects free/busy requests to the mailbox location. This provides seamless calendar visibility in both directions:

- Pre-migration: Target users see source mailbox availability
- Post-migration: Source users see target mailbox availability

Free/busy lookups do not chase multiple redirects. The targetAddress must point directly to the mailbox location.

#### Inbound Attribution and Sender Authorization

When a migrated user sends mail from the target tenant to recipients in the source tenant, Exchange attempts to attribute the message to the source MailUser. Successful attribution enables:

- Profile resolution: Recipients can view the sender's profile from the local GAL
- Sender authorization: The user can send to restricted distribution lists and other recipients with sender restrictions

Attribution requires the target email address to be present in the source MailUser's proxy addresses. If EXO licenses are restored on the source MailUser, proxy addresses are scrubbed to remove unverified domains, breaking attribution and sender authorization.

### MTO External Member Rehoming

When source and target tenants are part of a larger Multi-Tenant Organization with additional tenants, migrating users have external member accounts in those other tenants that must be rehomed. This ensures users retain access to resources in other MTO tenants from their new target account.

**The Problem:** When a user migrates from source to target, their external member accounts in other MTO tenants still point to their source identity. After migration, users authenticate with their target credentials, but their external member accounts in other tenants are linked to the wrong identity.

**The Solution:** A custom scripted process rehomes external members in other MTO tenants using the same identity conversion features used for source and target account management:

1. **Convert External to Internal:** Use "Convert to Internal User" to convert the external member (linked to source) to an internal member in the other tenant
2. **Update Email Address:** Update the internal member's email address to match the target account UPN
3. **Invite Internal to B2B:** Use "Invite Internal Users to B2B" to convert the internal member back to an external member linked to the target identity

This process preserves the object ID, group memberships, and application assignments in the other tenant while relinking the account to the user's new target identity.

#### Coordination with XTS

Rehoming must be coordinated with Cross-Tenant Synchronization scoping changes:

1. **Source XTS Descoping:** When the user is migrated, remove them from source tenant's XTS scope to other tenants. This triggers soft-delete of the external member in other tenants.
2. **Restore and Rehome:** Restore the soft-deleted external member and execute the rehoming script (convert to internal, update email, invite to B2B).
3. **Target XTS Scoping:** Add the user to target tenant's XTS scope to other tenants. XTS will match the rehomed account based on alternateSecurityIdentifier, avoiding duplicate provisioning.

**Critical Timing:** The entire sequence must complete within 30 days (soft-delete retention period). Rehoming should be orchestrated as part of the migration batch process to avoid access disruption in other MTO tenants.

#### Automation Requirements

At scale, manual rehoming is impractical. Organizations should develop PowerShell scripts to:

- Identify external members in other MTO tenants that correspond to migrating users
- Execute the convert-update-invite sequence for each affected account
- Handle batch processing with error handling and logging
- Coordinate with XTS scoping changes

### Hybrid Identity Integration

Organizations managing identities on-premises link target cloud accounts with corresponding target AD accounts via Entra Connect.

The process involves:

1. Provisioning accounts in target AD, initially excluded from Entra Connect sync scope
2. Deriving immutable ID from objectGUID or msDS-ConsistencyGuid (base64 encoded)
3. Assigning immutable ID to corresponding cloud accounts
4. Moving AD accounts into Entra Connect scope for hard match

Soft match based on email address only works after conversion to internal member. Soft matching is designed to link on-premises users with cloud-created accounts and may not function correctly against external user objects. Organizations should use hard match (immutable ID) for reliable linking of external members to on-premises AD accounts.

After linking, source of authority transitions to on-premises AD. Cross-tenant sync should be updated to avoid provisioning errors on locked accounts.

## Key Decisions

### Migration Orchestrator Usage

The Migration Orchestrator (currently in public preview) provides unified orchestration for mailbox, OneDrive, Teams chat, and Teams meeting migration. Organizations must decide whether to use the orchestrator or coordinate workloads manually using standalone migration features.

**Advantages of Migration Orchestrator:**

- Single interface for submitting and monitoring migrations across workloads
- Intelligent sequencing accounts for dependencies between workloads
- Reduced operational complexity for multi-workload coordination

**Considerations:**

- Public preview status may present stability or support limitations
- Individual batch submissions limited to 100 users; planning limit of 2,000 users per cutover window may constrain migration velocity
- Pre-staging submission requirements may extend the overall migration window
- Does not perform identity conversions; target account conversion and source B2B enablement must be coordinated separately

Organizations with straightforward requirements and tolerance for preview features may benefit from the orchestrator's unified approach. Organizations requiring higher velocity or more granular control may prefer standalone migration features with manual coordination.

### First-Party vs. Third-Party Migration Tools

First-party tools provide significant advantages:

- Move mechanics leave MailUser and redirect site in source, enabling seamless mail routing and free/busy coexistence
- No complex workarounds for pre-staging or coexistence
- Private preview available for migrating mailboxes on hold
- Best-available user experience for email and calendar coexistence

Third-party tools may be required when:

- Merging mailbox data into an existing mailbox (rare)
- Source tenant must retain a complete copy of held data for eDiscovery (divestiture scenarios)
- OneDrive sites on hold cannot use native migration (no preview exists)

### Hold Handling Strategy

Standard behavior prevents migration of mailboxes and OneDrive sites on hold. Options include:

- Private preview for mailbox holds: Migrates active content and recoverable items; substrate folders remain for eDiscovery in source
- Third-party tools for OneDrive on hold: Copy mechanics leave source site read-only; manual redirect required
- Hold removal: Clear holds prior to migration (requires careful data retention planning)

### Viva Engage Coexistence

External-to-internal conversion issues have been observed in Viva Engage as recently as 2024. Options include:

- Prohibit access to target Viva Engage network prior to migration (eliminates coexistence but avoids post-migration access issues)
- Enable coexistence with thorough testing of access patterns and required mitigations

### Migration Batch Scheduling

Users should be grouped into migration batches to minimize cross-tenant access disruption and integration breakage. Key scheduling principles:

**Minimize Cross-Tenant Mailbox Access:** Group users who share mailbox access (shared mailboxes, delegated calendars, Full Access permissions) into the same batch or adjacent batches. When delegates and mailbox owners are in different tenants, permissions must be manually reconfigured and cross-tenant B2B access may not provide equivalent functionality.

**Minimize Power Platform Breakage:** Flows and Power Apps using Office 365 connectors are tenant-bound. Group users who share Power Platform solutions together when possible. Cross-user dependencies will break regardless of scheduling, but co-migration reduces the window of disruption.

**Shared Mailbox Coordination:** Since shared mailboxes use standalone migration (not orchestrator), schedule them alongside or after their primary delegates. This ensures permissions can be reapplied with target identities.

A separate playbook section covers detailed scheduling methodology, batch sizing strategies, and scheduling templates.

### Hybrid Identity Timing

Accounts can be linked with on-premises AD at different points:

- Prior to migration (hybrid external members) for early access to on-prem apps via App Proxy
- At time of migration for simplified sequencing
- After migration with additional reconciliation steps

## Limitations and Considerations

### Public Folders

Public folder mailboxes are not covered by cross-tenant user migration and are not supported by first-party migration tools. Organizations requiring public folder migration must use third-party tools.

### M365 Group Mailboxes

M365 Group mailboxes (including Teams-connected groups) are not migrated by first-party cross-tenant migration tools. These are shared resources that require third-party tools for migration. M365 Group migration is covered in the shared data migration section of this playbook.

### Microsoft Bookings

Microsoft Bookings mailboxes cannot be migrated using first-party or third-party migration tools. Bookings calendars, staff configurations, and booking pages must be manually recreated in the target tenant after migration.

### Holds and eDiscovery

- Standard cross-tenant mailbox migration cannot migrate mailboxes on hold
- Private preview enables migration while preserving substrate folders for eDiscovery in source
- OneDrive sites on hold cannot be migrated with native tools; third-party copy tools required
- Substrate content behavior after MailUser deletion requires research and clarification with Microsoft

### Power Automate and Integration Breakage

- Flows using Office 365 Outlook or OneDrive connectors are tenant-bound and cannot be updated to access target tenant resources
- Flows must be recreated in the target tenant
- Flows owned by non-migrating users may affect many users
- Solutions must move atomically with their dependencies
- Breaking issues are unavoidable for cross-user dependencies; scheduling can only partially mitigate

Similar limitations apply to shared mailbox access, M365 group mailbox access, and any integration referencing user resources by identity or URL.

### IGA/JML Process Conflicts

Enterprise IGA and JML processes typically provision mailboxes by assigning licenses. This conflicts with cross-tenant migration requirements:

- CTIM must stamp Exchange attributes before licensing
- Premature licensing creates a mailbox instead of a MailUser
- Modification of mature IGA/JML workflows is often impractical
- Custom provisioning solutions may be required for migration

### Connected Device Experience

First-party tools use cutover mechanics that disconnect Outlook and OneDrive clients immediately. Users must reconfigure devices to connect to the target tenant.

- Users who have not read migration communications may perceive this as a breaking issue
- Custom or third-party tools can automate reconfiguration on managed Windows and Mac devices
- Mobile devices and unmanaged devices require manual reconfiguration
- Users always need to take some action to trigger or complete automation

### Viva Engage Risk

Issues with external-to-internal conversion in Viva Engage have been observed as recently as 2024:

- Viva Engage may not recognize the user state change
- Users may remain represented as B2B members, breaking access to the target network
- Administrative removal and reprovisioning may be required

Recommendations:

- Prohibit access to target Viva Engage network prior to migration where possible
- Test thoroughly if Viva Engage coexistence is required
- Test reach back access to source Viva Engage (works via deep links but not network switching as of last test)

### Pre-Migration Attribution Gap

Unverified email addresses must be removed from target MailUser objects before mailbox migration can proceed. This creates a temporary attribution gap during the pre-staging period:

- Mail sent from the source mailbox to target recipients does not attribute to the target MailUser
- Profile resolution fails for target recipients viewing the sender
- Sender authorization fails for restricted distribution lists in the target tenant

This gap begins when unverified addresses are removed (likely at batch submission for pre-staging) and ends when the mailbox is cut over and the user becomes an internal member with a licensed mailbox. The duration depends on the pre-staging window, which is at least two weeks when using the Migration Orchestrator.

Mitigation options are limited. Organizations should plan communications to affected users and consider this gap when scheduling batches that include users who frequently email restricted recipients in the target tenant.

### Proxy Scrubbing

Assigning Exchange Online licenses to MailUser objects triggers proxy scrubbing, which automatically removes proxy addresses with unverified domain suffixes. This breaks:

- Inbound attribution for mail from target to source
- Profile resolution for recipients viewing sender information
- Sender authorization for restricted distribution lists

Exchange Online is the confirmed trigger for proxy scrubbing. Other service plans (Customer Lockbox, Information Barriers, Microsoft Defender for Office 365, Microsoft Information Governance, Office 365 Advanced eDiscovery) may also trigger scrubbing if they include Exchange Online components. The complete list of triggering service plans should be validated during migration testing by reviewing service plan GUIDs and testing license assignment behavior.

### Cloud-Only Source Tenants

For cloud-only source tenants using third-party mailbox migration, B2B reach back access is not possible. Cloud mailboxes cannot be converted to MailUser objects in Exchange Online. This is typically not a major issue because:

- Private preview for holds enables first-party tools in most cases
- The limitation only applies to divestiture scenarios or mailbox merging (rare)

### Encrypted Content and Sensitivity Labels

Encrypted content behaves differently depending on the workload:

**Mailbox:** Encrypted data in the mailbox is migrated as-is. Content encrypted with Microsoft Purview Information Protection (formerly Azure RMS) remains encrypted, and users may be unable to decrypt it after migration if encryption keys remain tied to the source tenant. Super user access or rights management configuration migration may be required.

**OneDrive:** Files encrypted with sensitivity labels that use user-defined permissions cannot be migrated using cross-tenant migration. The migration will fail for sites containing such files. Before migration, encryption must be removed from affected files using the `Unlock-SPOSensitivityLabelEncryptedFile` cmdlet or by manually removing labels. After migration, labels can be reapplied using target tenant sensitivity labels.

Additionally, tenants with Service encryption using Microsoft Purview Customer Key enabled cannot use cross-tenant OneDrive migration. The migration will fail if Customer Key is enabled on the source tenant.

A separate playbook section covers sensitivity label and Microsoft Purview Information Protection migration in detail.

### Conditional Access Policy Considerations

Target tenant Conditional Access policies may require adjustment to support migrated users, particularly for dual account login scenarios.

**Device-Based CA Policies:** Organizations using device-based Conditional Access policies (requiring managed devices or blocking unmanaged device access) may prevent users from accessing source tenant resources via B2B reach back. After migration, users authenticate with target credentials, but their devices may not be recognized as managed by the source tenant's device compliance policies.

**Dual Account Fallback:** For applications that do not support B2B collaboration, users may need to fall back to their source credentials. If the source tenant has device-based CA policies, users on managed devices enrolled in the target tenant's MDM may be blocked from source tenant access.

**Recommendations:**
- Review target tenant CA policies for compatibility with migrated users
- Review source tenant CA policies for B2B access scenarios
- Consider temporary CA policy adjustments during migration window if dual account fallback is required
- Test CA policy behavior with pilot users before production migration

### MFA Re-enrollment

Multi-factor authentication (MFA) registration is tenant-specific. When users migrate to the target tenant, their MFA methods do not transfer automatically.

**Target Tenant MFA:** Users must register MFA methods in the target tenant. Options include:

- Pre-migration registration during coexistence (users register MFA in target while still working in source)
- Just-in-time registration at first target sign-in (may cause friction if not communicated)
- Temporary MFA bypass for migration window (requires careful security planning)

**Source Tenant MFA:** For B2B reach back access, users authenticate with target credentials. Source tenant MFA policies apply to B2B access. If source tenant requires MFA for B2B users, target MFA registration satisfies this requirement through MFA claim trust (configured as part of MTO cross-tenant access settings).

**Recommendations:**

- Communicate MFA registration requirements to users before migration
- Consider allowing MFA registration in target during coexistence period
- Verify MFA claim trust is configured in cross-tenant access settings
- Test B2B reach back with MFA to confirm expected behavior

### Microsoft Sway

Microsoft Sway stores data in Azure, and no migration tools are available. Sways remain accessible in the source tenant after user migration via B2B reach back.

Users who need to retain Sway content in the target tenant must take manual action:

1. Export each Sway to a Word document from the source tenant
2. Recreate the Sway in the target tenant using the exported document as a starting point
3. Expect significant editing to restore formatting

### Microsoft Forms

Microsoft Forms are not included in cross-tenant migration. Forms do not migrate to the target tenant, and B2B reach back does not provide access to forms in the source tenant after migration.

Users who need to retain form data must take manual action before losing access to their source account:

1. Log into the source tenant using a dual account or temporary access
2. Copy each form as a template and recreate it in the target tenant
3. Export response data to Excel from the source form

There is no native way to import response data back into Forms. Exported responses must be retained as Excel files or stored in another system. Third-party tools can automate form migration but also export responses as Excel files rather than restoring them as live form responses.

## Glossary

| Term | Definition |
|------|------------|
| **CTIM** | Cross-Tenant Identity Mapping. A Microsoft tool that stamps Exchange attributes (ExchangeGUID, ArchiveGUID, X500 addresses) on target MailUser objects to enable mailbox migration. |
| **External Guest** | A B2B collaboration user with UserType "Guest". Has limited permissions and does not receive MTO benefits. |
| **External Member** | A B2B collaboration user with UserType "Member". Required for MTO features. Provisioned by Cross-Tenant Synchronization. |
| **Hard Match** | The process of linking a cloud account with an on-premises AD account using immutable ID (base64-encoded objectGUID). |
| **IGA/JML** | Identity Governance and Administration / Joiner-Mover-Leaver. Enterprise systems that automate user provisioning and lifecycle management. |
| **Immutable ID** | A permanent identifier (onPremisesImmutableId) used to link Entra ID accounts with on-premises AD accounts. Typically derived from objectGUID. |
| **Internal Member** | A regular user account native to the tenant with UserType "Member". The target state for migrated users. |
| **MailUser** | An Exchange Online recipient type representing a mail-enabled user whose mailbox is external. Has proxy addresses and targetAddress for mail routing. |
| **MRS** | Mailbox Replication Service. The Exchange Online service that performs mailbox moves. |
| **MTO** | Multi-Tenant Organization. A group of Entra ID tenants with established trust relationships enabling seamless cross-tenant collaboration. |
| **Proxy Scrubbing** | The automatic removal of proxy addresses with unverified domain suffixes when Exchange Online licenses are assigned. |
| **Soft Match** | The process of linking a cloud account with an on-premises AD account using email address matching. Does not work reliably for external users. |
| **targetAddress** | The ExternalEmailAddress attribute on a MailUser that specifies where mail should be routed. |
| **XTS** | Cross-Tenant Synchronization. A Microsoft Entra ID feature that provisions and synchronizes B2B collaboration users across tenants. |

## Sources

### Microsoft Documentation

- [Migration Orchestrator Overview](https://learn.microsoft.com/en-us/microsoft-365/enterprise/migration-orchestrator-1-overview)
- [Cross-Tenant Identity Mapping (Preview)](https://learn.microsoft.com/en-us/microsoft-365/enterprise/cross-tenant-identity-mapping)
- [Cross-Tenant Mailbox Migration](https://learn.microsoft.com/en-us/microsoft-365/enterprise/cross-tenant-mailbox-migration)
- [Cross-Tenant OneDrive Migration](https://learn.microsoft.com/en-us/microsoft-365/enterprise/cross-tenant-onedrive-migration)
- [Convert External Users to Internal Users](https://learn.microsoft.com/en-us/entra/identity/users/convert-external-users-internal)
- [Invite Internal Users to B2B Collaboration](https://learn.microsoft.com/en-us/entra/external-id/invite-internal-users)
- [Set Up Viva Engage for a Multitenant Organization](https://learn.microsoft.com/en-us/viva/engage/mto-setup)

### Related Topics

- [Implementation Backlog](backlog.md)
- [Test Cases](tests.md)
