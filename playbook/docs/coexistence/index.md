# Coexistence

> **Created with AI. Pending verification by a human. Use with caution.**

## Introduction

The coexistence architecture describes the integration established between Microsoft Cloud tenants and associated on-premises systems to enable collaboration, application access, and other features across organizational boundaries. This architecture serves two primary purposes:

1. **Inter-environmental collaboration** — Connecting different organizations while they remain separated by tenant boundaries, enabling users to communicate, share resources, and access applications across tenants.

2. **Migration support** — Maintaining organizational productivity during cross-tenant migration as users, devices, and resources are moved from one environment to another.

This section provides prescriptive guidance for establishing cross-tenant coexistence using Microsoft Entra B2B collaboration as the foundational identity model. The architecture is progressive, allowing organizations to start with low-trust integration and advance to high-trust integration as organizational trust increases, without requiring rework of the underlying identity infrastructure.

## Prerequisites

Before implementing the coexistence architecture, ensure the following prerequisites are met:

| Prerequisite | Description |
|--------------|-------------|
| **Entra ID P1 licensing** | Required for cross-tenant synchronization. One P1 license per employee across the integrated tenants, plus at least one P1 license per tenant. |
| **Global Administrator or Security Administrator** | Required to configure cross-tenant access settings in both tenants. |
| **Exchange Administrator** | Required to configure organization relationships and mail flow connectors. |
| **Teams Administrator** | Required to configure external access and shared channel policies. |
| **Target architecture decisions** | Organization must have decided on the target state (durable coexistence vs. consolidation) and migration path (B2B account conversion vs. replacement). |

### Licensing Requirements

The following licensing is required for specific coexistence features:

| Feature | Licensing Requirement |
|---------|----------------------|
| **Cross-tenant synchronization** | Entra ID P1 for each synced user, plus at least one P1 license per tenant |
| **Cross-tenant access settings** | Included with Entra ID Free |
| **B2B collaboration (guests)** | Included with Entra ID Free; MAU-based billing applies at scale |
| **B2B direct connect (shared channels)** | Included with Entra ID Free |
| **Multitenant organization (MTO)** | Entra ID P1 for MTO membership |
| **Viva Engage MTO features** | Viva Engage Core Service (included with most Microsoft 365 plans) for basic features; Viva Suite or Viva Employee Communications and Communities for MTO communities and storyline announcements |
| **Conditional access policies** | Entra ID P1 for conditional access; Entra ID P2 for risk-based policies |
| **Device trust in cross-tenant access** | Intune enrollment for compliant device claims; hybrid join for hybrid Entra join claims |

## Process Overview

The coexistence architecture is implemented progressively across two trust levels:

**Low-Trust Integration**

- Establish organization relationships for free/busy sharing
- Configure Teams external access for federated chat and meetings
- Enable Teams shared channels via B2B direct connect
- Configure cross-tenant access settings for guest collaboration
- Deploy cross-tenant synchronization for unified people search
- Implement conditional access mitigations to restrict external member projection

**High-Trust Integration**

- Promote synchronized users from external guests to external members
- Establish multitenant organization (MTO) for enhanced Microsoft 365 collaboration
- Configure cross-tenant mail flow for direct routing between tenants
- Enable device trust in cross-tenant access settings
- Extend to hybrid integration for on-premises application access (when required)

**Migration Coexistence**

- Configure identity mapping for cross-tenant migration tools
- Establish group synchronization between tenants
- Implement reach-back access for migrated users
- Handle Microsoft 365 group migration with associated shared data

## Key Concepts

### B2B Collaboration as the Foundation

This architecture is prescriptive in its use of Microsoft Entra B2B collaboration, which dictates the underlying identity model and has implications for migration approach. Users are represented in partner tenants as B2B accounts (guests or external members) that are authenticated by their home tenant and authorized by the resource tenant for access to applications and data.

**Authentication and authorization flow:**

