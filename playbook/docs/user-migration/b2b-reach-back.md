# B2B Reach-Back Access

B2B reach-back access enables migrated users to continue accessing source tenant resources via SSO, retaining their original permissions, group memberships, and application profiles. This procedure is performed after mailbox migration is complete. The primary technical challenge is satisfying the requirement for a valid target email address on the source user, which requires disabling proxy scrubbing behavior in Entra ID.

!!! note
    This topic focuses on hybrid source topologies where users are synced from on-premises AD. While most of this content applies to cloud-only source topologies, that scenario has not been fully vetted and there may be meaningful differences in user experience—for example, retention of source credentials may not be possible.

## Overview

### Hybrid External Member

When a source account is enabled for B2B reach-back access, it is converted to what this playbook refers to as a *hybrid external member*. This is an external member that is synced from on-premises AD and retains a standard-format UPN, rather than the typical `#EXT#` UPN format used for standard external guests and members in Entra ID. The account is linked to the target identity using an alternate security identifier, allowing users to authenticate with their target credentials and be presented to source tenant applications as the original source identity.

Ongoing management for source identities is typically unchanged at this stage of integration. B2B enablement leaves source credentials intact. Users can fall back to their source credentials for any application that does not work with B2B collaboration. Whether signing in with source or target credentials, users are presented to apps as the same source identity.

### Benefits

- Access to source tenant resources with original permissions, group memberships, and application profiles
- Single MFA challenge on the target identity when cross-tenant access settings are configured to trust MFA
- Access from managed devices that have been migrated to the target tenant with the user, satisfying device-based conditional access policies in either tenant
- Compatibility with most applications without change, including SaaS and custom applications that rely on Entra ID, on-premises apps published through Entra application proxy, and on-premises apps that rely on Kerberos Constrained Delegation
- Compatibility with a growing number of first-party applications, including Teams and SharePoint, with continued improvement as Microsoft invests in multitenant organization (MTO) capabilities
- When source and target tenants belong to a common MTO, access to MTO features in Teams, most notably chat from people search that routes to user home accounts without requiring tenant switching
- Fallback to source credentials for any application that does not work with B2B collaboration (e.g., Power Platform administration, Forms authoring and management)

## Prerequisites

Before enabling B2B reach-back access for a user, the following conditions must be met:

- **Mailbox migration is complete.** The user's mailbox must be migrated to the target tenant before B2B reach-back access can be enabled. This topic assumes mailbox migration was performed using cross-tenant mailbox migration (XT migration). For environments using third-party mailbox migration tools, see [Third-Party Mailbox Migration](#third-party-mailbox-migration).

- **Cross-tenant access settings are configured.** Cross-tenant access settings should be configured between source and target tenants to enable automatic invitation redemption, MFA trust, and device compliance trust. This improves the MFA experience as noted in Benefits and avoids bypass of device-based conditional access policies when using B2B from devices managed in the target.

- **Target user is converted from external member to internal member (if applicable).** If B2B collaboration was used to provide access to the target tenant prior to migration, the target user must be converted from an external member to an internal member before enabling B2B reach-back access in the source. If this order of operations is reversed, automatic redemption will not work and the user will need to manually accept the B2B invitation before they can use reach-back access to the source.

## Proxy Scrubbing

To enable B2B reach-back access, the source user must have a valid target email address assigned as their primary SMTP address. This is typically the primary SMTP address from the target tenant (e.g., `user@target.com`). Because this domain cannot be verified in the source tenant, Entra ID will normally remove it through a process commonly referred to as proxy scrubbing, proxy calc, or email address sanitization.

### Conditions That Trigger Proxy Scrubbing

Entra ID removes unverified domain addresses from users under any of the following conditions:

- **User has a mailbox in Exchange Online.** This is the case when the mailbox has been copied to the target using third-party tools, which leave an active mailbox in the source configured to forward email to the target.

- **User is configured as a remote mailbox in on-premises AD and synced to Entra ID.** This is typically the case when the mailbox has been moved to the target using XT mailbox migration.

