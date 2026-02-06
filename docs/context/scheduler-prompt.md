Let's build a prototype of the scheduler, SQL state machine DB, and YAML runbook file. The scheduler function app will run on a timer trigger every 5 mins. It should iterate through each YAML runbook in the DB to determine what actions it needs to take.

The YAML runbook should specify at the top the SQL query that will be used to fetch associated batch data. The query will target Dataverse for scheduled migration batches (e.g., users scheduled to migrate March 3, 2026 at 8:00 PM UTC, sites scheduled to migrate March 6, 2026 at 5:00 PM UTC, etc.), or it will target Databricks SQL for remediation actions that should be dispatched immediately (e.g., provision missing groups, add/remove group members, address missing attribute values on users, etc.).

The runbook should specify which column indicates the batch start time, and any runbook steps that include an offset (e.g., T-5 days) will be calculated against this column value. Records that have a common value for this column will comprise the batch membership, and no other batch identifier is required from the query.

The runbook author will be responsible for adding a filter to the query that only returns batch data relevant to the scheduler. For example, if the first step in the batch has an offset of T-5 days, the runbook author would add a date-based filter to the query so only batches scheduled within the next 5 days are returned. This will avoid a situation where months of batch data will be returned by the query.

If the runbook author intends for the batch to be processed immediately, they can indicate that in the runbook instead of specifying the batch start time column, prompting the scheduler to assign current date/time as the batch start time in the DB for consistency with other batch data.
The runbook should also specify which column is the primary key (e.g., source Entra objectID).

When the scheduler evaluates the runbook (stored in a SQL table), it should create a corresponding table in the DB if one doesn't already exist and then update the runbook table with that info so it knows where batch data is stored for the runbook.

When you write this, include two sample runbooks: 1 for scheduled user migration sourced from Dataverse, and 1 for group membership changes from Databricks that should be dispatched immediately.

The query specified in the runbook could include any number of columns with data that will be used by the runbook. Some of these columns could be multi-valued (e.g., proxyAddresses), and we need a way to retrieve these values in the query and store them for use in runbook steps. Make a decision on how best to do this and include proxyAddresses (multi-valued string) as an example in the sample user migration runbook so I know how this should work.

Runbooks will need to be versioned in the DB, and I want you to include a function app that can be called by CI/CD pipelines to publish new runbook versions to the DB. That function app should disable previous versions but leave the associated table, and the scheduler on next run will pick up the latest version, execute the query, and create a new table with latest batch data.

When the new version is published, it should be specified whether overdue T- activities should be ignored or rerun at the time of version update. If rerun is specified, any T- activities that had already been completed for a batch under the previous runbook version can be ignored, and the scheduler will dispatch those again when it processes the new runbook version. For example, if a runbook includes a T-5 execution phase and a new runbook version is published at T-3, it should will fetch the batch data from SQL, recognize that the T-5 phase is overdue, and dispatch execution of that step to the orchestrator. This is its standard behavior. If, when the new version is published, it is specified that overdue T- phase should be ignored, they should only be ignored on first execution of the new runbook version and all subsequent executions should apply standard behavior.

All jobs/steps/functions should be idempotent to compensate for a scenario in which the same T- activities are rerun in this scenario.

Next, the runbook should specify batch initialization steps, such as setting up the batch in data migration tools. Whether these steps are rerun on version change should also be specified to the function that publishes the new version.

When the scheduler first detects a new batch (unique execution time), it needs to make record of it in the DB and dispatch initialization to the orchestrator, sending only a message with the "init" phase for the batch ID so the orchestrator knows that it should enqueue the associated steps for the runbook.

Every time the scheduler runs the query for the runbook, it should compare returned batch data to that in the DB and detect when members are added or removed. It should notify the orchestrator of these changes by enqueuing a message on Service Bus for the change. If the runbook includes a T-5 phase and a user is added at T-3, the orchestrator will react to this user add message by running catch up for all overdue steps in the T- phase.

The runbook should include a section specifying how the orchestrator should process a user removed event.

Each step in the runbook should specify the worker ID that will be responsible for execution, the function to be called by the worker, and the parameters that should be supplied in the job for the function to complete its work. It can also optionally include failure handling, referring to a rollback sequence in the runbook. The runbook could have multiple rollback sequences to accommodate failure at different steps.

The runbook needs a way to specify a polling step, for example to wait until an Entra attribute matches an expected value, indicating that Entra Connect sync has run. When the orchestrator hits this step, it will update the batch entry with a last polled date/time. Every execution, the scheduler needs to identify these polling steps in the runbook, identify the polling interval, and message the orchestrator when it's time to check again.

When you build the SQL schema, make sure to include the columns that will be needed by the orchestrator to track step progression and execution results (we'll build the orchestrator next and iterate on this as needed).

When you build this, include complete documentation for architecture, deployment guidance in Azure, and include enough code comments so it's relatively easy to follow what it's doing. Also document all of the information needed to build a compatible orchestrator so you have the necessary context to build that component in a later session.