1. User attempts to access a resource in the partner (resource) tenant
2. Resource tenant redirects user to their home tenant for authentication
3. User authenticates with home tenant credentials (including MFA if required)
4. Home tenant issues a token with claims about the user's identity and authentication
5. Resource tenant evaluates its conditional access policies using the token claims
6. If trust settings allow, resource tenant accepts MFA and device claims from home tenant
7. Resource tenant authorizes access based on the user's B2B account permissions

This flow ensures users maintain a single identity and credential set while allowing each tenant to enforce its own security policies.

**Advantages over alternatives:**

This approach has several advantages over alternatives such as dual accounts or contact-based GAL synchronization:

- **Single sign-on without separate credentials** — Users authenticate once and access resources across tenants without managing tenant-specific passwords.
- **Single managed device** — Users access resources across tenants from one managed device without requiring relaxation of conditional access policy, avoiding the cost and complexity of VDI or secondary devices.
- **Multitenant organization support** — Enables seamless collaboration in Teams, OneDrive, SharePoint, and Viva Engage that approaches intra-tenant experience.
- **No dual licensing requirement** — Users are licensed in their home tenant, and those licenses are respected for cross-tenant access.

### Trust and Integration Levels

The architecture defines two discrete levels of integration that can be implemented progressively:

**Low-Trust Integration** maintains effective segmentation between entities at the expense of collaboration experience:

| Component | Purpose |
|-----------|---------|
| Free/busy sharing | Calendar availability lookups across tenants |
| Teams external access | Federated chat, calling, and meetings |
| Teams shared channels | Seamless channel collaboration via B2B direct connect |
| B2B collaboration with guests | Restricted access to explicitly shared resources |
| Cross-tenant synchronization | Unified people search with guest accounts |

In low-trust integration, users have limited default access in the partner tenant and cannot access data that hasn't been explicitly shared with them. Users are tagged as external or guests in Microsoft 365 apps to encourage caution when sharing data.

### Teams Collaboration Methods

Teams provides three distinct methods for cross-tenant collaboration, each with different identity models and use cases:

| Method | Identity Model | User Experience | Best For |
|--------|---------------|-----------------|----------|
| **External access (federation)** | No account in resource tenant | Users chat/call from home tenant; tagged as "External" | Ad-hoc communication between organizations |
| **Guest access (B2B collaboration)** | Guest account created in resource tenant | Users switch tenants to access team; tagged as "Guest" | Full team membership with standard or private channels |
| **Shared channels (B2B direct connect)** | No account in resource tenant | Users access channel from home tenant; no tenant switch | Project collaboration without full team membership |

**External access** uses Teams federation and requires only that both tenants allow federation with each other. Users remain in their home tenant context and have no access to team resources (files, apps, tabs).

**Guest access** creates a B2B guest account in the resource tenant. Users must switch tenant context to access the team and have access to team resources within guest permission boundaries.

**Shared channels** use B2B direct connect and allow users to participate in specific channels without switching tenants or having an account in the host tenant. Users can access files and apps within the shared channel but cannot access other team resources.

**High-Trust Integration** maximizes collaboration experience at the expense of segmentation:

| Component | Purpose |
|-----------|---------|
| B2B collaboration with external members | Member-level default access in partner tenant |
| Multitenant organization (MTO) | Enhanced collaboration features in Microsoft 365 |
| Cross-tenant mail flow | Direct mail routing for GAL resolution and sender authorization |
| Device trust | Cross-tenant conditional access using device compliance and hybrid join claims |
| Hybrid integration | Access to on-premises applications via Entra App Proxy with Kerberos SSO |

External members have default member permissions in the resource tenant, can discover and access broadly shared data, and are not tagged as external in most Microsoft 365 apps.

### Cross-Tenant Synchronization (XTS)

Cross-tenant synchronization (XTS) automates the provisioning and management of B2B user accounts between integrated tenants using the Entra provisioning engine. Synchronized users appear in people search throughout Microsoft 365 and are represented in Exchange Online as mail user recipient objects, enabling unified global address list (GAL) experience.

**Scoping configuration:**

The product group generally recommends group-based scoping filters for cross-tenant synchronization over attribute-based filtering, though either approach is possible. Group-based scoping offers more predictable behavior and simpler management, as membership changes immediately affect sync scope without requiring attribute modifications.

