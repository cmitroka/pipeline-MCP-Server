using JobPipelineMcp;
using ModelContextProtocol.Protocol;

var builder = WebApplication.CreateBuilder(args);

// Do not call UseUrls here. Docker/Azure set ASPNETCORE_URLS (e.g. http://+:8080).
// UseUrls("http://127.0.0.1:3001") overrides that and breaks container port mapping.

builder.Services.AddSingleton(new PipelineStore(ResolvePipelinePath()));
builder.Services.AddSingleton<InboxReceiptImporter>();
builder.Services.AddHttpClient<LinkedInJobSearch>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
});
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "job-pipeline",
            Title = "Job Pipeline",
            Version = "1.0.0",
        };
        options.ServerInstructions =
            "Personal job-search pipeline stored in a local JSON file. " +
            "Funnel order is Applied, Recruiter screen, Interview, Offer, Accepted. " +
            "Rejected and Withdrawn close an application. " +
            "Interview rounds are numbered and usually map one conversation/person each " +
            "(Greg = round 1, Kathy = round 2). Prefer upsert_interview_contact and omit round when adding " +
            "so a new round is created per person. Use upsert_interview for round date/notes. " +
            "Do not use update_status alone to create separate rounds — plain Interview does not add rounds. " +
            "When the user asks to go through inbox receipts or add jobs from email confirmations, " +
            "call import_inbox_receipts once — do not open individual Gmail messages for that flow. " +
            "Use search_jobs to fetch public LinkedIn guest listings, then ask which ones to track with add_application. " +
            "Use update_application for any change to an existing application; do not edit pipeline.json directly. " +
            "Pay/salary can be set only when never set; once stored it cannot change. " +
            "Stored pay amounts are never returned by tools; hasPayRange only indicates whether pay is stored. " +
            "Use an application id from list_applications when company and role could match more than one row.";
    })
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();
var store = app.Services.GetRequiredService<PipelineStore>();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("JobPipeline");
logger.LogInformation("Pipeline file: {PipelineFile}", store.FilePath);
logger.LogInformation("Built at: {BuiltAt}", BuildInfo.BuiltAtUtc);
logger.LogInformation("MCP endpoint path: /mcp");

var apiKey = app.Configuration["MCP_API_KEY"];
app.Use(async (context, next) =>
{
    if (HttpMethods.IsGet(context.Request.Method) &&
        (context.Request.Path == "/" || context.Request.Path.StartsWithSegments("/health")))
    {
        await next();
        return;
    }

    if (string.IsNullOrEmpty(apiKey) ||
        context.Request.Headers["X-Api-Key"] != apiKey)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next();
});

app.MapGet("/", () => Results.Text(
    "Job Pipeline MCP is running.\nUse the Streamable HTTP endpoint at /mcp.\nHealth: /health\n",
    "text/plain"));
app.MapGet("/health", () => Results.Ok(new { status = "healthy", builtAt = BuildInfo.BuiltAtUtc }));
app.MapMcp("/mcp");
app.Run();

static string ResolvePipelinePath()
{
    var configured = Environment.GetEnvironmentVariable("JOB_PIPELINE_FILE");
    if (!string.IsNullOrWhiteSpace(configured))
        return Path.GetFullPath(configured.Trim());

    return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "pipeline.json"));
}
