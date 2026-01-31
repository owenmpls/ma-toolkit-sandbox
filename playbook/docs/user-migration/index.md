# Cross-Tenant User Migration

> **Created with AI. Pending verification by a human. Use with caution.**

This document provides an overview of cross-tenant user migration using Microsoft's first-party tools and techniques within a Multi-Tenant Organization (MTO) coexistence architecture. MTO enables seamless single sign-on access to applications and resources across tenants, improved collaboration in Microsoft Teams, and unified people search during the coexistence period. Users are migrated with identity, mailbox, OneDrive, Teams chat, and meetings in a single orchestrated overnight event. Users are scheduled in batches and migrated throughout the week, at a velocity determined by the customer based on operational constraints and tool limitations.

## Key Concepts

### Multi-Tenant Organization (MTO)

A Multi-Tenant Organization (MTO) is a group of Microsoft Entra tenants that have established mutual trust relationships. MTO provides significant benefits during the coexistence period when users exist in both tenants:

**Single Sign-On Across Tenants:** Users can access applications and resources in the opposite tenant without additional authentication prompts. Automatic redemption of B2B invitations eliminates consent dialogs, and MFA claims are trusted across tenant boundaries.

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

The orchestrator supports batching of up to 2,000 users per batch. This limit, combined with the need to submit batches for pre-staging several days before cutover, may constrain average migration velocity. Organizations should verify orchestrator behavior at scale to determine impacts to velocity and whether techniques are available to work around the batching limit. Use of the orchestrator may result in slower average migration velocity compared to standalone migration features with larger batch sizes.

### Identity Migration

Cross-tenant user migration involves identity transformations in both tenants to enable seamless access and coexistence. These identity conversions are performed separately from user data migration and must be coordinated with the migration schedule.

#### Target Account Preparation

Before any users are migrated, target external member accounts should be prepared with the desired target-state UPN. Changing UPNs in advance allows identity maps in all migration tools to reference the final target UPNs consistently. If UPNs are changed just-in-time as part of orchestrated migration, identity maps would need constant updates across all tools, creating operational complexity and risk.

Additionally, unverified email addresses (addresses from domains not verified in the target tenant) must be removed from target accounts before mailbox migration. If unverified addresses remain, the migration will fail. However, removing these addresses causes the same inbound attribution and sender authorization issues in the target that occur in the source post-migration. During the period between address removal and mailbox cutover, mail sent from the user's source mailbox to target recipients will not attribute to the target MailUser, breaking profile resolution and sender authorization for restricted recipients.

This address removal should be deferred as late as possible to minimize the coexistence impact. The exact timing requirement needs validation: it is likely required before the migration batch is submitted for pre-staging (typically several days before cutover), but it may be possible to retain the address during pre-staging and remove it only before cutover.

#### Target Tenant: External Member to Internal Member

Prior to migration, users exist in the target tenant as external members. External members (not external guests) are required for Multi-Tenant Organization (MTO) features to function correctly. Using the Entra ID "Convert to Internal User" feature, external members are converted to internal members immediately prior to user data cutover.

The GA version of this feature supports:

- Synced user conversion with preservation of UPN (no change required during conversion)
- Preservation of synced password when Password Hash Sync is in use (no reset required)
- Preservation of object ID, group memberships, and application assignments

#### Source Tenant: Internal Member to External Member

After migration, source accounts are enabled for B2B collaboration using the "Invite Internal Users to B2B" feature. This converts the internal member to an external member, linking the source account with the target identity using an alternate security identifier. Users authenticate with their target credentials and access source resources with their original permissions, group memberships, and application profiles.

For applications that do not work with B2B collaboration, users can fall back to their source credentials, which remain intact after B2B enablement.

### Mailbox Migration

Cross-tenant mailbox migration uses move mechanics via the Mailbox Replication Service (MRS). The source mailbox is converted to a MailUser object upon completion.

#### What Moves to Target

- All IPM (user-visible) content: email, contacts, calendar, tasks, notes
- Recoverable Items folders (Deletions, Versions, Purges)

#### What Remains in Source