**Matching existing guests:**

If a guest account already exists in the target tenant for a user at the time synchronization is configured, XTS will match the existing account using the `alternativeSecurityId` (altSecID) attribute and assume management of the object. By default, XTS does not change the user type from guest to member during this matching process. To enforce member user type for matched accounts, update the `userType` attribute transformation in the XTS provisioning configuration to set the value explicitly rather than using the default expression.

**Key limitations:**

- **Cannot manage targetAddress** — B2B users are forward synced to Exchange Online with PSMTP as target address. For hybrid Exchange organizations, this creates free/busy lookup issues because a single domain can only route lookups to Exchange Online or on-premises Exchange, not both.
- **Cannot sync proxyAddresses** — Only the primary SMTP address is synchronized. Secondary addresses must be managed separately if required.
- **Disabled accounts don't deprovision** — When an account is disabled and later deleted in the home tenant, cross-tenant sync does not deprovision the B2B user, resulting in stale objects.

**Authorization mitigation for low-trust integration:**

Cross-tenant synchronization uses coarse-grained authorization that allows partner administrators to project member-level access at their discretion. For low-trust scenarios where only guest access should be permitted, implement conditional access policy to block external members by default:

1. Create a policy in each resource tenant
2. Under users, select "B2B collaboration member users" only
3. Scope to the external Entra organizations subject to this mitigation
4. Under grant, select "Block access"

Partners configure cross-tenant sync to provision guests. While they retain the technical ability to provision external members, those accounts would be blocked from accessing the tenant.

### Multitenant Organization (MTO)

Multitenant organization is a collection of features in Microsoft 365 and Microsoft Entra that deliver enhanced collaboration between tenants belonging to the same organization. MTO is a superset of B2B collaboration features and provides the following additional capabilities when configured with external member accounts:

- **Improved Teams collaboration** — Search for users in another tenant and start chats that route to their home account as federated chat, avoiding multiple chat threads in different tenants.
- **Visual indicators in Teams** — Prompts for users to switch to federated chat between home accounts when B2B chat is detected.
- **Viva Engage MTO features** — Multitenant storyline announcements and MTO communities (requires Viva Premium licensing).

**MTO configuration:**

- One tenant must be designated as the owner; multiple owners can be configured
- Additional tenants join as members (maximum 100 tenants; this is a soft limit)
- A tenant can only be a member of one MTO at a time
- For Viva Engage, one tenant is designated as the hub tenant for storyline announcements and MTO communities

MTO can be configured via the Microsoft 365 admin center (guided setup) or via PowerShell/Microsoft Graph (more granular control). The admin center setup also creates organization relationships, cross-tenant sync configurations, and cross-tenant access settings.

### Free/Busy Sharing Architecture

Free/busy lookups in Exchange Online use organization relationships to establish trust and autodiscover to locate the target mailbox. Understanding the lookup flow is essential for troubleshooting, particularly in hybrid environments.

**Lookup process:**

1. User creates a meeting and adds an attendee from the partner tenant
2. Outlook queries the local organization relationship configuration for the attendee's domain
3. If an organization relationship exists with free/busy enabled, Outlook uses the configured autodiscover endpoint to locate the attendee's mailbox
4. The autodiscover response returns the EWS endpoint for the mailbox
5. Outlook queries the EWS endpoint to retrieve availability information

**Critical constraint:** Free/busy lookups do not chase multiple redirects. If a MailUser's targetAddress points to a routing address that forwards to another location, the lookup will fail. The targetAddress must point directly to the mailbox location.

**Hybrid implications:**

For organizations with Exchange hybrid deployments, free/busy lookups must be directed to the correct Exchange environment based on mailbox location:

- Cloud mailboxes → Exchange Online autodiscover endpoint
- On-premises mailboxes → On-premises autodiscover endpoint

This typically requires multiple organization relationships scoped by domain, or careful management of targetAddress values to ensure lookups route correctly.

### Cross-Tenant Access Settings Architecture

