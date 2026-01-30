I want you to refer to this architecture sketch and the chat titled "Automation subsystem architecture" for context on this project. Build a basic prototype of the PowerShell worker which will run in Azure Container Apps.

Each instance of the worker should be assigned an identifier that will be referenced in it's Service Bus subscription on the jobs topic. When the orchestrator enqueues a job for the worker, it will reference it by this identifier. For now, let's specify it on container start, along with the max parallelism for the worker within the container (i.e., the number of runspaces or threads that will process jobs).

On start, the tenant should initiate sessions using the MgGraph and Exchange PowerShell modules with a single tenant using modern app authentication. Note that this will not be the tenant hosting the Azure resources for this solution. It should retrieve the secret used for authentication (avoid certificates for now) from AKV using managed ID. It should also initiate the specified number of parallel runspaces or threads (I want you to make a decision on how best to manage parallel execution of jobs against the tenant using MgGraph and Exchange Online PowerShell, assuming a single app registration will be used for auth). It should also start listening on a Service Bus subscription for the jobs topic, filtering by it's worker ID.

When the worker receives a job via Service Bus, it will specify the function that needs to executed and the parameter values required for execution. The worker should dispatch the job to an available runspace or thread, calling the function specified in the job and supplying the parameters. The worker should then enqueue the results of function execution on the Service Bus results topic to notify the orchestrator. Functions will either return a boolean value indicating successful execution, or they'll return a PSObject that should be serialized and included in the results message. This latter return type will be used when the function needs to provide data to the orchestrator, for example in a situation where an updated value is found on the object being processed and needs to be updated in SQL batch data for use by subsequent functions in a runbook.

If the function throws an exception, the worker should evaluate it to determine if it's due to throttling for the MgGraph or Exchange PowerShell modules, in which case it should implement standard logic for retry and backoff instead of reporting execution failure to the orchestrator. Any other exception should be reported back to the orchestrator.

The standard function library should be implemented as a module that's loaded by the worker. The worker should also load any custom function modules it finds in it's working folder, which is how the worker will be extended with customer logic.

For now, I want you to add the following functions to the standard library. Refer to public Microsoft documentation on how to perform all functions via PowerShell modules.

    • Create new user (Entra)
    • Add user to group (Entra)
    • Remove user from group (Entra)
    • Invite the user to B2B collaboration (Entra)
    • Convert a B2B member or guest to an internal member, assigning a randomly generated password (Entra)
    • Change the UPN for a user (Entra)
    • Add a secondary email address to a mail user (Exchange)
    • Change the primary email address on a mail user (Exchange)
    • Change the external email address on a mail user (Exchange)
    • Assign an Exchange GUID and archive GUID (if provided) to a mail user (Exchange)
    • Check if an attribute matches a provided value, with support for multi-value attribute checking (one for Entra, one for Exchange)
    • Check if a user is a member of a group (one for Entra, one for Exchange)
    
Also add an example custom function module with a sample function.

The worker should handle execution logging using app insights or any alternative that you recommend based on standard Azure reference architectures for a solution like this.

The project should include all PowerShell code, container files, and documentation in MD that details its architecture and guidance for deployment in Azure. It should also include guidance and samples for enqueuing a job for the worker, along with a very simple PowerShell script that can be used for testing. That test script should accept a CSV as input when submitting a job, with a message enqueued for each row in the CSV.