- **User is licensed with an Exchange Online or Exchange Online add-on service plan.** This triggers proxy scrubbing regardless of Exchange object status.

### Service Plans That Trigger Proxy Scrubbing

The following service plans trigger proxy scrubbing when assigned to a user. This list reflects Microsoft 365 E5 licensing as of June 5, 2024. Microsoft may add service plans to existing SKUs that trigger proxy scrubbing, and this should always be validated during user migration testing.

- Customer Lockbox
- Exchange Online
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
- RETIRED – Microsoft Communications Compliance
- RETIRED – Microsoft Insider Risk Management

## Preparing the Source User

Before B2B reach-back access can be enabled, the source user must be prepared by disabling service plans that trigger proxy scrubbing and converting the remote mailbox to a mail user.

### Disabling EXO Service Plans

All Exchange Online and Exchange Online add-on service plans must be disabled for the source user to prevent proxy scrubbing. The procedure varies based on the means of license assignment in the source tenant.

**Group-based licensing:** Create a new license group with the required service plan configuration. All Exchange Online and Exchange Online add-on plans must be disabled for the SKUs assigned to the new group. Add the user to the new license group before removing them from the existing license group to avoid temporary revocation of the license.

!!! warning
    When a volume licensing (VL) remap has been executed to transition licenses to the target, source licenses will be left in grace. Service plan changes on grace licenses may not be possible, and revoked licenses may not be reassignable through traditional means. In this scenario, carefully test the process of transitioning users between license groups with accounts that have grace licenses assigned to confirm the change can be made without service disruption.

**Direct assignment:** Disable service plans directly on the source user's license assignment.

### Converting Remote Mailbox to Mail User

After XT mailbox migration completes, the source user remains configured as a remote mailbox. This must be converted to a mail user to prevent proxy scrubbing.

Make the following attribute changes on-premises:

| Attribute | Value |
|-----------|-------|
| `msExchRecipientDisplayType` | `6` |
| `msExchRecipientTypeDetails` | `128` |
| `msExchRemoteRecipientType` | `NULL` |
| `targetAddress` | Coex routing address in the target (e.g., `user@target.mail.onmicrosoft.com`) |

!!! warning
    XT mailbox migration assigns the coex routing address in Exchange Online upon cutover. This target address is not overridden so long as the on-premises user is a remote mailbox. If the `targetAddress` is not updated on-premises before converting to a mail user, the conversion will override the target address in Exchange Online with the value assigned on-premises (e.g., `user@source.mail.onmicrosoft.com`), which will break mail flow.

!!! note
    This conversion can also be performed in Exchange PowerShell by running `Disable-RemoteMailbox` followed by `Enable-MailUser`. However, `Disable-RemoteMailbox` removes all Exchange attributes from the account, including email addresses and extension attributes. These values must be captured before running this command and restored to the mail user to avoid disruption to mail flow, application access, and other systems or processes that rely on these attributes.

## Enabling B2B Collaboration

After the source user has been prepared, B2B reach-back access can be enabled by assigning the target email address and inviting the user for B2B collaboration.

### Assigning the Target Email Address

The source user must have a valid target email address assigned as their primary SMTP address. This is required for the B2B invitation to succeed.

For synced users, this change must be made on-premises using Exchange PowerShell or direct attribute manipulation in AD. Both the `mail` attribute and the primary SMTP address in the `proxyAddresses` collection must be changed to match the primary SMTP address assigned to the user in the target. For Exchange-enabled objects, the `mail` attribute in Entra ID is derived from the primary SMTP address, so changing the `mail` attribute alone is not sufficient.

Allow changes to sync to Entra ID via Entra Connect before proceeding with B2B enablement.

### Inviting the Internal User