Cross-tenant access settings in Entra ID control B2B collaboration and B2B direct connect between specific partner organizations. Settings are configured separately for inbound access (what external users can do in your tenant) and outbound access (what your users can do in external tenants).

**Settings hierarchy:**

1. **Default settings** — Apply to all external organizations not explicitly configured
2. **Partner-specific settings** — Override defaults for specific organizations identified by tenant ID or domain

**Key configuration areas:**

| Area | Inbound | Outbound |
|------|---------|----------|
| **B2B collaboration** | Which external users can be invited; which apps they can access | Which users can accept invitations; which apps they can access externally |
| **B2B direct connect** | Which external users can access shared channels; which apps they can access | Which users can participate in external shared channels |
| **Trust settings** | Accept MFA, compliant device, and hybrid join claims from partner | N/A |
| **Automatic redemption** | Suppress consent prompt for inbound invitations | Suppress consent prompt for outbound invitations |

**Automatic redemption requirements:**

For automatic invitation redemption (consent prompt suppression), the setting must be enabled on both sides:

- Inbound automatic redemption in the resource tenant
- Outbound automatic redemption in the user's home tenant

If either side has not enabled the setting, users will see a consent prompt when accessing the partner tenant.

**Device trust and conditional access:**

Cross-tenant access settings can be configured to trust device claims (compliant device and hybrid Entra join) from partner tenants. When enabled, the resource tenant's conditional access policies can evaluate device compliance and hybrid join status based on claims from the user's home tenant.

This capability provides a significant advantage for MTO-based coexistence over dual account coexistence scenarios. In a dual account model, each user has a separate account in each tenant with its own device registrations. When a resource tenant's conditional access policy requires a compliant device, users must have a device enrolled and compliant in that tenant specifically—their compliant device in the home tenant does not satisfy the policy.

With MTO coexistence using cross-tenant device trust:

- Users authenticate with their home tenant account
- The home tenant provides device compliance claims in the authentication token
- The resource tenant accepts these claims based on cross-tenant access settings
- Conditional access policies requiring compliant devices are satisfied

This eliminates the need for dual device enrollment, VDI solutions, or relaxation of conditional access requirements to support cross-tenant access.

### Cross-Tenant Mail Flow

Direct mail flow between tenants provides:

- **GAL resolution for cross-tenant senders** — Recipients can view profile information for senders from the partner tenant
- **Distribution group participation** — Users can participate in distribution groups in the partner tenant without opening those groups to internet email
- **Sender authorization** — Users can email protected groups or book restricted rooms in the partner tenant based on assigned permissions

Mail flow is configured through partner connectors:

- **Outbound connectors** — Scoped for MOERA, coex routing domain, and primary SMTP domains; smart hosts target Exchange Online Protection in the partner tenant
- **Inbound connectors** — Must include partner tenant MOERA in TrustedOrganizations parameter for proper message attribution

**Inbound resolution** requires a unified GAL achieved through cross-tenant synchronization. When mail from a partner tenant is attributed to an inbound connector, Exchange resolves the sender address to a local mail user recipient, enabling sender authorization and profile display.

### External Member Access Implications

External members have significantly broader default access than guests:

- Use organization-wide sharing links in OneDrive and SharePoint, including links created before their account existed
- Hold implicit membership in the "Everyone except external users" group in SharePoint
- View all directory information
- Discover and join public teams and Microsoft 365 groups
- Decrypt sensitivity-labeled content secured for tenant members (online apps only)
- Not tagged with "(Guest)" in Microsoft Teams

**Data segmentation concern:** High-trust integration using external members has similar data privacy implications as tenant consolidation. External members can search for and access any content shared via org-wide links or the "Everyone except external users" group.

### Unified Domain Branding

A domain can only be verified and used in one Microsoft 365 tenant at a time. When users need to send email for a domain verified in another tenant, email address rewriting is required.

**Address rewriting architecture:**

1. Outbound email routes through an email gateway capable of header substitution
2. Gateway replaces sender and recipient addresses with the desired domain branding
3. SPF records for the shared domain must authorize the gateway as a sender

