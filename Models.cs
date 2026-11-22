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

    // Numbered interview rounds (1st, 2nd, 3rd, …) with people met and notes.
    // Funnel stage stays "Interview"; round detail lives here.
    public List<InterviewRound> Interviews { get; set; } = [];

    // Stages this application has actually entered, oldest first.
    // Conversion rates use this list so a skipped stage is not invented later.
    public List<string> StagesReached { get; set; } = [];
}

public sealed class Note
{
    public DateOnly Date { get; set; }
    public string Text { get; set; } = "";
}

public sealed class InterviewRound
{
    public string Id { get; set; } = "";
    public int Round { get; set; }
    public DateOnly? Date { get; set; }
    public string? Notes { get; set; }
    public DateOnly LastUpdate { get; set; }
    public List<InterviewContact> Contacts { get; set; } = [];
}

public sealed class InterviewContact
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public string? Notes { get; set; }
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

    private static readonly Dictionary<string, int> OrdinalWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["first"] = 1, ["1st"] = 1,
        ["second"] = 2, ["2nd"] = 2,
        ["third"] = 3, ["3rd"] = 3,
        ["fourth"] = 4, ["4th"] = 4,
        ["fifth"] = 5, ["5th"] = 5,
        ["sixth"] = 6, ["6th"] = 6,
        ["seventh"] = 7, ["7th"] = 7,
        ["eighth"] = 8, ["8th"] = 8,
        ["ninth"] = 9, ["9th"] = 9,
        ["tenth"] = 10, ["10th"] = 10,
    };

    public static bool IsOpen(string stage) =>
        stage is Applied or RecruiterScreen or Interview or Offer;

    public static bool TryCanonicalize(string? stage, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(stage))
            return false;

        var trimmed = stage.Trim();
        if (Aliases.TryGetValue(trimmed, out canonical!))
            return true;

        // "2nd interview", "interview 3", "second round" → Interview
        if (TryParseInterviewRound(trimmed, out _))
        {
            canonical = Interview;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses phrases like "2nd interview", "interview 3", "second round" into a 1-based round number.
    /// </summary>
    public static bool TryParseInterviewRound(string? text, out int round)
    {
        round = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var t = text.Trim().ToLowerInvariant();
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[#:.,]", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();

        // interview 2 / round 2 / loop 2
        var numbered = System.Text.RegularExpressions.Regex.Match(
            t, @"^(?:interview|round|loop)\s+(\d+)$");
        if (numbered.Success && int.TryParse(numbered.Groups[1].Value, out round) && round >= 1)
            return true;

        // 2nd interview / 2 interview / second interview
        var ordinal = System.Text.RegularExpressions.Regex.Match(
            t, @"^(\d+)(?:st|nd|rd|th)?\s+(?:interview|round|loop)$");
        if (ordinal.Success && int.TryParse(ordinal.Groups[1].Value, out round) && round >= 1)
            return true;

        var parts = t.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 &&
            OrdinalWords.TryGetValue(parts[0], out round) &&
            parts[1] is "interview" or "round" or "loop" or "interviews")
            return true;

        // Bare "Interview" / "onsite" is a stage only — not a specific round.
        // Numbered phrases above create distinct rounds (Greg = 1, Kathy = 2, …).
        return false;
    }

    public static string Require(string? stage)
    {
        if (TryCanonicalize(stage, out var canonical))
            return canonical;

        var shown = string.IsNullOrWhiteSpace(stage) ? "(empty)" : stage.Trim();
        throw new ModelContextProtocol.McpException(
            $"Unknown stage \"{shown}\". Use {string.Join(", ", All)}. " +
            "Phone screen and screening are stored as Recruiter screen. " +
            "Onsite / 2nd interview / interview 3 map to Interview (use upsert_interview for round detail).");
    }
}
