using JobPipelineMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

var builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP JSON-RPC channel. Every log line goes to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

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
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();
var store = host.Services.GetRequiredService<PipelineStore>();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("JobPipeline");
logger.LogInformation("Pipeline file: {PipelineFile}", store.FilePath);

await host.RunAsync();

static string ResolvePipelinePath()
{
    var configured = Environment.GetEnvironmentVariable("JOB_PIPELINE_FILE");
    if (!string.IsNullOrWhiteSpace(configured))
        return Path.GetFullPath(configured.Trim());

    return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "pipeline.json"));
}
