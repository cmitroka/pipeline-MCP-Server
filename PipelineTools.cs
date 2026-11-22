using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace JobPipelineMcp;

[McpServerToolType]
public sealed class PipelineTools(PipelineStore store, LinkedInJobSearch jobSearch, InboxReceiptImporter inbox)
{
    [McpServerTool(Name = "list_applications", Title = "List applications", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "List job applications saved in the local pipeline, most recently updated first. " +
        "Call this when the user asks what they have applied to, wants the status of a company or role, or needs an id before a status update. " +
        "Pass stage to return only applications currently in that stage; omit it to return every application. " +
        "Stage names: Applied, Recruiter screen, Interview, Offer, Accepted, Rejected, Withdrawn. " +
        "Phone screen and screening match Recruiter screen. Onsite / 2nd interview map to Interview. " +
        "Numbered interview rounds and people met live under applications[].interviews. " +
        "Stored pay amounts are never returned; hasPayRange only shows whether pay is stored.")]
    public string ListApplications(
        [Description("Optional current stage to keep. Omit to list every application. Applied, Recruiter screen, Interview, Offer, Accepted, Rejected, or Withdrawn.")]
        string? stage = null)
    {
        var applications = store.List(stage);
        return PipelineJson.Serialize(new
        {
            stage = string.IsNullOrWhiteSpace(stage) ? "all" : StageNames.Require(stage),
            count = applications.Count,
            applications = FinancialAccess.PresentAll(applications),
        });
    }

    [McpServerTool(Name = "add_application", Title = "Add application", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Save a new job application in the local pipeline. " +
        "Call this when the user says they applied, wants to start tracking a company and role, or gives you a posting to remember. " +
        "Company and role are required. Stage defaults to Applied and dateApplied defaults to today. " +
        "Use this for a new application. " +
        "Use update_application for any change to an existing application (company, title, status, salary, payRange, location, dates, notes, url). " +
        "Use update_status when you only need to move the stage and append a stage-dated note. " +
        "Stored pay amounts are never returned; hasPayRange only shows whether pay is stored.")]
    public string AddApplication(
        [Description("Company name, for example Northwind.")] string company,
        [Description("Role title, for example Backend Engineer.")] string role,
        [Description("Stage to start in. Defaults to Applied. Use Recruiter screen, Interview, Offer, Accepted, Rejected, or Withdrawn when the application is already past Applied.")]
        string? stage = null,
        [Description("Date the application was sent, as YYYY-MM-DD. Defaults to today.")]
        string? dateApplied = null,
        [Description("Optional pay range as text, for example $160-180k or 90-110 EUR/hour. Stored but never returned by tools.")]
        string? payRange = null,
        [Description("Optional URL for the posting or the application.")]
        string? link = null,
        [Description("Optional note recorded on the applied date, such as a referral or which site it was sent from.")]
        string? notes = null)
    {
        var appliedOn = ParseDate(dateApplied, "dateApplied");
        var application = store.Add(company, role, stage, appliedOn, payRange, link, notes);
        return PipelineJson.Serialize(new
        {
            message = $"Added {application.Company}, {application.Role} (id {application.Id}) at stage {application.Stage}.",
            application = FinancialAccess.Present(application),
        });
    }

    [McpServerTool(Name = "update_application", Title = "Update application", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Change fields on an existing application by id. " +
        "Use this tool for any change to an existing application: company, title (role), status (stage), salary, payRange, location, appliedDate, notes, or url. " +
        "Pass only the fields to change; omitted fields stay as-is. " +
        "salary or payRange may be set only when pay was never set; once set, it cannot be changed. status must be an allowed stage. " +
        "Do not edit pipeline.json directly — call this tool instead. " +
        "For a stage move that also needs a \"Stage: note\" history line, update_status is still fine. " +
        "Stored pay amounts are never returned; hasPayRange only shows whether pay is stored.")]
    public string UpdateApplication(
        [Description("Application id from list_applications.")]
        string id,
        [Description("New company name.")]
        string? company = null,
        [Description("New role title.")]
        string? title = null,
        [Description("New stage. Applied, Recruiter screen, Interview, Offer, Accepted, Rejected, or Withdrawn.")]
        string? status = null,
        [Description("Salary as a positive number. Only allowed when pay was never set. Pass salary or payRange, not both. Value is stored but never returned.")]
        double? salary = null,
        [Description("Pay range as text, for example $160-180k. Only allowed when pay was never set. Pass salary or payRange, not both. Value is stored but never returned.")]
        string? payRange = null,
        [Description("Work location text, for example Remote or Philadelphia, PA. Pass empty string to clear.")]
        string? location = null,
        [Description("Applied date as YYYY-MM-DD.")]
        string? appliedDate = null,
        [Description("Note to append on the application (dated with today's UTC date).")]
        string? notes = null,
        [Description("Posting or application URL. Pass empty string to clear.")]
        string? url = null)
    {
        var application = store.UpdateApplication(
            id, company, title, status, salary, payRange, location, appliedDate, notes, url);
        return PipelineJson.Serialize(new
        {
            message = $"Updated application {application.Id} ({application.Company}, {application.Role}).",
            application = FinancialAccess.Present(application),
        });
    }

