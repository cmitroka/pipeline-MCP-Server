# Job Pipeline MCP

A local [Model Context Protocol](https://modelcontextprotocol.io) server for one job search. You run it as an ASP.NET Core process and Cursor talks to it over Streamable HTTP. Applications live in a `pipeline.json` file on this machine.

The server is a .NET 10 web app using the official [`ModelContextProtocol.AspNetCore`](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore) C# SDK.

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
| `search_jobs` | You want public LinkedIn guest listings to choose from before calling `add_application`. |
| `import_inbox_receipts` | You want to scan Gmail application confirmations and add new jobs without opening each email in the model. |

`pipeline_stats` reports:

- How many applications are in each stage, and how many are still open (Applied through Offer).
- Conversion between each pair of funnel stages. The rate is the share of applications that entered the next stage *after* the earlier one. Jumping from Applied to Interview does not count as a recruiter screen.
- Every application whose last update is 10 or more days ago. `stillOpen` is false for Accepted, Rejected, and Withdrawn.

`ratePercent` is null when nothing has reached the earlier stage yet, so an empty pipeline is not reported as 0%.

## Run locally

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
$env:ASPNETCORE_URLS = "http://127.0.0.1:8080"
dotnet run
```

That matches the Cursor URLs below (`http://127.0.0.1:8080/mcp`). `/health` is for probes. Leave the process running while Cursor uses the tools.

Optional env vars:

| Variable | Purpose |
| --- | --- |
| `JOB_PIPELINE_FILE` | Pin the data file path (default: `pipeline.json` in the working directory) |
| `MCP_API_KEY` | Require an `X-Api-Key` header on `/mcp` |
| `GMAIL_ADDRESS` | Gmail address for `import_inbox_receipts` (IMAP) |
| `GMAIL_APP_PASSWORD` | Gmail [app password](https://myaccount.google.com/apppasswords) (POC; spaces optional) |
| `IMAP_HOST` / `IMAP_PORT` | Defaults `imap.gmail.com` / `993` |

Docker example with inbox import:

```powershell
docker run --rm -p 8080:8080 `
  -v pipeline-data:/data `
  -e JOB_PIPELINE_FILE=/data/pipeline.json `
  -e MCP_API_KEY=mitrokatest `
  -e GMAIL_ADDRESS=you@gmail.com `
  -e GMAIL_APP_PASSWORD=xxxx-xxxx-xxxx-xxxx `
  pipeline-mcp
```

The `pipeline-data` volume keeps `pipeline.json` after the container exits.

Typical prompt after that: “Go through inbox receipts and add new jobs to the pipeline.” The model should call `import_inbox_receipts` once.

## Deploy to Azure (GitHub Actions)

Pushes to `main` run `.github/workflows/deploy.yml`. You can also run it by hand: GitHub → **Actions** → **Deploy to Azure Container Apps** → **Run workflow** (branch `main`). The workflow builds the `Dockerfile`, pushes to ACR `cjmpipelinemcp`, and updates Container App `pipeline-mcp` in resource group `pipeline-mcp-rg`.

Repo secrets required:

| Secret | Purpose |
| --- | --- |
| `AZURE_CREDENTIALS` | Service principal JSON from `az ad sp create-for-rbac ... --sdk-auth` |
| `MCP_API_KEY` | Value sent to the app as `MCP_API_KEY` |

After a successful run, the MCP URL is:

`https://<your-container-app-fqdn>/mcp`

Keep the real FQDN in a local file such as `Notes` (gitignored), not in the repo.

## Register it in Cursor

HTTP servers are registered by URL in `mcp.json`. A project file applies to this workspace. A global file applies to every workspace. If both define `job-pipeline`, the project file wins.

Start the server first (`dotnet run` with `ASPNETCORE_URLS` as above, or Docker on port 8080). Cursor does not launch it for you when the transport is HTTP.

### This project

Create `.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "job-pipeline": {
      "url": "http://127.0.0.1:8080/mcp"
    }
  }
}
```

### Every workspace

Create `C:\Users\chris\.cursor\mcp.json` (merge this entry into `mcpServers` if the file already exists):

```json
{
  "mcpServers": {
    "job-pipeline": {
      "url": "http://127.0.0.1:8080/mcp"
    }
  }
}
```

Then open **Cursor Settings → MCP** and enable **job-pipeline**. Startup logs show the pipeline file path and the MCP endpoint. Server logs stay in the `dotnet run` terminal.

A useful first prompt:

> I applied to Northwind for a Backend Engineer role, pay range $160-180k. How does the pipeline look, and what has had no update in 10 days?

## Extending tools

Tools are classes marked `[McpServerToolType]`. `Program.cs` registers every one of them with `WithToolsFromAssembly()`, and `PipelineStore` is the shared singleton that reads and writes `pipeline.json`. Rebuild, restart the server, and new tools are available.