**Rewriting options:**

- **Domain substitution** — Requires globally unique prefixes across all environments sharing the domain
- **Address-based substitution** — Requires an address map maintained as addresses change

**Known limitations:**

- Meeting response tracking fails because rewriting solutions typically don't update addresses in MIME message body content
- B2B invites sent to rewritten addresses fail acceptance because the address doesn't exist on the Entra ID account
- When inbound mail routes through the owning tenant and forwards to home tenant, rewritten addresses cannot be resolved to GAL entries

**Alternative approach:** Verify a subdomain of the desired domain in the target tenant (e.g., `fabrikam.contoso.com` when `contoso.com` is verified elsewhere). This is simpler but less ideal for branding.

### Hybrid External Members

B2B external member accounts can be joined to on-premises AD user accounts, creating hybrid external members that are synced users enabled for external authentication. This topology is required for:

- **Kerberos SSO via App Proxy** — On-premises applications published through Entra App Proxy that rely on Kerberos Constrained Delegation
- **Exchange hybrid mail flow** — When unified domain branding uses addresses assigned in the resource tenant
- **On-premises group membership** — When security or distribution groups must remain managed in on-premises AD
- **Migration preparation** — Provisioning AD accounts in preparation for consolidation

**Important:** Hybrid external members are incompatible with cross-tenant synchronization. An alternative identity synchronization solution is required, such as Microsoft Identity Manager (MIM) or third-party AD migration tools.

## Key Decisions

| Decision | Considerations |
|----------|----------------|
| **Migration path** | B2B account conversion (GA, preserves permissions/metadata/chat but has known Viva Engage issues) vs. B2B account replacement (more supportable but loses permissions/metadata/chat). |
| **Trust level progression** | Start with low-trust and advance to high-trust only after security assessment and policy alignment, or proceed directly to high-trust if organizational trust is established. |
| **MTO ownership** | Which tenant will be the MTO owner. Typically the primary or corporate tenant, but may be complicated by specific scenarios. |
| **Viva Engage hub tenant** | Which tenant hosts storyline announcements and MTO communities. Can differ from MTO owner. |
| **Synchronization topology** | One-way vs. two-way cross-tenant synchronization based on collaboration requirements and tenant sizes. |
| **Unified domain branding** | Whether address rewriting is required, and if so, the rewriting approach (domain vs. address-based substitution). |
| **Hybrid integration scope** | Whether on-premises application access is required for cross-tenant users, driving the need for hybrid external members. |

## Limitations and Considerations

### Cross-Tenant Synchronization Limitations

- Target address management is not supported, creating free/busy complications for hybrid Exchange organizations
- Proxy addresses are not synchronized; only the primary SMTP address flows to the target tenant
- Disabled and deleted accounts do not automatically deprovision B2B users
- Synchronized users may take up to 24 hours to be available in Teams and SharePoint people search

### External Member Limitations

- Power Platform solutions are not accessible to external members
- Power BI assets require a license assigned in the resource tenant
- Dynamic groups cannot reliably differentiate external members from internal members in filters
- Existing guest restrictions in SharePoint persist after promotion to external member (resolved for new conversions as of August 2024)

### MTO Limitations

- A tenant can only participate in one MTO at a time
- MTO cannot span different cloud environments (e.g., commercial to GCC High)
- Microsoft Teams Rooms (MTR) is not supported

### B2B Account Conversion Limitations

- Known issues with Viva Engage (last verified 2024; may be resolved with recent feature updates)
- Some applications may not fully support converted accounts

### Address Rewriting Limitations

- Meeting response tracking does not work properly
- B2B invitation acceptance fails for rewritten addresses
- GAL resolution fails for rewritten addresses in forwarded mail scenarios

## Glossary

