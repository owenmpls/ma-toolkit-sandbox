# Style Sample

This page demonstrates the formatting conventions used throughout the playbook.

## Callout Box Types

### Warning

Use for actions that could cause problems if not followed correctly.

!!! warning "Timing Dependency"
    Complete the source tenant configuration before initiating the migration batch. Failure to do so results in orphaned objects in the target tenant.

### Danger

Use for irreversible actions, data loss risks, or security implications.

!!! danger "Irreversible Action"
    Deleting the source tenant removes all associated data permanently. This action cannot be undone.

### Info

Use for supplementary context that aids understanding.

!!! info "Background"
    Cross-tenant mailbox migration was introduced in 2020 to support M&A scenarios without requiring PST exports or third-party tools.

### Note

Use for important details that should not be overlooked.

!!! note "Licensing Requirement"
    Both source and target tenants must have Exchange Online Plan 2 or equivalent.

### Tip

Use for best practices and efficiency suggestions.

!!! tip "Best Practice"
    Run a pilot migration with 5-10 mailboxes before proceeding with bulk migration.

### Question

Use for uncertainty requiring verification before taking dependency.

!!! question "Requires Verification"
    Microsoft documentation does not specify whether this setting persists after tenant-to-tenant migration. Test in a non-production environment before relying on this behavior.

## Implementation Backlog

### Configure Organization Relationship

**Objective:** Establish trust between source and target tenants to enable cross-tenant mailbox migration.

**Level of Effort:** Medium

**Prerequisites:**

- Global Administrator access to both tenants
- Target tenant domain verified

**Steps:**

1. In the target tenant Exchange admin center, navigate to **Organization** > **Sharing**.
2. Create a new organization relationship with the source tenant domain.
3. Configure the relationship to allow mailbox moves.
4. Repeat in the source tenant, referencing the target tenant domain.

**Validation:** Verify the organization relationship appears as "Enabled" in both tenants.

**References:**

- [Configure cross-tenant mailbox migration](https://learn.microsoft.com/en-us/microsoft-365/enterprise/cross-tenant-mailbox-migration)
- [Organization relationships in Exchange Online](https://learn.microsoft.com/en-us/exchange/sharing/organization-relationships/organization-relationships)

## Tests

### Verify Organization Relationship

**Objective:** Confirm that the organization relationship is correctly configured and functional.

**Prerequisites:**

- Organization relationship configured in both tenants

**Steps:**

1. Open Exchange Online PowerShell connected to the target tenant.
2. Run `Get-OrganizationRelationship | Format-List` and confirm MailboxMoveEnabled is True.
3. Repeat in the source tenant.

**Expected Result:** Both tenants show the organization relationship with MailboxMoveEnabled set to True and MailboxMoveCapability set appropriately (Inbound for target, Outbound for source).

## Code Block Format

PowerShell example with placeholder values:

```powershell
# Connect to Exchange Online in target tenant
Connect-ExchangeOnline -UserPrincipalName admin@<TARGET_TENANT_DOMAIN>

# Create organization relationship
New-OrganizationRelationship `
    -Name "Source Tenant Migration" `
    -DomainNames "<SOURCE_TENANT_DOMAIN>" `
    -MailboxMoveEnabled $true `
    -MailboxMoveCapability Inbound
```

## Sources

- [Cross-tenant mailbox migration](https://learn.microsoft.com/en-us/microsoft-365/enterprise/cross-tenant-mailbox-migration) — Microsoft Learn, accessed 2026-02-03
- [Organization relationships in Exchange Online](https://learn.microsoft.com/en-us/exchange/sharing/organization-relationships/organization-relationships) — Microsoft Learn, accessed 2026-02-03

## Related Topics

- [User Migration](user-migration/index.md)

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-02-03 | Owen Lundberg | Initial publication |
