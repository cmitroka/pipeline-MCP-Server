using JobPipelineMcp;
using ModelContextProtocol.Protocol;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://127.0.0.1:3001");
builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1";

builder.Services.AddSingleton(new PipelineStore(ResolvePipelinePath()));
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
            "Use an application id from list_applications when company and role could match more than one row.";
    })
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();
var store = app.Services.GetRequiredService<PipelineStore>();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("JobPipeline");
logger.LogInformation("Pipeline file: {PipelineFile}", store.FilePath);
logger.LogInformation("MCP endpoint: http://127.0.0.1:3001/mcp");

app.MapMcp("/mcp");
app.Run();

static string ResolvePipelinePath()
{
    var configured = Environment.GetEnvironmentVariable("JOB_PIPELINE_FILE");
    if (!string.IsNullOrWhiteSpace(configured))
        return Path.GetFullPath(configured.Trim());

    return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "pipeline.json"));
}
