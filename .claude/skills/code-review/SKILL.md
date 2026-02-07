---
name: code-review
description: Perform a thorough code review and file findings as GitHub Issues with structured labels
argument-hint: <focus> (e.g., "security", "src/automation/scheduler/", "infra")
disable-model-invocation: true
---

# Code Review Skill

You are performing a structured code review of the MA-Toolkit codebase. Follow these phases exactly.

## Phase 1 — Determine Scope

Interpret `$ARGUMENTS` as the review focus:

- **Directory path** (e.g., `src/automation/scheduler/`): Review all source files in that directory tree
- **Topic** (e.g., `security`, `performance`, `error-handling`): Review the full codebase through that lens
- **Component name** (e.g., `scheduler`, `cloud-worker`, `infra`): Map to the corresponding project directory
- **Empty**: Ask the user what area or topic they want reviewed using AskUserQuestion

Component-to-directory mapping:
| Component | Directory |
|-----------|-----------|
| scheduler | `src/automation/scheduler/` |
| orchestrator | `src/automation/orchestrator/` |
| admin-api | `src/automation/admin-api/` |
| admin-cli | `src/automation/admin-cli/` |
| cloud-worker | `src/automation/cloud-worker/` |
| infra | `infra/` |
| shared | `src/automation/shared/` |

## Phase 2 — Review

Thoroughly explore the relevant code using Read, Glob, Grep, and the Explore agent. Read the project-level `CLAUDE.md` files for architectural context.

Look for:
- **Bugs**: Logic errors, off-by-one, null references, race conditions
- **Security**: Injection, secrets exposure, missing auth checks, OWASP top 10
- **Performance**: N+1 queries, unnecessary allocations, missing caching, blocking async
- **Correctness**: Contract mismatches between components, wrong assumptions, silent failures
- **Error handling**: Missing try/catch, swallowed exceptions, unhelpful error messages
- **Infrastructure**: Bicep misconfigurations, missing resources, wrong SKUs, missing RBAC
- **Resilience**: Missing retries, no dead-letter handling, timeout issues

For each finding, record:
1. A concise title
2. Priority: P0 (critical), P1 (high), P2 (medium), P3 (low)
3. Component label (from the table above)
4. Affected file(s) with line numbers
5. What the problem is (with code snippet)
6. What the fix should be (with code snippet if applicable)

### Priority Definitions

- **P0 — Critical**: Blocks deployment, causes data loss, security vulnerability with active exploit path
- **P1 — High**: Must fix before production load — incorrect behavior, missing error handling on critical paths, infra misconfiguration that causes failures
- **P2 — Medium**: Should fix soon — suboptimal patterns, missing validation, non-critical misconfigurations
- **P3 — Low**: Fix when convenient — style issues, minor improvements, documentation gaps

## Phase 3 — Present Findings

Display all findings in a numbered summary table:

```
| #  | Title                              | Priority | Component  | Files                        | Description                    |
|----|------------------------------------|----------|------------|------------------------------|--------------------------------|
| 1  | SQL injection in query builder     | P0       | admin-api  | QueryService.cs:45           | User input not parameterized   |
| 2  | Missing retry on SB send           | P1       | orchestrator | Dispatcher.cs:89           | Transient failures not handled |
| ...                                                                                                                      |
```

Then ask the user which findings to file as GitHub Issues. Use AskUserQuestion with options:
- "All findings" — file every finding
- "Let me pick" — user will specify which numbers to include
- "None" — skip issue creation

If the user picks "Let me pick", ask them to specify the finding numbers to file.

## Phase 4 — Create GitHub Issues

### Step 4a — Ensure Labels Exist

Run the following `gh label create` commands. These are idempotent (will fail silently if the label already exists — that's fine).

Priority labels:
```
gh label create "priority/p0" --color "b60205" --description "Critical — blocks deployment or causes data loss" --force
gh label create "priority/p1" --color "d93f0b" --description "High — must fix before production load" --force
gh label create "priority/p2" --color "fbca04" --description "Medium — should fix soon" --force
gh label create "priority/p3" --color "0e8a16" --description "Low — fix when convenient" --force
```

Component labels:
```
gh label create "component/scheduler" --color "1d76db" --description "Scheduler Function App" --force
gh label create "component/orchestrator" --color "1d76db" --description "Orchestrator Function App" --force
gh label create "component/admin-api" --color "1d76db" --description "Admin API Function App" --force
gh label create "component/admin-cli" --color "1d76db" --description "Admin CLI tool" --force
gh label create "component/cloud-worker" --color "1d76db" --description "Cloud Worker (ACA/PowerShell)" --force
gh label create "component/infra" --color "1d76db" --description "Bicep infrastructure templates" --force
gh label create "component/shared" --color "1d76db" --description "Shared .NET library" --force
```

### Step 4b — Create Issues

For each approved finding, create a GitHub Issue using `gh issue create`. Use a HEREDOC for the body to preserve formatting.

**Labels**: Apply both `priority/p<N>` and `component/<name>` labels.

**Title**: Use the concise finding title.

**Body format**:
```markdown
## Impact
[Why this matters — what breaks, what's at risk]

## Affected Files
- `path/to/file.cs:123` — description of what's wrong at this location
- `path/to/other.bicep:45` — description of what's wrong at this location

## Problem
[Detailed description of the issue]

\```<language>
// Code snippet showing the problematic code
\```

## Proposed Fix
[Description of the recommended fix]

\```<language>
// Code snippet showing the corrected code
\```

---
Found by `/code-review $ARGUMENTS` on YYYY-MM-DD
```

Replace `YYYY-MM-DD` with today's date. Replace `$ARGUMENTS` with the actual arguments passed to the skill.

**Example `gh` command**:
```bash
gh issue create \
  --title "SQL injection in QueryService" \
  --label "priority/p0" --label "component/admin-api" \
  --body "$(cat <<'EOF'
## Impact
...

## Affected Files
...

## Problem
...

## Proposed Fix
...

---
Found by `/code-review admin-api` on 2026-02-07
EOF
)"
```

After creating all issues, display a summary of the created issues with their numbers and URLs.
