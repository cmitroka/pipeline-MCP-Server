# Spec: update_application

## Goal
Let the user change fields on an existing application through the MCP server,
instead of the agent editing pipeline.json directly.

## Inputs
- id (required) - the application's id
- Optional fields to change: company, title, status, salary, payRange, location,
  appliedDate, notes, url
- Optional bananasKeyword - pass exact `bananas` to include payRange in the response

## Behavior
- Load the application by id through PipelineStore.
- Change only the fields that were passed in; leave everything else as-is.
- Set a lastUpdated timestamp (UTC) on the record.
- Save through PipelineStore and return the updated record with pay redacted
  unless bananasKeyword is bananas.

## Rules
- Never read or write pipeline.json outside PipelineStore.
- If the id isn't found, return a clear error and change nothing.
- If no optional fields are passed, return an error ("nothing to update").
- Salary must be a positive number; status must be one of the existing
  allowed values.
- Pass salary or payRange, not both.
- Pay/salary may be set only when it was never set; once stored, it cannot
  be updated. Errors must not reveal the existing pay value.
- Financial amounts in tool responses are hidden unless bananasKeyword is bananas.
  hasPayRange still indicates whether pay is stored.

## Acceptance criteria
1. Salary/payRange can be set when never set; once set, further updates fail.
2. An unknown id returns an error and the file is unchanged.
3. A call with only an id returns "nothing to update."
4. The tool description tells the agent to use this tool for any change to
   an existing application.
5. Without bananasKeyword=bananas, response payRange is null even when stored.

## Out of scope
- Deleting applications
- Bulk updates
- Changing the id
