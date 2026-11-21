using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace JobPipelineMcp;

[McpServerToolType]
public sealed class PipelineTools(PipelineStore store)
{
    [McpServerTool(Name = "list_applications", Title = "List applications", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description(
        "List job applications saved in the local pipeline, most recently updated first. " +
        "Call this when the user asks what they have applied to, wants the status of a company or role, or needs an id before a status update. " +
        "Pass stage to return only applications currently in that stage; omit it to return every application. " +
        "Stage names: Applied, Recruiter screen, Interview, Offer, Accepted, Rejected, Withdrawn. " +
        "Phone screen and screening match Recruiter screen. Onsite matches Interview.")]
    public string ListApplications(
        [Description("Optional current stage to keep. Omit to list every application. Applied, Recruiter screen, Interview, Offer, Accepted, Rejected, or Withdrawn.")]
        string? stage = null)
    {
        var applications = store.List(stage);
        return PipelineJson.Serialize(new
        {
            stage = string.IsNullOrWhiteSpace(stage) ? "all" : StageNames.Require(stage),
            count = applications.Count,
            applications,
        });
    }

    [McpServerTool(Name = "add_application", Title = "Add application", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Save a new job application in the local pipeline. " +
        "Call this when the user says they applied, wants to start tracking a company and role, or gives you a posting to remember. " +
        "Company and role are required. Stage defaults to Applied and dateApplied defaults to today. " +
        "Use this for a new application. Use update_status when an application already in the pipeline moves forward or gets a note.")]
    public string AddApplication(
        [Description("Company name, for example Northwind.")] string company,
        [Description("Role title, for example Backend Engineer.")] string role,
        [Description("Stage to start in. Defaults to Applied. Use Recruiter screen, Interview, Offer, Accepted, Rejected, or Withdrawn when the application is already past Applied.")]
        string? stage = null,
        [Description("Date the application was sent, as YYYY-MM-DD. Defaults to today.")]
        string? dateApplied = null,
        [Description("Optional pay range as text, for example $160-180k or 90-110 EUR/hour.")]
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
            application,
        });
    }

    [McpServerTool(Name = "update_status", Title = "Update status", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Set a stage on an existing application and append a dated note. " +
        "Call this when the user reports a recruiter reply, phone screen, interview, offer, acceptance, rejection, withdrawal, or any follow-up that belongs on an application already in the pipeline. " +
        "Pass id from list_applications when you have it. Otherwise pass company and role, which must match exactly one application. " +
        "The note is stored as \"Stage: note\" and dated with date (YYYY-MM-DD, default today). " +
        "Pass the application's current stage when the stage is unchanged and only the note is new.")]
    public string UpdateStatus(
        [Description("New stage. Applied, Recruiter screen, Interview, Offer, Accepted, Rejected, or Withdrawn.")]
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
        return PipelineJson.Serialize(new
        {
            message = $"Updated {application.Company}, {application.Role} (id {application.Id}) to {application.Stage}.",
            application,
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

    private static DateOnly ParseDate(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DateOnly.FromDateTime(DateTime.Today);

        if (!DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new McpException($"{name} must be YYYY-MM-DD. Received \"{value.Trim()}\".");

        return date;
    }
}
