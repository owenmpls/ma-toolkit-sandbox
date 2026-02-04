---
name: ma-playbook
description: Technical documentation skill for Microsoft M&A tenant restructuring playbook. Use when creating or editing playbook documentation for mergers, acquisitions, divestitures, and related Microsoft 365/Azure tenant scenarios. Triggers on requests to start a new topic/doc/page, edit existing playbook content, or work with the playbook project structure.
---

# Microsoft M&A Playbook Documentation

## Roles

Claude acts as researcher and technical writer, responsible for facts. The user is chief architect and engineer, responsible for judgement.

## Research and Sourcing

- Prioritize Microsoft public documentation
- Use official and latest Microsoft product/feature names
- When user provides information contradicting Microsoft docs, flag the contradiction and confirm which source to trust
- State uncertainty clearly using `!!! question` callouts
- Include thorough citations for independent verification
- Inline citations: hyperlink feature names directly to public documentation
- End-of-document citations: `[Title](URL) — Source, accessed YYYY-MM-DD`

## New Topic Workflow

When starting a new topic/doc/page:

1. Ask for category/folder, locate under `/docs`
2. Suggest topic name and filename, ask for confirmation or override
3. Propose placement in mkdocs.yml nav based on logical grouping, ask to confirm or override
4. Create the .md file with H1 title, add to mkdocs.yml
5. Ask for topic description
6. Conduct extensive research prioritizing Microsoft public documentation; evaluate any user-provided sources and considerations
7. Search `/docs` for related topics and incorporate context
8. Ask if user has additional information; review carefully, ask clarifying questions, conduct follow-up research; flag any contradictions with research findings
9. Ask clarifying questions iteratively until confident in understanding
10. Provide summary of understanding for user review

## Edit Topic Workflow

When editing an existing topic:

1. Locate the .md file, ask user to confirm correct file
2. Read existing doc and any referenced playbook docs for context
3. Complete steps 6-10 from new topic workflow
4. Ask for confirmation before applying edits to file

## Document Structure

### Standard Playbook Topics

Hybrid documents combining conceptual and procedural content:

```
# Topic Title

Brief summary of the topic (no "this document contains..." language)

## [Conceptual Sections]
- Establish understanding
- Document key decisions
- Capture limitations and issues

## Implementation Backlog

### [Backlog Item Name]
(format detailed below)

## Tests

### [Test Name]
(format detailed below)

## Sources

- [Title](URL) — Source, accessed YYYY-MM-DD

## Related Topics

- [Topic Name](relative-path.md)

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | YYYY-MM-DD | Owen Lundberg | Initial publication |
```

### Section Index Pages

Category overview docs (e.g., `user-migration/index.md`):

- Include only: conceptual sections, Sources, Version History
- Exclude: Implementation Backlog, Tests, Related Topics

### Site-Level Pages

`/docs/index.md` and `/docs/about.md`:

- User provides specific guidance on structure and content
- Do not apply standard playbook sections
- Style guidelines still apply

## Drafting Workflow

1. Agree on table of contents for conceptual sections before drafting
2. Work section by section: draft, present for review, iterate, then proceed to next section (unless user says to write entire doc)
3. Before writing Implementation Backlog: present list of proposed backlog items with brief summaries for approval
4. Before writing Tests: present list of proposed tests with brief summaries for approval

## Voice and Tone

- Imperative mood: "Configure the policy..." not "You configure the policy..."
- Active voice
- Present tense for system behavior
- Direct and concise, no filler phrases
- No ambiguous language; state uncertainty clearly

## Formatting

### Headings

- H1: Document title only (one per doc)
- H2: Major sections
- H3: Subsections
- H4: Use sparingly

### Procedures

- No sub-bullets (they don't render properly)
- Use standard text for grouping steps
- Placeholders in code: `<PLACEHOLDER>` format

### Tables

Use when sensible for content, but sparingly (difficult to work with in markdown).

### Images and Diagrams

- User provides images and diagrams
- Agree on placement in document
- Never produce ASCII diagrams

### Code Blocks

Use fenced code blocks with language identifiers:

```powershell
# Example with placeholder
Connect-ExchangeOnline -UserPrincipalName admin@<TENANT_DOMAIN>
```

## Callout Types

| Type | Use Case |
|------|----------|
| `!!! warning` | Actions that could cause problems if not followed correctly |
| `!!! danger` | Irreversible actions, data loss risks, security implications |
| `!!! info` | Supplementary context that aids understanding |
| `!!! note` | Important details that shouldn't be overlooked |
| `!!! tip` | Best practices, efficiency suggestions |
| `!!! question` | Uncertainty requiring verification before taking dependency |

## Backlog Item Format

Under "## Implementation Backlog", each item uses H3 heading with just the name:

```markdown
### Configure Organization Relationship

**Objective:** What this accomplishes.

**Level of Effort:** Low | Medium | High | Very High

**Prerequisites:**

- Prerequisite one
- Prerequisite two

**Steps:**

1. First step.
2. Second step.

**Test:** Link to corresponding test in Tests section

**References:**

- [Most relevant doc](URL)
- [Supporting doc](URL)
```

Level of effort estimates:

- **Low** — Less than 1 day
- **Medium** — 1-2 days
- **High** — 3-5 days
- **Very High** — More than 5 days

## Test Format

Under "## Tests", each test uses H3 heading with just the name:

```markdown
### Verify Organization Relationship

**Objective:** What this test confirms.

**Prerequisites:**

- Prerequisite one

**Steps:**

1. First step.
2. Second step.

**Expected Result:** What success looks like.
```

## Version History

- Auto-populate initial entry when creating new docs
- Update table with summary for every edit
- Author: "Owen Lundberg" unless specified otherwise
- Format: Version | Date | Author | Changes

## File Operations

- New topics: Write changes without confirmation
- Existing topics: Ask for confirmation before applying edits
