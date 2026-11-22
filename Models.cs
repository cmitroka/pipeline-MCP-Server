namespace JobPipelineMcp;

public sealed class JobApplication
{
    public string Id { get; set; } = "";
    public string Company { get; set; } = "";
    public string Role { get; set; } = "";
    public string Stage { get; set; } = StageNames.Applied;
    public DateOnly DateApplied { get; set; }
    public DateOnly LastUpdate { get; set; }
    public string? PayRange { get; set; }
    public string? Location { get; set; }
    public string? Link { get; set; }
    public List<Note> Notes { get; set; } = [];

    // Stages this application has actually entered, oldest first.
    // Conversion rates use this list so a skipped stage is not invented later.
    public List<string> StagesReached { get; set; } = [];
}

public sealed class Note
{
    public DateOnly Date { get; set; }
    public string Text { get; set; } = "";
}

public sealed class PipelineFile
{
    public List<JobApplication> Applications { get; set; } = [];
}

public sealed record PipelineStats(
    DateOnly AsOf,
    int Total,
    int Open,
    IReadOnlyDictionary<string, int> CountsByStage,
    IReadOnlyList<StageConversion> ConversionRates,
    int StaleAfterDays,
    IReadOnlyList<StaleApplication> Stale,
    string Definition);

public sealed record StageConversion(
    string From,
    string To,
    int ReachedFrom,
    int ReachedTo,
    double? RatePercent);

public sealed record StaleApplication(
    string Id,
    string Company,
    string Role,
    string Stage,
    DateOnly LastUpdate,
    int DaysSinceUpdate,
    bool StillOpen);

public static class StageNames
{
    public const string Applied = "Applied";
    public const string RecruiterScreen = "Recruiter screen";
    public const string Interview = "Interview";
    public const string Offer = "Offer";
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";
    public const string Withdrawn = "Withdrawn";

    public const int StaleAfterDays = 10;

    public static readonly IReadOnlyList<string> Funnel =
    [
        Applied,
        RecruiterScreen,
        Interview,
        Offer,
        Accepted,
    ];

    public static readonly IReadOnlyList<string> All =
    [
        Applied,
        RecruiterScreen,
        Interview,
        Offer,
        Accepted,
        Rejected,
        Withdrawn,
    ];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [Applied] = Applied,
        [RecruiterScreen] = RecruiterScreen,
        ["recruiter"] = RecruiterScreen,
        ["screening"] = RecruiterScreen,
        ["phone screen"] = RecruiterScreen,
        ["phone-screen"] = RecruiterScreen,
        ["phonescreen"] = RecruiterScreen,
        [Interview] = Interview,
        ["onsite"] = Interview,
        ["on-site"] = Interview,
        ["on site"] = Interview,
        [Offer] = Offer,
        [Accepted] = Accepted,
        ["offer accepted"] = Accepted,
        [Rejected] = Rejected,
        ["rejection"] = Rejected,
        [Withdrawn] = Withdrawn,
        ["withdrew"] = Withdrawn,
    };

    public static bool IsOpen(string stage) =>
        stage is Applied or RecruiterScreen or Interview or Offer;

    public static bool TryCanonicalize(string? stage, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(stage))
            return false;

        return Aliases.TryGetValue(stage.Trim(), out canonical!);
    }

    public static string Require(string? stage)
    {
        if (TryCanonicalize(stage, out var canonical))
            return canonical;

        var shown = string.IsNullOrWhiteSpace(stage) ? "(empty)" : stage.Trim();
        throw new ModelContextProtocol.McpException(
            $"Unknown stage \"{shown}\". Use {string.Join(", ", All)}. " +
            "Phone screen and screening are stored as Recruiter screen. Onsite is stored as Interview.");
    }
}