- The source mailbox is converted to a MailUser with targetAddress pointing to the target mailbox
- Non-IPM substrate folders remain, including:
  - ComponentShared mailbox (Teams chat compliance records)
  - SubstrateExtension mailbox (if present due to autosplitting)
  - SubstrateHolds folder

The MailUser object enables mail routing coexistence (via targetAddress), free/busy redirection (via organization relationship), and inbound attribution for sender authorization.

### OneDrive Migration

Cross-tenant OneDrive migration uses move mechanics. Content moves to the target tenant while a redirect site is placed at the original URL in the source. The redirect enables end-user link continuity for bookmarks and shared links.

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

### Hybrid Identity Integration

Organizations managing identities on-premises link target cloud accounts with corresponding target AD accounts via Entra Connect.

The process involves:

1. Provisioning accounts in target AD, initially excluded from Entra Connect sync scope
2. Deriving immutable ID from objectGUID or msDS-ConsistencyGuid (base64 encoded)
3. Assigning immutable ID to corresponding cloud accounts
4. Moving AD accounts into Entra Connect scope for hard match

Soft match based on email address only works after conversion to internal member. Entra ID does not soft match against external user objects.

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
- Batch size limit of 2,000 users may constrain migration velocity
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

### Hybrid Identity Timing

Accounts can be linked with on-premises AD at different points:

- Prior to migration (hybrid external members) for early access to on-prem apps via App Proxy
- At time of migration for simplified sequencing
- After migration with additional reconciliation steps

## Limitations and Considerations

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

This gap begins when unverified addresses are removed (likely at batch submission for pre-staging) and ends when the mailbox is cut over and the user becomes an internal member with a licensed mailbox. The duration depends on the pre-staging window, which may be several days.

Mitigation options are limited. Organizations should plan communications to affected users and consider this gap when scheduling batches that include users who frequently email restricted recipients in the target tenant.

### Proxy Scrubbing

Restoring EXO or EXO add-on service plans on source MailUser objects triggers proxy scrubbing, removing unverified domain addresses. This breaks:

- Inbound attribution for mail from target to source
- Profile resolution for recipients viewing sender information
- Sender authorization for restricted distribution lists

Service plans that trigger proxy scrubbing include: Exchange Online, Customer Lockbox, Information Barriers, Microsoft Defender for Office 365, Microsoft Information Governance, Office 365 Advanced eDiscovery, and others. The complete list should be validated during migration testing.

### Cloud-Only Source Tenants

For cloud-only source tenants using third-party mailbox migration, B2B reach back access is not possible. Cloud mailboxes cannot be converted to mail users in Exchange Online. This is typically not a major issue because:

- Private preview for holds enables first-party tools in most cases
- The limitation only applies to divestiture scenarios or mailbox merging (rare)

### Encrypted Content and Sensitivity Labels

Encrypted content behaves differently depending on the workload:

**Mailbox:** Encrypted data in the mailbox is migrated as-is. Content encrypted with Azure RMS remains encrypted, and users may be unable to decrypt it after migration if encryption keys remain tied to the source tenant. Super user access or Azure RMS configuration migration may be required.

**OneDrive:** Files encrypted with sensitivity labels are migrated to the target as unencrypted by default. Encryption is only preserved if sensitivity labels are recreated in the target tenant with matching label IDs prior to migration. This creates significant data security risk if not addressed.

A separate playbook section covers sensitivity label and Azure RMS migration in detail.

### Microsoft Forms

Microsoft Forms are not included in cross-tenant migration. Forms do not migrate to the target tenant, and B2B reach back does not provide access to forms in the source tenant after migration.

Users who need to retain form data must take manual action before losing access to their source account:

1. Log into the source tenant using a dual account or temporary access
2. Copy each form as a template and recreate it in the target tenant
3. Export response data to Excel from the source form

There is no native way to import response data back into Forms. Exported responses must be retained as Excel files or stored in another system. Third-party tools can automate form migration but also export responses as Excel files rather than restoring them as live form responses.

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

- [Setup Checklist](setup.md)
- [Test Cases](tests.md)