    [McpServerTool(Name = "update_status", Title = "Update status", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Set a stage on an existing application and append a dated note. " +
        "Call this when the user reports a recruiter reply, phone screen, interview, offer, acceptance, rejection, withdrawal, or any follow-up that belongs on an application already in the pipeline. " +
        "For other field edits (company, title, salary, payRange, location, url, applied date), use update_application instead. " +
        "Pass id from list_applications when you have it. Otherwise pass company and role, which must match exactly one application. " +
        "The note is stored as \"Stage: note\" and dated with date (YYYY-MM-DD, default today). " +
        "Pass the application's current stage when the stage is unchanged and only the note is new. " +
        "Numbered stages like \"2nd interview\" create/update that round. " +
        "Plain \"Interview\" only moves the funnel stage — use upsert_interview_contact (omit round) so each person gets their own round (Greg=1, Kathy=2).")]
    public string UpdateStatus(
        [Description("New stage. Applied, Recruiter screen, Interview, Offer, Accepted, Rejected, or Withdrawn. Or a numbered interview like 2nd interview.")]
        string stage,
        [Description("What happened. Stored with the stage and a date, for example a recruiter reply or an interview time.")]
        string note,
        [Description("Application id from list_applications. Used when present, even if company and role are also passed.")]
        string? id = null,
        [Description("Company name. Used with role only when id is omitted.")]
        string? company = null,
        [Description("Role title. Used with company only when id is omitted.")]
        string? role = null,
        [Description("Date of this update, as YYYY-MM-DD. Defaults to today. An older date stays on the note and does not move lastUpdate backward.")]
        string? date = null)
    {
        var noteDate = ParseDate(date, "date");
        var application = store.UpdateStatus(id, company, role, stage, note, noteDate);

        // Only numbered phrases create/update a specific round. Bare "Interview" must not reuse round 1.
        if (StageNames.TryParseInterviewRound(stage, out var round))
            application = store.UpsertInterview(application.Id, round, interviewDate: noteDate, notes: null);

        return PipelineJson.Serialize(new
        {
            message = $"Updated {application.Company}, {application.Role} (id {application.Id}) to {application.Stage}.",
            application = FinancialAccess.Present(application),
        });
    }

    [McpServerTool(Name = "upsert_interview", Title = "Upsert interview round", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Create or update a numbered interview round on an application. " +
        "Pass round (1, 2, 3, …) to target that round, or omit round to append a new round. " +
        "Call this for round date/notes. For who they met in a conversation, prefer upsert_interview_contact " +
        "(omit round to give each person their own round: Greg=Interview 1, Kathy=Interview 2). " +
        "Sets the application stage to Interview.")]
    public string UpsertInterview(
        [Description("Application id from list_applications.")]
        string id,
        [Description("Interview round number. 1 = first, 2 = second, …. Omit to append the next new round.")]
        int? round = null,
        [Description("Date of the interview as YYYY-MM-DD. Optional.")]
        string? date = null,
        [Description("Notes about the round overall (agenda, outcome). Optional; pass empty string to clear.")]
        string? notes = null)
    {
        DateOnly? interviewDate = null;
        if (!string.IsNullOrWhiteSpace(date))
            interviewDate = ParseDate(date, "date");

        var application = store.UpsertInterview(id, round, interviewDate, notes);
        var interview = round is not null
            ? application.Interviews.First(item => item.Round == round.Value)
            : application.Interviews.OrderByDescending(item => item.Round).First();
        return PipelineJson.Serialize(new
        {
            message = $"Interview round {interview.Round} saved on {application.Company}, {application.Role} (id {application.Id}).",
            interview,
            application = FinancialAccess.Present(application),
        });
    }

