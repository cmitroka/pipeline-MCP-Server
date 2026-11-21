# Job Pipeline MCP

A local [Model Context Protocol](https://modelcontextprotocol.io) server for one job search. Cursor starts it as a console process and talks to it over stdin/stdout. Applications live in a `pipeline.json` file on this machine.

The server is a .NET 10 console app using the official [`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol) C# SDK and the stdio transport.

## What it stores

Each application has:

| Field | Meaning |
| --- | --- |
| `id` | Short id the tools use to tell rows apart |
| `company` | Company name |
| `role` | Role title |
| `stage` | Current stage |
| `dateApplied` | Date you applied (`YYYY-MM-DD`) |
| `lastUpdate` | Date of the latest note, or the applied date when there are no later notes |
| `payRange` | Free-text pay range |
| `link` | Posting or application URL |
| `notes` | Dated notes. A status update is stored as `"Stage: what happened"` |
| `stagesReached` | Stages the application has actually entered, oldest first. Conversion rates use this |

Stages, in funnel order:

1. Applied
2. Recruiter screen (also accepts "phone screen" and "screening")
3. Interview (also accepts "onsite")
4. Offer
5. Accepted

Rejected and Withdrawn close an application. They are outcomes, not funnel steps.

The file is created the first time an application is added. It is listed in `.gitignore` so notes stay on this machine. Remove that line if you want the file in git.

```json
{
  "applications": [
    {
      "id": "c8f96008",
      "company": "Northwind",
      "role": "Backend Engineer",
      "stage": "Interview",
      "dateApplied": "2026-11-01",
      "lastUpdate": "2026-11-12",
      "payRange": "$160-180k",
      "link": "https://example.com/jobs/1",
      "notes": [
        { "date": "2026-11-01", "text": "Applied via referral" },
        { "date": "2026-11-12", "text": "Interview: Recruiter booked a loop for Nov 18" }
      ],
      "stagesReached": ["Applied", "Interview"]
    }
  ]
}
```

## Tools

| Tool | Call it when |
| --- | --- |
| `list_applications` | You want to see the pipeline, check a company, or look up an id. Optional `stage` filter. |
| `add_application` | You applied, or you want to start tracking a company and role. |
| `update_status` | Something happened on an application already in the pipeline: a reply, interview, offer, rejection, withdrawal, or a note. |
| `pipeline_stats` | You want counts, conversion rates, or the roles with no update in 10 or more days. |

`pipeline_stats` reports:

- How many applications are in each stage, and how many are still open (Applied through Offer).
- Conversion between each pair of funnel stages. The rate is the share of applications that entered the next stage *after* the earlier one. Jumping from Applied to Interview does not count as a recruiter screen.
- Every application whose last update is 10 or more days ago. `stillOpen` is false for Accepted, Rejected, and Withdrawn.

`ratePercent` is null when nothing has reached the earlier stage yet, so an empty pipeline is not reported as 0%.

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build -c Release
```

Rebuild after code changes, then turn the MCP server off and on in Cursor so it loads the new dll.

## Register it in Cursor

Cursor starts stdio servers from `mcp.json`. A project file applies to this workspace. A global file applies to every workspace. If both define `job-pipeline`, the project file wins.

Stdout is the protocol channel. The config below runs the built dll, which does not print build output. `dotnet run` writes build messages to stdout and will break the connection.

`JOB_PIPELINE_FILE` is the data file. With it set, the server does not depend on the working directory Cursor happens to use.

### This project

Create `.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "job-pipeline": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "${workspaceFolder}/bin/Release/net10.0/JobPipelineMcp.dll"
      ],
      "env": {
        "JOB_PIPELINE_FILE": "${workspaceFolder}/pipeline.json"
      }
    }
  }
}
```

`${workspaceFolder}` is the folder that contains `.cursor`.

### Every workspace

Create `C:\Users\chris\.cursor\mcp.json` (merge this entry into `mcpServers` if the file already exists):

```json
{
  "mcpServers": {
    "job-pipeline": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "C:/Users/chris/Projects/JobPipelineMcp/bin/Release/net10.0/JobPipelineMcp.dll"
      ],
      "env": {
        "JOB_PIPELINE_FILE": "C:/Users/chris/Projects/JobPipelineMcp/pipeline.json"
      }
    }
  }
}
```

Then open **Cursor Settings → MCP** and enable **job-pipeline**. The first startup log line is the pipeline file path. Server logs are under **View → Output → MCP Logs**.

A useful first prompt:

> I applied to Northwind for a Backend Engineer role, pay range $160-180k. How does the pipeline look, and what has had no update in 10 days?

## Adding a Gmail sync tool later

Tools are classes marked `[McpServerToolType]`. `Program.cs` registers every one of them with `WithToolsFromAssembly()`, and `PipelineStore` is the shared singleton that reads and writes `pipeline.json`.

A Gmail tool can be a new class that reads mail and calls `PipelineStore.Add` or `PipelineStore.UpdateStatus`. Rebuild, restart the server, and the new tool is available. The host setup stays the same.
