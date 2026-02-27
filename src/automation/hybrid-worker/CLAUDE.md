# Claude Context -- Hybrid Worker

## What This Project Is

An on-premises PowerShell worker that runs as a Scheduled Task, part of the Migration Automation Toolkit's **automation subsystem**. It executes migration functions against on-premises (Active Directory, Exchange Server) and cloud services (SharePoint Online, Teams) using PS 5.1 PSSessions. Supports multi-forest AD environments with lazy connection validation.

## Project Structure

```
hybrid-worker/
+-- CLAUDE.md                              <- this file
+-- version.txt                            # Current version
+-- Start-HybridWorker.ps1                 # Launcher (invoked by Scheduled Task every N min)
+-- Install-HybridWorkerTask.ps1           # First-time setup (run as admin)
+-- Uninstall-HybridWorkerTask.ps1         # Clean removal
+-- Download-Dependencies.ps1              # Fetch NuGet packages for dotnet-libs/
+-- src/
|   +-- worker.ps1                         # Full worker (11-phase boot, job dispatch loop)
|   +-- config.ps1                         # JSON config loader + env var overrides
|   +-- logging.ps1                        # App Insights telemetry
|   +-- auth.ps1                           # SP cert auth + credential retrieval
|   +-- service-bus.ps1                    # SB integration (ClientCertificateCredential)
|   +-- ad-forest-manager.ps1              # Multi-forest config validation
|   +-- session-pool.ps1                   # PS 5.1 PSSession pool (single engine)
|   +-- job-dispatcher.ps1                 # Capability-gated dispatch + update check
|   +-- service-connections.ps1            # Per-service module scanning + catalog
|   +-- update-manager.ps1                 # Blob storage version check + zip download
|   +-- health-check.ps1                   # Health endpoint (disabled by default)
+-- modules/
|   +-- ADFunctions/                       # RequiredService = activeDirectory
|   |   +-- ADFunctions.psd1
|   |   +-- ADFunctions.psm1
|   |   +-- ADForestConnection.ps1         # Get-ADForestConnection, Reset-ADForestConnection
|   |   +-- ADOperations.ps1               # New-ADMigrationUser, Set-ADUserAttribute
|   +-- ExchangeServerFunctions/           # RequiredService = exchangeServer
|   |   +-- ExchangeServerFunctions.psd1
|   |   +-- ExchangeServerFunctions.psm1
|   |   +-- ExchangeServerOperations.ps1   # New-ExchangeRemoteMailbox
|   +-- SPOFunctions/                      # RequiredService = sharepointOnline
|   |   +-- SPOFunctions.psd1
|   |   +-- SPOFunctions.psm1
|   |   +-- SPOOperations.ps1              # New-MigrationSPOSite
|   +-- TeamsFunctions/                    # RequiredService = teams
|   |   +-- TeamsFunctions.psd1
|   |   +-- TeamsFunctions.psm1
|   |   +-- TeamsOperations.ps1            # New-MigrationTeam
|   +-- CustomFunctions/                   # Customer-specific extensibility modules
|       +-- SampleCustomFunctions/         # RequiredServices = @(all four)
|           +-- SampleCustomFunctions.psd1
|           +-- SampleCustomFunctions.psm1
|           +-- SampleFunctions.ps1        # 4 sample functions (one per service pattern)
+-- config/
|   +-- worker-config.example.json         # Example configuration file
+-- tests/
|   +-- Test-WorkerLocal.ps1               # Parse + structure + stale reference validation
+-- dotnet-libs/                           # .NET assemblies (fetched by Download-Dependencies.ps1)
```

## Key Architecture Decisions

- **Scheduled Task model**: A launcher (`Start-HybridWorker.ps1`) runs every N minutes. It peeks Service Bus -- if messages exist, it dot-sources `worker.ps1` for a full work cycle. No persistent process when idle.
- **Single engine**: PS 5.1 PSSession pool only -- all remaining modules (AD, Exchange Server, SPO, Teams) work in PS 5.1
- **Per-service modules**: Each module declares `RequiredService` in `PrivateData`, enabling selective loading based on enabled services
- **Capability gating**: Functions for disabled services are cataloged but not whitelisted; jobs get informative `CapabilityDisabledError` instead of generic rejection
- **Multi-forest AD**: Supports 20+ forests via config-driven `forests` array with lazy connection validation (`Get-ADForestConnection`)
- **Config-driven services**: Each service (activeDirectory, exchangeServer, sharepointOnline, teams) can be independently enabled/disabled
- **Service Bus SDK**: Azure.Messaging.ServiceBus .NET assembly, authenticated via `ClientCertificateCredential`
- **Self-update**: Polls Azure Blob Storage for new versions, downloads + stages, applies at next launcher tick
- **Certificate auth**: SP certificate in `Cert:\LocalMachine\My` for Azure resources
- **On-prem credentials**: Username/password stored as JSON secrets in Key Vault, retrieved at startup
- **Health check disabled by default**: No persistent HTTP endpoint; monitoring via App Insights `LauncherTick` events

## Launcher Flow (Start-HybridWorker.ps1)

Each tick (~6-12s when no work):
1. Apply pending update (staged file swap)
2. Load config
3. Read version.txt
4. Initialize App Insights logging
5. Connect-AzAccount (SP + cert)
6. Check for updates (Test-UpdateAvailable -> Install-WorkerUpdate)
7. Load SB assemblies, peek subscription for messages
8. Log `LauncherTick` event (version, messages found, update status)
9. Flush telemetry
10. No messages -> exit 0. Messages found -> dot-source worker.ps1

## Service Bus Message Format

Same as cloud-worker -- see `src/automation/cloud-worker/CLAUDE.md` for job/result message schemas.

## Tests

Run `pwsh -File tests/Test-WorkerLocal.ps1` -- validates parse correctness for all .ps1 files, module structure, manifest exports, config schema, launcher behavior, and verifies no stale references to removed components (RunspacePool, StandardFunctions, MgGraph, etc.).
