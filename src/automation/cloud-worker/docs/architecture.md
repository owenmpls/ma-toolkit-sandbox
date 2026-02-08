# Architecture

## System Context

The PowerShell Cloud Worker is a component of the Migration Automation Toolkit's automation subsystem. It executes migration functions against a target Microsoft 365 tenant on behalf of the orchestrator.

```
                                    ┌─────────────────────┐
                                    │     Orchestrator     │
                                    │  (Azure Functions)   │
                                    └──────┬────────▲──────┘
                                           │        │
                                    enqueue jobs  results
                                           │        │
                              ┌────────────▼────────┴────────────┐
                              │        Azure Service Bus          │
                              │  ┌───────────┐ ┌──────────────┐  │
                              │  │worker-jobs│ │worker-results│  │
                              │  └────────┬──┘ └────▲─────────┘  │
                              └───────────┼─────────┼────────────┘
                                          │         │
                              ┌───────────▼─────────┴───────────┐
                              │     PowerShell Cloud Worker      │
                              │      (Azure Container Apps)      │
                              │                                  │
                              │  ┌────────────────────────────┐  │
                              │  │      Runspace Pool          │  │
                              │  │  ┌────┐ ┌────┐ ┌────┐     │  │
                              │  │  │ RS │ │ RS │ │ RS │ ... │  │
                              │  │  └──┬─┘ └──┬─┘ └──┬─┘     │  │
                              │  └─────┼──────┼──────┼────────┘  │
                              └────────┼──────┼──────┼───────────┘
                                       │      │      │
                              ┌────────▼──────▼──────▼───────────┐
                              │       Target M365 Tenant          │
                              │  ┌──────────┐  ┌──────────────┐  │
                              │  │  Graph    │  │ Exchange     │  │
                              │  │  API      │  │ Online       │  │
                              │  └──────────┘  └──────────────┘  │
                              └───────────────────────────────────┘
```

## Worker Lifecycle

### Startup Sequence

1. **Configuration** — Loads environment variables, validates required values
2. **Logging** — Initializes Application Insights TelemetryClient
3. **Azure Auth** — Connects to Azure using managed identity (for Key Vault access)
4. **Secret Retrieval** — Retrieves the target tenant app client secret from Key Vault
5. **Service Bus** — Loads .NET assemblies, creates client/receiver/sender
6. **Runspace Pool** — Creates pool, authenticates each runspace to MgGraph and EXO
7. **Shutdown Handler** — Registers SIGTERM/SIGINT handlers for graceful shutdown
8. **Job Dispatcher** — Enters main processing loop

### Idle Timeout and Scale-to-Zero

The worker tracks idle time — the elapsed duration since the last message was received or last job completed. When no jobs are in flight and the idle timeout is reached (`IDLE_TIMEOUT_SECONDS`, default 300s), the worker initiates a graceful shutdown and the process exits.

The Container App is configured with **min replicas = 0** and **max replicas = 1**. A KEDA `azure-servicebus` scaler monitors the worker's subscription on the `worker-jobs` topic. When one or more messages are pending, KEDA scales the container from 0 → 1, triggering the full boot sequence. After the worker finishes processing and the idle timeout elapses, the process exits and ACA scales back to 0.

```
Messages arrive on subscription
    │
    ▼
KEDA detects messageCount >= 1
    │
    ▼
ACA scales 0 → 1 (container starts)
    │
    ▼
Worker boots, authenticates, processes jobs
    │
    ▼
No new messages for IDLE_TIMEOUT_SECONDS
    │
    ▼
Worker exits gracefully, ACA scales 1 → 0
```

Set `IDLE_TIMEOUT_SECONDS=0` to disable idle shutdown and keep the worker running indefinitely.

### Shutdown Sequence

Shutdown is triggered by either a SIGTERM from the container orchestrator, or by the idle timeout.

1. Shutdown signal received (SIGTERM or idle timeout reached)
2. Stop accepting new messages
3. Wait for active jobs to complete (`SHUTDOWN_GRACE_SECONDS`, default 30s)
4. Send remaining results
5. Close runspace pool
6. Dispose Service Bus resources
7. Flush telemetry
8. Exit (process exits, ACA scales to zero)

## Parallelism Model

### RunspacePool

The worker uses a PowerShell `RunspacePool` for parallel job execution. This was chosen over alternatives because:

- **RunspacePool vs ThreadJobs**: RunspacePool provides lower overhead and more control over the execution environment. ThreadJobs use the PowerShell Jobs infrastructure which adds scheduling overhead inappropriate for a high-throughput worker.
- **RunspacePool vs ForEach-Object -Parallel**: The `-Parallel` parameter is designed for pipeline-based parallel iteration, not for a long-lived worker pool accepting work items over time.

### Session Isolation

Each runspace in the pool maintains its own authenticated sessions:

- **Microsoft Graph**: `Connect-MgGraph` with client secret credential per runspace. The MgGraph module stores connection context in module-scoped variables, so each runspace needs its own connection.
- **Exchange Online**: `Connect-ExchangeOnline` with an access token obtained via OAuth client credentials flow. Each runspace gets its own EXO session.

This isolation prevents thread-safety issues that would arise from sharing a single connection across concurrent operations.

### Concurrency Control

The `MaxParallelism` configuration controls the runspace pool size. The job dispatcher only fetches new Service Bus messages when runspace slots are available, providing natural backpressure.

## Service Bus Integration

### Topics and Subscriptions

- **Jobs Topic** (`worker-jobs`): The orchestrator publishes job messages. Each worker has a subscription with a SQL filter: `WorkerId = 'worker-XX'`.
- **Results Topic** (`worker-results`): Workers publish result messages. The orchestrator has a subscription receiving all results.

### Message Flow

```
Orchestrator → worker-jobs topic → worker-XX subscription → Worker
Worker → worker-results topic → orchestrator subscription → Orchestrator
```

### Message Handling

- **PeekLock**: Messages are received with PeekLock mode. The worker completes the message after successful processing or sends a failure result. Messages are abandoned on infrastructure errors so they can be retried.
- **Filtering**: Messages include a `WorkerId` application property. The subscription SQL filter ensures each worker only receives its assigned jobs.

## Authentication Architecture

### Infrastructure Auth (Managed Identity)

The container app has a system-assigned managed identity used for:
- Azure Key Vault secret retrieval
- Service Bus data operations (send/receive)

RBAC role assignments are provisioned by the Bicep template.

### Target Tenant Auth (App Registration)

A separate app registration in the **target** tenant provides access to Graph and Exchange Online. The client secret is stored in Key Vault.

```
Worker Container (Azure tenant A)
    │
    ├─ Managed Identity ──► Key Vault (tenant A)
    │                           └─ Client Secret
    │
    ├─ Managed Identity ──► Service Bus (tenant A)
    │
    └─ App Registration ──► Target Tenant (tenant B)
        ├─ Graph API
        └─ Exchange Online
```

### EXO Token Acquisition

Exchange Online's `Connect-ExchangeOnline` natively requires certificate-based app-only auth. To use client secrets instead, the worker:

1. Sends an OAuth client credentials request to `https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token`
2. Requests scope `https://outlook.office365.com/.default`
3. Passes the resulting access token to `Connect-ExchangeOnline -AccessToken`

## Throttle Handling

Both Microsoft Graph and Exchange Online enforce rate limits. The worker detects throttling at the runspace level:

### Detection Patterns

**Graph API**: `TooManyRequests`, `429`, `throttled`, `Rate limit`

**Exchange Online**: `Server Busy`, `ServerBusyException`, `MicroDelay`, `BackoffException`, `Too many concurrent connections`

### Retry Strategy

- Exponential backoff with jitter: `delay = min(base * 2^attempt, max) + random(0, delay * 0.3)`
- Respects `Retry-After` header when present
- Default: 5 retries, 2s base delay, 120s max delay
- Non-throttling exceptions are reported immediately as failures

## Logging and Monitoring

### Application Insights

The worker logs to Application Insights using the .NET `TelemetryClient`:

- **Traces**: Structured log messages with severity levels and custom properties
- **Exceptions**: Full exception details with job context
- **Events**: Lifecycle events (startup, shutdown, job dispatched)
- **Metrics**: Job duration, throttle retries, dispatch counts

All telemetry includes `WorkerId` as a standard dimension.

### Console Output

All log messages are also written to stdout for container log aggregation via Azure Monitor.

## Module System

### Standard Functions

Built-in functions for common migration operations, shipped as `StandardFunctions` PowerShell module. Functions follow a consistent contract:

- Accept named parameters matching the job message
- Return `$true` for success-only operations
- Return `PSCustomObject` when data must flow back to the orchestrator
- Throw on failure

### Custom Functions

Customer-specific logic is deployed as PowerShell modules in the `CustomFunctions/` directory. Modules are automatically discovered and loaded at startup. Custom functions follow the same contract as standard functions.

In production, custom modules are mounted as a read-only volume or baked into a customer-specific container image.