    [McpServerTool(Name = "upsert_interview_contact", Title = "Upsert interview contact", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Add or update a person met in an interview conversation. " +
        "Preferred for \"I interviewed with Greg, then Kathy\": omit round when adding so each person gets a new round " +
        "(Greg → round 1, Kathy → round 2). " +
        "Pass round only when adding someone to an existing round, or contactId to update that person. " +
        "Optional date stamps the round.")]
    public string UpsertInterviewContact(
        [Description("Application id from list_applications.")]
        string id,
        [Description("Interview round number. Omit when adding a new conversation/person so a new round is created.")]
        int? round = null,
        [Description("Contact id from interviews[].contacts when updating an existing person. Omit when adding.")]
        string? contactId = null,
        [Description("Person's name. Required when adding; optional when updating.")]
        string? name = null,
        [Description("Person's title, for example Engineering Manager. Pass empty string to clear.")]
        string? title = null,
        [Description("Notes on what you spoke about with this person. Pass empty string to clear.")]
        string? notes = null,
        [Description("Date of this conversation as YYYY-MM-DD. Optional.")]
        string? date = null)
    {
        DateOnly? interviewDate = null;
        if (!string.IsNullOrWhiteSpace(date))
            interviewDate = ParseDate(date, "date");

        var application = store.UpsertInterviewContact(id, round, contactId, name, title, notes, interviewDate);

        InterviewRound interview;
        InterviewContact contact;
        if (!string.IsNullOrWhiteSpace(contactId))
        {
            interview = application.Interviews.First(i =>
                i.Contacts.Any(c => string.Equals(c.Id, contactId.Trim(), StringComparison.OrdinalIgnoreCase)));
            contact = interview.Contacts.First(c =>
                string.Equals(c.Id, contactId.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            interview = round is not null
                ? application.Interviews.First(item => item.Round == round.Value)
                : application.Interviews.OrderByDescending(item => item.Round).First();
            contact = interview.Contacts.Last();
        }

        return PipelineJson.Serialize(new
        {
            message = $"Contact {contact.Name} saved on interview round {interview.Round} for {application.Company}, {application.Role}.",
            contact,
            interview,
            application = FinancialAccess.Present(application),
        });
    }

    [McpServerTool(Name = "pipeline_stats", Title = "Pipeline stats", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "Summarize the job search pipeline. " +
        "Call this when the user asks how the search is going, where applications are concentrated, what the conversion rate is, or which roles have gone quiet. " +
        "Returns a count for every stage, how many are still open, conversion rates along Applied → Recruiter screen → Interview → Offer → Accepted, and every application with no update in 10 or more days. " +
        "A conversion counts a next stage only when that stage was recorded after the earlier one. Skipping a stage does not count as reaching it. " +
        "stillOpen is false for Accepted, Rejected, and Withdrawn.")]
    public string PipelineStats() =>
        PipelineJson.Serialize(store.GetStats(DateOnly.FromDateTime(DateTime.Today)));

    [McpServerTool(Name = "search_jobs", Title = "Search LinkedIn jobs", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = true)]
    [Description(
        "Search public LinkedIn guest job listings and return title, company, location, link, and listed time. " +
        "Call this when the user wants new roles to consider, a LinkedIn search, or help picking postings before tracking them. " +
        "Do not invent jobs. Show the returned listings and let the user choose which ones to keep. " +
        "After they pick one, call add_application with that company, role/title, and link. " +
        "Defaults match a Downingtown PA search for software development/engineering manager or director of software, past week, remote/hybrid, mid-senior/director. " +
        "Pass searchUrl to reuse a full LinkedIn jobs/search URL. Use start=10, 20, … for the next page. " +
        "LinkedIn may rate-limit; if that happens, wait and retry.")]
    public async Task<string> SearchJobs(
        [Description("Optional keywords override. Defaults to software development/engineering manager OR director of software.")]
        string? keywords = null,
        [Description("Optional location override. Defaults to Downingtown, Pennsylvania.")]
        string? location = null,
        [Description("Optional distance in miles. Defaults to 50.")]
        int? distance = null,
        [Description("Optional LinkedIn f_TPR time filter, for example r86400 (day), r604800 (week), r2592000 (month). Defaults to r604800.")]
        string? timePosted = null,
        [Description("Optional LinkedIn f_WT workplace types. 1=on-site, 2=remote, 3=hybrid. Defaults to 2,3.")]
        string? workplaceTypes = null,
        [Description("Optional LinkedIn f_E experience levels. Defaults to 4,5 (mid-senior, director).")]
        string? experienceLevels = null,
        [Description("Optional full LinkedIn jobs/search URL. When set, its query string is used instead of the other filters.")]
        string? searchUrl = null,
        [Description("Result offset for paging. 0 is the first page, then 10, 20, and so on.")]
        int start = 0,
        CancellationToken cancellationToken = default)
    {
        var (jobs, sourceUrl) = await jobSearch.SearchAsync(
            keywords,
            location,
            distance,
            timePosted,
            workplaceTypes,
            experienceLevels,
            searchUrl,
            start,
            cancellationToken);

        return PipelineJson.Serialize(new
        {
            count = jobs.Count,
            start,
            sourceUrl,
            nextStart = start + Math.Max(jobs.Count, 10),
            instruction = "Present these jobs to the user. When they choose one, call add_application with company, role (title), and link.",
            jobs,
        });
    }

    [McpServerTool(Name = "import_inbox_receipts", Title = "Import inbox receipts", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = true)]
    [Description(
        "Scan Gmail for job-application confirmation emails (thank-you-for-applying / application received), " +
        "parse company, role, and date on the server, dedupe against the pipeline, and optionally add new rows. " +
        "Call this when the user says to go through inbox receipts and add new jobs to the pipeline. " +
        "Do not call Gmail tools or open individual emails for this flow — this tool already returns compact results. " +
        "Requires GMAIL_ADDRESS and GMAIL_APP_PASSWORD on the server (IMAP app password POC). " +
        "Defaults: last 30 days, up to 50 messages, addNew=true. Pass addNew=false to preview only.")]
    public async Task<string> ImportInboxReceipts(
        [Description("How many days of mail to scan. Defaults to 30.")]
        int daysBack = 30,
        [Description("Max messages to inspect after search. Defaults to 50.")]
        int maxMessages = 50,
        [Description("When true (default), add parsed receipts that are not already in the pipeline. When false, only report candidates.")]
        bool addNew = true,
        CancellationToken cancellationToken = default)
    {
        var receipts = await inbox.FetchAsync(daysBack, maxMessages, cancellationToken);

        var added = new List<object>();
        var alreadyTracked = new List<object>();
        var candidates = new List<object>();
        var needsReview = new List<object>();

        foreach (var receipt in receipts)
        {
            if (!receipt.Parsed || receipt.Company is null || receipt.Role is null)
            {
                needsReview.Add(new
                {
                    receipt.Subject,
                    receipt.From,
                    dateApplied = receipt.DateApplied.ToString("yyyy-MM-dd"),
                    receipt.Company,
                    receipt.Role,
                    confidence = receipt.Confidence,
                    reason = "Could not parse both company and role",
                });
                continue;
            }

            var existing = store.FindByCompanyRole(receipt.Company, receipt.Role);
            if (existing is not null)
            {
                alreadyTracked.Add(new
                {
                    existing.Id,
                    existing.Company,
                    existing.Role,
                    existing.Stage,
                    receiptSubject = receipt.Subject,
                    dateApplied = receipt.DateApplied.ToString("yyyy-MM-dd"),
                });
                continue;
            }

            // Only high-confidence parses auto-add. Medium/low stay in needsReview.
            if (receipt.Confidence != "high")
            {
                needsReview.Add(new
                {
                    receipt.Subject,
                    receipt.From,
                    dateApplied = receipt.DateApplied.ToString("yyyy-MM-dd"),
                    receipt.Company,
                    receipt.Role,
                    confidence = receipt.Confidence,
                    reason = "Parsed but not high confidence — confirm before adding",
                });
                continue;
            }

            if (!addNew)
            {
                candidates.Add(new
                {
                    receipt.Subject,
                    receipt.From,
                    dateApplied = receipt.DateApplied.ToString("yyyy-MM-dd"),
                    receipt.Company,
                    receipt.Role,
                    confidence = receipt.Confidence,
                });
                continue;
            }

            var application = store.Add(
                receipt.Company,
                receipt.Role,
                StageNames.Applied,
                receipt.DateApplied,
                payRange: null,
                link: null,
                notes: $"Imported from inbox: {receipt.Subject}");

            added.Add(new
            {
                application.Id,
                application.Company,
                application.Role,
                dateApplied = application.DateApplied.ToString("yyyy-MM-dd"),
                receipt.Subject,
                confidence = receipt.Confidence,
            });
        }

        return PipelineJson.Serialize(new
        {
            daysBack,
            maxMessages,
            addNew,
            scanned = receipts.Count,
            addedCount = added.Count,
            alreadyTrackedCount = alreadyTracked.Count,
            candidateCount = candidates.Count,
            needsReviewCount = needsReview.Count,
            instruction =
                "Summarize counts for the user. Only high-confidence receipts are auto-added. " +
                "If addNew was false, candidates are high-confidence preview rows. " +
                "For needsReview, ask before calling add_application.",
            added,
            alreadyTracked,
            candidates,
            needsReview,
        });
    }

    private static DateOnly ParseDate(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DateOnly.FromDateTime(DateTime.Today);

        if (!DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new McpException($"{name} must be YYYY-MM-DD. Received \"{value.Trim()}\".");

        return date;
    }
}