| Term | Definition |
|------|------------|
| **B2B collaboration** | Microsoft Entra feature enabling users from one tenant to access resources in another tenant using guest or external member accounts. |
| **B2B direct connect** | Microsoft Entra feature enabling users to access resources in another tenant without a local account, used for Teams shared channels. |
| **Coex routing domain** | The `tenant.mail.onmicrosoft.com` domain used for mail routing during coexistence and migration. |
| **Cross-tenant access settings** | Entra ID configuration controlling inbound and outbound B2B collaboration and direct connect with specific partner organizations. |
| **CTIM** | Cross-Tenant Identity Mapping; a Microsoft tool that stamps Exchange attributes (ExchangeGUID, ArchiveGUID, X500 addresses) on target MailUser objects to enable cross-tenant mailbox migration. |
| **External guest** | A B2B account with guest-level permissions, subject to guest restrictions in Microsoft 365 apps. |
| **External member** | A B2B account with member-level permissions, not subject to guest restrictions. |
| **Hybrid external member** | A B2B external member account joined to an on-premises AD user account via directory synchronization. |
| **MailUser** | An Exchange Online recipient type representing a mail-enabled user whose mailbox is external. Has proxy addresses and targetAddress for mail routing. |
| **MOERA** | Microsoft Online Email Routing Address; the `tenant.onmicrosoft.com` domain. |
| **MTO** | Multitenant organization; a collection of Entra ID and Microsoft 365 features for enhanced collaboration between tenants. |
| **Organization relationship** | Exchange Online configuration enabling free/busy sharing with external organizations. Free/busy lookups do not chase multiple redirects; targetAddress must point directly to the mailbox location. |
| **Target address** | The `ExternalEmailAddress` attribute on mail user objects specifying the forwarding destination. |
| **XTS** | Cross-Tenant Synchronization; Entra ID feature automating provisioning of B2B accounts between tenants using the provisioning engine. |

## Sources

- [Multitenant organization overview - Microsoft Entra](https://learn.microsoft.com/en-us/entra/identity/multi-tenant-organizations/multi-tenant-organization-overview)
- [Cross-tenant synchronization overview - Microsoft Entra](https://learn.microsoft.com/en-us/entra/identity/multi-tenant-organizations/cross-tenant-synchronization-overview)
- [Cross-tenant access overview - Microsoft Entra External ID](https://learn.microsoft.com/en-us/entra/external-id/cross-tenant-access-overview)
- [Plan for multitenant organizations in Microsoft 365](https://learn.microsoft.com/en-us/microsoft-365/enterprise/plan-multi-tenant-org-overview)
- [Set up a multitenant org in Microsoft 365](https://learn.microsoft.com/en-us/microsoft-365/enterprise/set-up-multi-tenant-org)
- [Configure cross-tenant synchronization - Microsoft Entra](https://learn.microsoft.com/en-us/entra/identity/multi-tenant-organizations/cross-tenant-synchronization-configure)
- [Organization relationships in Exchange Online](https://learn.microsoft.com/en-us/exchange/sharing/organization-relationships/organization-relationships)
- [Set up connectors for secure mail flow with a partner organization](https://learn.microsoft.com/en-us/exchange/mail-flow-best-practices/use-connectors-to-configure-mail-flow/set-up-connectors-for-secure-mail-flow-with-a-partner)
- [Shared channels in Microsoft Teams](https://learn.microsoft.com/en-us/microsoftteams/shared-channels)
- [Manage external meetings and chat in Teams](https://learn.microsoft.com/en-us/microsoftteams/trusted-organizations-external-meetings-chat)
- [B2B collaboration overview - Microsoft Entra External ID](https://learn.microsoft.com/en-us/entra/external-id/what-is-b2b)
- [B2B direct connect overview - Microsoft Entra External ID](https://learn.microsoft.com/en-us/entra/external-id/b2b-direct-connect-overview)
- [Migration orchestrator overview - Microsoft 365](https://learn.microsoft.com/en-us/microsoft-365/enterprise/migration-orchestrator-1-overview)

## Related Topics

- [Coexistence Implementation Backlog](backlog.md)
- [Coexistence Test Cases](tests.md)
- [Cross-Tenant User Migration](../user-migration/index.md) — Identity conversions, mailbox migration, OneDrive migration, Teams chat and meetings migration, reach-back access
- Shared Data Migration (cross-reference to shared data migration section)