Invite the source user for B2B collaboration using the [Invite internal users to B2B collaboration](https://learn.microsoft.com/en-us/entra/external-id/invite-internal-users) feature in Entra ID.

!!! note
    Suppress email notifications when inviting users. System notifications are likely to confuse users, and reach-back access should be communicated as part of the broader communications package for the migration.

### Reverting the Primary SMTP Address

!!! warning
    Revert the primary SMTP address to the original value immediately after B2B enablement. Entra ID-integrated apps and other processes may have dependency on the original email address. Permanently changing this value to the target email address may break authentication to applications that use an email claim to map the user to the corresponding application profile, and there is high risk of other impacts in the environment.

After reverting, the target email address is typically left as a secondary SMTP address to facilitate inbound mail flow attribution for email sent by the user to the source tenant. This supports profile resolution for that email and sender authorization, allowing the user to email protected recipients in the source.

### Automatic Redemption and Order of Operations

Cross-tenant access settings should be configured to automatically redeem invitations. This allows migrated users to begin using B2B reach-back access without completing any additional steps.

If B2B collaboration was used to provide access to the target tenant prior to migration, B2B enablement in the source must be performed after the target user is converted from an external member to an internal member. If this order of operations is reversed, automatic redemption will not work and the user will need to manually accept the B2B invitation before they can use reach-back access to the source.

## Third-Party Mailbox Migration

When mailboxes are migrated using third-party tools rather than XT mailbox migration, the source mailbox must be deleted before B2B reach-back access can be enabled. This section describes the additional considerations and procedure differences for this scenario.

!!! warning
    This procedure can only be performed for users synced from on-premises AD. Cloud mailboxes cannot be converted to mail users because Exchange Online does not support mail-enablement for an existing cloud user. For cloud-only source environments, B2B reach-back access can only be enabled when using XT mailbox migration.

### Data Retention Considerations

Removing the source mailbox must be coupled with a strategy for data retention. Not all retention data can be moved to the target, and holds must be removed from the source mailbox before it can be removed. This prevents conversion to an inactive mailbox and subjects it to standard soft deletion, which results in destruction of the data after 30 days.

This may be a blocking issue for use of this technique with third-party mailbox migration for organizations with strict compliance requirements or a substantial number of users on hold.

### Hold Removal

Holds must be removed before the source mailbox can be disabled:

1. Exclude the source mailbox from any retention policies.
2. Clear all holds on the source mailbox.

!!! warning
    There is a four-hour delay after holds are removed before the source mailbox can be disabled. Performing hold removal during the user migration window will likely delay enablement of B2B reach-back access. Consider removing holds as a prerequisite prior to cutover. In this situation, set `ElcProcessingDisabled` on the source mailbox before clearing holds, which prevents the managed folder assistant from processing retention policy and purging data. This allows holds to be removed without immediate risk of data loss. More information is available at [Place a mailbox on retention hold in Exchange Online](https://learn.microsoft.com/en-us/exchange/security-and-compliance/messaging-records-management/mailbox-retention-hold).

!!! note
    For mailboxes that have `ComplianceTagHoldApplied`, special handling is required. This property is set by the managed folder assistant when a retention policy has been applied to a mailbox or when items have been labeled or tagged. It only protects the mailbox from being disabled and is never reverted to false, even when these conditions no longer apply. The property can only be cleared by product engineering through a support request, which must include a list of mailboxes to process and written authorization to make the change.

### Procedure

After holds are removed and the four-hour delay has passed:

1. Capture any Exchange attributes that must be retained, including source email addresses (`proxyAddresses`) and extension attributes. These will be removed by the `Disable-RemoteMailbox` command.
2. Run `Disable-RemoteMailbox` on the on-premises user.
3. Run `Enable-MailUser` on the user, providing the correct coex routing address as the external email address (e.g., `user@target.mail.onmicrosoft.com`).
4. Restore email addresses, extension attributes, and any other Exchange attributes that must be retained to the mail user object.
5. Allow changes to sync to Exchange Online via Entra Connect.
6. Disable EXO service plans as described in [Preparing the Source User](#preparing-the-source-user).
7. Assign the target email address and enable B2B collaboration as described in [Enabling B2B Collaboration](#enabling-b2b-collaboration).

## Implementation Backlog

### Identify Service Plans and Create License Groups for B2B Reach-Back

**Objective:** Identify service plans in the source tenant that trigger proxy scrubbing and create license groups with those service plans disabled to support B2B reach-back enablement.

**Level of Effort:** Medium

**Prerequisites:**

- License Administrator role in the source tenant
- Access to create and manage groups in the source environment (on-premises AD or Entra ID, depending on existing setup)

**Steps:**

1. Review the [list of service plans that trigger proxy scrubbing](#service-plans-that-trigger-proxy-scrubbing) documented in this topic.
2. Query assigned licenses in the source tenant to identify which SKUs are in use.
3. For each SKU in use, identify the service plans included and compare against the documented list.
4. Document any additional service plans not on the list that may trigger proxy scrubbing based on type (EXO or EXO add-on).
5. Identify all existing license groups that assign SKUs containing service plans that trigger proxy scrubbing.
6. For each relevant existing license group:
    - Create a corresponding new license group with identified EXO service plans disabled.
    - Create new groups alongside existing groups, matching the existing setup (on-premises or cloud).
    - Document the mapping between the existing license group and the new reach-back license group.
7. Compile the complete 1:1 mapping between existing license groups and new reach-back license groups. Automation will use this mapping to replace old group membership with new group membership.

**Test:** [Verify License Groups Do Not Trigger Proxy Scrubbing](#verify-license-groups-do-not-trigger-proxy-scrubbing)

**References:**

- [Product names and service plan identifiers for licensing](https://learn.microsoft.com/en-us/entra/identity/users/licensing-service-plan-reference)
- [Assign licenses to a group](https://learn.microsoft.com/en-us/entra/identity/users/licensing-groups-assign)

---

### Provision Access for B2B Reach-Back Automation

**Objective:** Provision the necessary permissions and access to execute B2B reach-back automation scripts in the source environment.

**Level of Effort:** Medium

**Prerequisites:**

- Global Administrator or Privileged Role Administrator access to register applications and grant admin consent in Entra ID
- Domain Admin or delegated access to create service accounts and assign permissions in on-premises AD

**Steps:**

**Entra ID (Service Principal with Certificate Authentication):**

1. Register an application in Entra ID for B2B reach-back automation.
2. Generate or procure a certificate and upload the public key to the application registration. Store the private key securely for use by automation.
3. Assign the following Microsoft Graph application permissions and grant admin consent:
    - `User.Invite.All` (invite users for B2B collaboration)
    - `User.ReadWrite.All` (modify user attributes)
4. Grant the service principal access to modify license group membership:
    - For cloud-based license groups: Add the service principal as an owner of each license group (both existing and reach-back versions).
    - For on-premises license groups: Group membership changes will be performed by the on-premises service account; no additional Entra ID permissions required.

**On-Premises AD (Service Account):**

1. Create a dedicated service account in on-premises AD for B2B reach-back automation.
2. Delegate the following permissions to the service account on the OU(s) containing users in scope for B2B reach-back:
    - Write `msExchRecipientDisplayType`
    - Write `msExchRecipientTypeDetails`
    - Write `msExchRemoteRecipientType`
    - Write `targetAddress`
    - Write `mail`
    - Write `proxyAddresses`
3. If using on-premises license groups, grant the service account permission to modify group membership for relevant license groups.
4. Configure secure credential storage for the service account (e.g., Windows Credential Manager, Azure Key Vault, or equivalent).
5. Document the access provisioned and any constraints on its use.

**Test:** [Verify Automation Access](#verify-automation-access)

**References:**

- [Microsoft Graph permissions reference](https://learn.microsoft.com/en-us/graph/permissions-reference)

---

### Develop B2B Reach-Back Automation

**Objective:** Develop script or M&A toolkit configuration to automate the B2B reach-back enablement process.

**Level of Effort:** High

**Prerequisites:**

- License groups for B2B reach-back have been created with documented mapping
- Access for automation has been provisioned

**Steps:**

1. Develop automation to perform the following steps for each user:
    - Remove user from existing license group and add to corresponding reach-back license group based on documented mapping.
    - Update on-premises AD attributes to convert remote mailbox to mail user and assign target email address as primary SMTP:

        | Attribute | Value |
        |-----------|-------|
        | `msExchRecipientDisplayType` | `6` |
        | `msExchRecipientTypeDetails` | `128` |
        | `msExchRemoteRecipientType` | `$null` |
        | `targetAddress` | `SMTP:<user>@<target>.mail.onmicrosoft.com` |
        | `mail` | `<user>@<target-primary-domain>` |
        | `proxyAddresses` | Replace primary SMTP with `SMTP:<user>@<target-primary-domain>`; demote original primary to secondary (`smtp:`) |

    - Wait for changes to sync to Entra ID via Entra Connect.
    - Invite source user for B2B collaboration with email notifications suppressed.
    - Update on-premises AD attributes to revert primary SMTP address:

        | Attribute | Value |
        |-----------|-------|
        | `mail` | `<original-primary-email>` |
        | `proxyAddresses` | Restore original primary SMTP; demote target email to secondary (`smtp:`) |

2. Implement error handling and logging for each step.
3. Implement support for batch processing and retry logic.

**Test:** [Verify B2B Reach-Back Automation](#verify-b2b-reach-back-automation)

**References:**

- [Invite internal users to B2B collaboration](https://learn.microsoft.com/en-us/entra/external-id/invite-internal-users)

## Tests

### Verify License Groups Do Not Trigger Proxy Scrubbing

**Objective:** Confirm that reach-back license groups have the correct service plans disabled and do not trigger proxy scrubbing.

**Prerequisites:**

- Reach-back license groups have been created per [Identify Service Plans and Create License Groups for B2B Reach-Back](#identify-service-plans-and-create-license-groups-for-b2b-reach-back)
- A test mail user exists in the source tenant that meets other criteria for B2B reach-back (converted from remote mailbox to mail user, synced from on-premises AD)

**Steps:**

1. Assign an unverified domain address (e.g., `testuser@target.com`) as the primary SMTP address on the test mail user.
2. Add the test mail user to one of the reach-back license groups.
3. Wait for license assignment to process.
4. Verify that the unverified domain address remains as the primary SMTP address and was not removed by proxy scrubbing.
5. Repeat for each reach-back license group.

**Expected Result:** The test mail user retains the unverified domain address as primary SMTP after license group assignment for all reach-back license groups.

---

### Verify Automation Access

**Objective:** Confirm that the service principal and on-premises service account can authenticate and execute required operations.

**Prerequisites:**

- Access has been provisioned per [Provision Access for B2B Reach-Back Automation](#provision-access-for-b2b-reach-back-automation)

**Steps:**

1. Authenticate to Microsoft Graph using the service principal with certificate authentication.
2. Verify the service principal can read and modify group membership for license groups:
    - For cloud-based license groups, verify the service principal is listed as an owner and can add/remove members.
    - For on-premises license groups, verify the on-premises service account can modify group membership.
3. Verify the service principal can invoke the B2B invitation API.
4. Verify the on-premises service account can authenticate and modify user attributes (`msExchRecipientDisplayType`, `msExchRecipientTypeDetails`, `msExchRemoteRecipientType`, `targetAddress`, `mail`, `proxyAddresses`) on a test user.

**Expected Result:** All authentication and authorization checks pass. The service principal and on-premises service account can execute all required operations.

---

### Verify B2B Reach-Back Automation

**Objective:** Confirm that the automation successfully processes a test user through all steps.

**Prerequisites:**

- Automation has been developed per [Develop B2B Reach-Back Automation](#develop-b2b-reach-back-automation)
- A test user exists in the source tenant with a migrated mailbox in the target tenant

**Steps:**

1. Execute the automation against the test user.
2. Verify the test user was removed from the existing license group and added to the corresponding reach-back license group.
3. Verify the on-premises AD attributes were updated to convert the remote mailbox to a mail user:
    - `msExchRecipientDisplayType` = `6`
    - `msExchRecipientTypeDetails` = `128`
    - `msExchRemoteRecipientType` = `$null`
    - `targetAddress` = coex routing address
4. Verify the target email address was temporarily assigned as primary SMTP and the B2B invitation was automatically accepted.
5. Verify the primary SMTP address was reverted to the original value with the target email address retained as a secondary SMTP.
6. Verify the source user is now a B2B external member linked to the target identity.

**Expected Result:** The automation completes all steps without error. The test user is B2B-enabled with the correct attribute configuration.

---

### Verify B2B Reach-Back Access

**Objective:** Confirm that a user processed by automation can access source tenant resources via SSO using target credentials.

**Prerequisites:**

- A test user has been processed by automation per [Verify B2B Reach-Back Automation](#verify-b2b-reach-back-automation)
- Cross-tenant access settings are configured for MFA trust and device compliance trust

**Steps:**

1. Sign in to the target tenant as the test user using target credentials.
2. Navigate to a SharePoint Online site in the source tenant. Verify access is granted via SSO without additional authentication prompts.
3. Open Teams and switch to the source tenant. Verify the user can access Teams resources in the source tenant.
4. Navigate to a key Entra ID-integrated application in the source tenant. Verify access is granted with the user's original permissions.
5. If device-based conditional access is used, perform the above steps from a managed device in the target tenant. Verify access is granted without device compliance errors.
6. Verify only a single MFA challenge occurs on the target identity when accessing source tenant resources.

**Expected Result:** The test user can access SharePoint Online, Teams, and Entra ID-integrated applications in the source tenant via SSO. If device-based conditional access is configured, access succeeds from a managed device. Only one MFA challenge is required on the target identity.

---

### Verify Teams Chat Experience with MTO

**Objective:** If MTO is enabled, confirm that Teams chat from people search routes to the user's home account without requiring tenant switching.

**Prerequisites:**

- Source and target tenants are configured as a multitenant organization (MTO)
- A test user has been processed by automation per [Verify B2B Reach-Back Automation](#verify-b2b-reach-back-automation)
- Teams desktop app is installed (Teams web app does not support MTO features)

**Steps:**

1. Sign in to Teams desktop app in the source tenant as another user.
2. Search for the test user using people search.
3. Initiate a chat with the test user from the search results.
4. Verify the chat routes to the test user's home account in the target tenant without requiring tenant switching.

**Expected Result:** Chat initiated from people search in the source tenant routes to the test user's home account in the target tenant. The test user receives the chat without needing to switch tenants.

## Sources

- [Invite internal users to B2B collaboration](https://learn.microsoft.com/en-us/entra/external-id/invite-internal-users) — Microsoft Learn, accessed 2026-02-04
- [Cross-tenant mailbox migration](https://learn.microsoft.com/en-us/microsoft-365/enterprise/cross-tenant-mailbox-migration) — Microsoft Learn, accessed 2026-02-04
- [Place a mailbox on retention hold in Exchange Online](https://learn.microsoft.com/en-us/exchange/security-and-compliance/messaging-records-management/mailbox-retention-hold) — Microsoft Learn, accessed 2026-02-04
- [Product names and service plan identifiers for licensing](https://learn.microsoft.com/en-us/entra/identity/users/licensing-service-plan-reference) — Microsoft Learn, accessed 2026-02-04
- [Assign licenses to a group](https://learn.microsoft.com/en-us/entra/identity/users/licensing-groups-assign) — Microsoft Learn, accessed 2026-02-04
- [Microsoft Graph permissions reference](https://learn.microsoft.com/en-us/graph/permissions-reference) — Microsoft Learn, accessed 2026-02-04

## Related Topics

*None at this time.*

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-02-04 | Owen Lundberg | Initial publication |
