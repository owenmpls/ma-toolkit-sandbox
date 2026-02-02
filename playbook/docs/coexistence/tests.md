# Coexistence Test Cases

> **Created with AI. Pending verification by a human. Use with caution.**

This document contains test cases for validating the cross-tenant coexistence implementation. Each test case is linked to a corresponding backlog item.

## Test Categories

| Category | Description |
|----------|-------------|
| **LT** | Low-Trust Integration Tests |
| **HT** | High-Trust Integration Tests |
| **MF** | Mail Flow Tests |
| **HY** | Hybrid Integration Tests |
| **MC** | Migration Coexistence Tests |
| **IR** | Identity Rationalization Tests |

---

## Low-Trust Integration Tests

### LT-01-T01: Free/Busy Sharing Validation

**Related Backlog Item:** [LT-01: Configure Organization Relationships for Free/Busy Sharing](backlog.md#lt-01-configure-organization-relationships-for-freebusy-sharing)

**Objective:** Validate that calendar free/busy information is accessible across tenants.

**Prerequisites:**
- Organization relationships configured in both tenants
- Test users with calendar events in both tenants

**Test Steps:**

1. **Create test event in source tenant**
   - Sign in as `testuser@contoso.com`
   - Create a calendar event for tomorrow, 2:00 PM - 3:00 PM
   - Set event as "Busy"

2. **Verify free/busy from partner tenant**
   - Sign in as `testuser@fabrikam.com`
   - Create a new meeting request
   - Add `testuser@contoso.com` as required attendee
   - Open Scheduling Assistant
   - Verify that 2:00 PM - 3:00 PM shows as busy

3. **Test bidirectional lookup**
   - Repeat steps 1-2 in reverse direction
   - Verify free/busy works from Contoso to Fabrikam

**Expected Results:**
- [ ] Scheduling Assistant shows busy time for partner user
- [ ] Free/busy detail level matches organization relationship configuration
- [ ] Lookup completes within 5 seconds

**Troubleshooting:**
- If lookup fails, verify targetAddress on MailUser points directly to mailbox (not routing address)
- Check autodiscover endpoint in organization relationship
- Verify no conflicting organization relationships for the domain

---

### LT-02-T01: Cross-Tenant Access Settings Validation

**Related Backlog Item:** [LT-02: Configure Cross-Tenant Access Settings for B2B Collaboration](backlog.md#lt-02-configure-cross-tenant-access-settings-for-b2b-collaboration)

**Objective:** Validate that B2B collaboration settings are correctly configured.

**Prerequisites:**
- Cross-tenant access settings configured for partner tenant

**Test Steps:**

1. **Verify partner organization configuration**
   ```powershell
   Connect-MgGraph -Scopes "Policy.Read.All"
   Get-MgPolicyCrossTenantAccessPolicyPartner | Where-Object { $_.TenantId -eq "fabrikam-tenant-id" }
   ```

2. **Test guest invitation flow**
   - Sign in to Contoso tenant as user with invite permissions
   - Invite `newuser@fabrikam.com` to a SharePoint site
   - Verify invitation sent successfully

3. **Test automatic redemption**
   - Sign in as `newuser@fabrikam.com`
   - Access the SharePoint site
   - Verify no consent prompt appears (automatic redemption)

**Expected Results:**
- [ ] Partner organization appears in cross-tenant access policy
- [ ] Inbound B2B collaboration enabled
- [ ] Automatic redemption suppresses consent prompt

---

### LT-03-T01: Teams External Access Validation

**Related Backlog Item:** [LT-03: Configure Teams External Access](backlog.md#lt-03-configure-teams-external-access)

**Objective:** Validate Teams federation for chat and meetings.

**Test Steps:**

1. **Verify federation configuration**
   ```powershell
   Connect-MicrosoftTeams
   Get-CsTenantFederationConfiguration | Select-Object AllowFederatedUsers, AllowedDomains
   ```

2. **Test federated chat**
   - Sign in to Teams as `testuser@contoso.com`
   - Search for `testuser@fabrikam.com`
   - Start a new chat
   - Send a test message

3. **Verify external indicator**
   - Confirm user shows "(External)" tag in chat
   - Verify message delivery

4. **Test federated meeting**
   - Create Teams meeting from Contoso
   - Add `testuser@fabrikam.com` as required attendee
   - Verify meeting invite delivered
   - Join meeting from both tenants

**Expected Results:**
- [ ] Federation enabled for partner domain
- [ ] Federated chat works bidirectionally
- [ ] External users tagged appropriately
- [ ] Federated meetings function correctly

---

### LT-04-T01: Shared Channels Validation

**Related Backlog Item:** [LT-04: Configure B2B Direct Connect for Shared Channels](backlog.md#lt-04-configure-b2b-direct-connect-for-shared-channels)

**Objective:** Validate Teams shared channels with B2B direct connect.

**Test Steps:**

1. **Create shared channel**
   - Sign in to Teams as team owner in Contoso
   - Create a new shared channel in existing team
   - Add `testuser@fabrikam.com` to shared channel

2. **Verify partner access**
   - Sign in to Teams as `testuser@fabrikam.com`
   - Verify shared channel appears in Teams client
   - Verify no tenant switch required

3. **Test collaboration**
   - Post message in shared channel from both users
   - Upload file to shared channel
   - Verify file accessible to partner user

**Expected Results:**
- [ ] Shared channel created successfully
- [ ] Partner user can access channel without tenant switch
- [ ] Files and chat work within shared channel
- [ ] Partner user cannot access other team resources

---

### LT-05-T01: Cross-Tenant Synchronization Validation

**Related Backlog Item:** [LT-05: Deploy Cross-Tenant Synchronization](backlog.md#lt-05-deploy-cross-tenant-synchronization)

**Objective:** Validate B2B user provisioning via cross-tenant sync.

**Test Steps:**

1. **Verify sync configuration**
   - Check XTS provisioning status in Entra admin center
   - Verify scoping group membership

2. **Add user to scoping group**
   - Add `newuser@contoso.com` to XTS scoping group
   - Wait for provisioning cycle (up to 40 minutes)

3. **Verify provisioned user**
   ```powershell
   Connect-MgGraph -Scopes "User.Read.All"
   Get-MgUser -Filter "mail eq 'newuser@contoso.com'" | Select-Object DisplayName, Mail, UserType
   ```

4. **Test people search**
   - Sign in to Outlook/Teams as `testuser@fabrikam.com`
   - Search for `newuser@contoso.com` in people picker
   - Verify user appears in GAL

**Expected Results:**
- [ ] User provisioned as B2B guest in target tenant
- [ ] User appears in people search
- [ ] User has MailUser object in Exchange Online
- [ ] Sync completes within expected timeframe

---

### LT-05-T02: XTS Existing Guest Matching

**Related Backlog Item:** [LT-05: Deploy Cross-Tenant Synchronization](backlog.md#lt-05-deploy-cross-tenant-synchronization)

**Objective:** Validate that XTS matches existing guest accounts on altSecID.

**Test Steps:**

1. **Create guest before enabling XTS**
   - Manually invite `existinguser@contoso.com` as guest in Fabrikam
   - Verify guest account created

2. **Enable XTS with existing guest in scope**
   - Add `existinguser@contoso.com` to XTS scoping group
   - Trigger sync cycle

3. **Verify matching behavior**
   ```powershell
   Connect-MgGraph -Scopes "User.Read.All"
   Get-MgUser -Filter "mail eq 'existinguser@contoso.com'" | Select-Object DisplayName, UserType, Id
   ```

4. **Verify userType unchanged**
   - Confirm userType remains "Guest" (not automatically promoted to Member)
   - Verify XTS now manages the account (check provisioning logs)

**Expected Results:**
- [ ] XTS matches existing guest on alternativeSecurityId
- [ ] XTS assumes management of existing account
- [ ] UserType remains Guest (default behavior)
- [ ] No duplicate account created

---

### LT-06-T01: External Member Blocking Validation

**Related Backlog Item:** [LT-06: Implement Conditional Access Mitigation for External Members](backlog.md#lt-06-implement-conditional-access-mitigation-for-external-members)

**Objective:** Validate that external members are blocked by conditional access.

**Test Steps:**

1. **Temporarily provision external member**
   - Update XTS to provision user as Member
   - Provision test user `blockeduser@contoso.com`

2. **Attempt access**
   - Sign in as `blockeduser@contoso.com`
   - Attempt to access Fabrikam SharePoint
   - Verify access denied by CA policy

3. **Verify guest access still works**
   - Sign in as existing guest `guestuser@contoso.com`
   - Attempt to access shared SharePoint site
   - Verify access granted

**Expected Results:**
- [ ] External members blocked by CA policy
- [ ] Block message indicates conditional access denial
- [ ] External guests not affected by policy
- [ ] Policy correctly scoped to partner tenant

---

## High-Trust Integration Tests

### HT-01-T01: External Member Access Validation

**Related Backlog Item:** [HT-01: Promote Synchronized Users to External Members](backlog.md#ht-01-promote-synchronized-users-to-external-members)

**Objective:** Validate external member default access capabilities.

**Test Steps:**

1. **Promote user to external member**
   ```powershell
   Connect-MgGraph -Scopes "User.ReadWrite.All"
   Update-MgUser -UserId "user-object-id" -UserType "Member"
   ```

2. **Test org-wide sharing access**
   - Create SharePoint document with "People in organization" sharing
   - Sign in as promoted external member
   - Verify access to org-wide shared document

3. **Test Teams discovery**
   - Sign in as external member
   - Search for public team
   - Verify ability to discover and request to join

4. **Verify visual indicators**
   - Check Teams profile for external member
   - Verify no "(Guest)" tag displayed

**Expected Results:**
- [ ] External member can access org-wide shared content
- [ ] External member can discover public teams
- [ ] No guest tag in Teams
- [ ] Member appears in "Everyone except external users" group

---

### HT-02-T01: MTO Teams Collaboration Validation

**Related Backlog Item:** [HT-02: Establish Multitenant Organization](backlog.md#ht-02-establish-multitenant-organization)

**Objective:** Validate MTO-enhanced Teams collaboration.

**Test Steps:**

1. **Verify MTO membership**
   ```powershell
   Connect-MgGraph -Scopes "MultiTenantOrganization.Read.All"
   Get-MgTenantRelationshipMultiTenantOrganizationTenant
   ```

2. **Test cross-tenant people search in Teams**
   - Sign in to Teams as `testuser@contoso.com`
   - Search for `testuser@fabrikam.com` in chat
   - Verify user found with MTO-enhanced search

3. **Test federated chat routing**
   - Start chat with partner user
   - Verify chat routes to home account (federated chat)
   - Check for duplicate chat threads

4. **Verify visual prompt**
   - If B2B chat detected, verify prompt to switch to federated chat

**Expected Results:**
- [ ] MTO membership confirmed for both tenants
- [ ] People search works across tenants
- [ ] Federated chat routes correctly
- [ ] No duplicate chat threads created

---

### HT-03-T01: Device Trust Validation

**Related Backlog Item:** [HT-03: Enable Device Trust in Cross-Tenant Access Settings](backlog.md#ht-03-enable-device-trust-in-cross-tenant-access-settings)

**Objective:** Validate cross-tenant device compliance claims.

**Prerequisites:**
- Test device enrolled in Intune in home tenant
- CA policy in resource tenant requiring compliant device

**Test Steps:**

1. **Verify trust settings**
   ```powershell
   Connect-MgGraph -Scopes "Policy.Read.All"
   Get-MgPolicyCrossTenantAccessPolicyPartner | Select-Object TenantId, InboundTrust
   ```

2. **Test with compliant device**
   - Sign in from Intune-enrolled device as partner user
   - Access resource requiring compliant device
   - Verify access granted

3. **Test with non-compliant device**
   - Sign in from non-enrolled device as partner user
   - Attempt same resource access
   - Verify access denied

**Expected Results:**
- [ ] Trust settings accept compliant device claims
- [ ] Compliant device from partner satisfies CA policy
- [ ] Non-compliant device correctly denied
- [ ] Device compliance evaluated from home tenant

---

## Mail Flow Tests

### MF-01-T01: Outbound Connector Validation

**Related Backlog Item:** [MF-01: Configure Outbound Partner Connector](backlog.md#mf-01-configure-outbound-partner-connector)

**Objective:** Validate mail routes through partner connector.

**Test Steps:**

1. **Send test message**
   - Send email from `testuser@contoso.com` to `testuser@fabrikam.com`

2. **Verify connector routing**
   ```powershell
   Connect-ExchangeOnline
   Get-MessageTrace -SenderAddress "testuser@contoso.com" -RecipientAddress "testuser@fabrikam.com" -StartDate (Get-Date).AddHours(-1)
   ```

3. **Check message headers**
   - View received message headers
   - Verify routing through partner connector
   - Check for direct EOP delivery (not internet MX)

**Expected Results:**
- [ ] Message routes through outbound partner connector
- [ ] Delivery via partner EOP endpoint
- [ ] No internet MX routing

---

### MF-02-T01: Inbound Connector and GAL Resolution Validation

**Related Backlog Item:** [MF-02: Configure Inbound Partner Connector](backlog.md#mf-02-configure-inbound-partner-connector)

**Objective:** Validate inbound mail attribution and sender resolution.

**Prerequisites:**
- Cross-tenant synchronization deployed
- MailUser objects exist for partner users

**Test Steps:**

1. **Send inbound test message**
   - Send email from `testuser@fabrikam.com` to `testuser@contoso.com`

2. **Verify connector attribution**
   ```powershell
   Connect-ExchangeOnline
   Get-MessageTrace -SenderAddress "testuser@fabrikam.com" -RecipientAddress "testuser@contoso.com" -StartDate (Get-Date).AddHours(-1) | Select-Object Received, SenderAddress, RecipientAddress, Status
   ```

3. **Check sender display**
   - Open received message in Outlook
   - Verify sender shows GAL profile information
   - Verify sender photo/details from directory

4. **Test sender authorization**
   - Add partner user to restricted distribution group
   - Send message from partner user to group
   - Verify message delivered (authorization works)

**Expected Results:**
- [ ] Inbound mail attributed to partner connector
- [ ] Sender resolves to MailUser in GAL
- [ ] Sender profile information displayed
- [ ] Sender authorization works for groups

---

## Hybrid Integration Tests

### HY-01-T01: Hybrid External Member Validation

**Related Backlog Item:** [HY-01: Configure Hybrid External Members](backlog.md#hy-01-configure-hybrid-external-members)

**Objective:** Validate hybrid external member for on-premises access.

**Prerequisites:**
- Hybrid external member provisioned
- App Proxy application published with KCD

**Test Steps:**

1. **Verify hybrid sync**
   - Confirm user synced from on-premises AD
   - Verify external authentication configured

2. **Test Kerberos SSO**
   - Sign in as hybrid external member
   - Access App Proxy published application
   - Verify Kerberos SSO to backend application

3. **Verify mail flow**
   - Send email to hybrid external member
   - Verify delivery to correct mailbox

**Expected Results:**
- [ ] User synced as hybrid external member
- [ ] Kerberos SSO works via App Proxy
- [ ] Mail routing functions correctly

---

## Migration Coexistence Tests

### MC-01-T01: CTIM Attribute Validation

**Related Backlog Item:** [MC-01: Configure Cross-Tenant Identity Mapping (CTIM)](backlog.md#mc-01-configure-cross-tenant-identity-mapping-ctim)

**Objective:** Validate CTIM attribute stamping for migration.

**Test Steps:**

1. **Run CTIM tool**
   - Execute CTIM for test user
   - Verify completion without errors

2. **Verify attributes stamped**
   ```powershell
   Connect-ExchangeOnline -UserPrincipalName admin@fabrikam.com
   Get-MailUser "testuser@fabrikam.com" | Select-Object ExchangeGuid, ArchiveGuid, EmailAddresses
   ```

3. **Verify X500 addresses**
   - Check EmailAddresses for X500 entries
   - Verify matches source Exchange organization

**Expected Results:**
- [ ] ExchangeGUID matches source mailbox
- [ ] ArchiveGUID stamped (if applicable)
- [ ] X500 addresses added for legacy routing

---

### MC-03-T01: Reach-Back Access Validation

**Related Backlog Item:** [MC-03: Implement Reach-Back Access](backlog.md#mc-03-implement-reach-back-access)

**Objective:** Validate migrated users can access source tenant resources.

**Prerequisites:**
- User migrated to target tenant
- B2B account created in source tenant for migrated user

**Test Steps:**

1. **Verify B2B account in source**
   ```powershell
   Connect-MgGraph -Scopes "User.Read.All"
   Get-MgUser -Filter "mail eq 'migrateduser@fabrikam.com' and userType eq 'Guest'"
   ```

2. **Test resource access**
   - Sign in as migrated user (in target tenant)
   - Access SharePoint site in source tenant
   - Verify SSO (no additional authentication)

3. **Verify permissions**
   - Confirm migrated user retains access to previously accessible content
   - Test document editing capability

**Expected Results:**
- [ ] B2B account exists in source tenant
- [ ] Migrated user can access source resources
- [ ] SSO works without additional auth
- [ ] Permissions function correctly

---

## Identity Rationalization Tests

### IR-01-T01: B2B Account Conversion Validation

**Related Backlog Item:** [IR-01: B2B Account Conversion](backlog.md#ir-01-b2b-account-conversion)

**Objective:** Validate B2B to internal account conversion.

**Test Steps:**

1. **Pre-conversion inventory**
   - Document user's group memberships
   - Document SharePoint permissions
   - Document Teams memberships

2. **Execute conversion**
   - Convert B2B account to internal
   - Provision credentials in resource tenant

3. **Post-conversion validation**
   - Verify group memberships preserved
   - Verify SharePoint permissions retained
   - Verify Teams chat history accessible

4. **Test Viva Engage (known issue)**
   - Access Viva Engage as converted user
   - Document any issues encountered

**Expected Results:**
- [ ] Account converted to internal type
- [ ] Group memberships preserved
- [ ] SharePoint permissions retained
- [ ] Teams chat history accessible
- [ ] Viva Engage issues documented (if any)

---

## Test Execution Tracking

| Test ID | Test Name | Status | Executed By | Date | Notes |
|---------|-----------|--------|-------------|------|-------|
| LT-01-T01 | Free/Busy Sharing | | | | |
| LT-02-T01 | Cross-Tenant Access Settings | | | | |
| LT-03-T01 | Teams External Access | | | | |
| LT-04-T01 | Shared Channels | | | | |
| LT-05-T01 | Cross-Tenant Synchronization | | | | |
| LT-05-T02 | XTS Existing Guest Matching | | | | |
| LT-06-T01 | External Member Blocking | | | | |
| HT-01-T01 | External Member Access | | | | |
| HT-02-T01 | MTO Teams Collaboration | | | | |
| HT-03-T01 | Device Trust | | | | |
| MF-01-T01 | Outbound Connector | | | | |
| MF-02-T01 | Inbound Connector and GAL Resolution | | | | |
| HY-01-T01 | Hybrid External Member | | | | |
| MC-01-T01 | CTIM Attribute Validation | | | | |
| MC-03-T01 | Reach-Back Access | | | | |
| IR-01-T01 | B2B Account Conversion | | | | |

---

## Related Documents

- [Overview](index.md)
- [Implementation Backlog](backlog.md)

## Related Topics

- [Coexistence Overview](index.md)
- [Coexistence Implementation Backlog](backlog.md)
- [Cross-Tenant User Migration](../user-migration/index.md)
