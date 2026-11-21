using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol;

namespace JobPipelineMcp;

public sealed class PipelineStore
{
    private readonly string _path;
    private readonly Lock _gate = new();

    public PipelineStore(string path)
    {
        _path = path;
    }

    public string FilePath => _path;

    public IReadOnlyList<JobApplication> List(string? stage)
    {
        string? canonical = string.IsNullOrWhiteSpace(stage) ? null : StageNames.Require(stage);

        lock (_gate)
        {
            var applications = Load().Applications;
            IEnumerable<JobApplication> query = applications;
            if (canonical is not null)
                query = query.Where(application => application.Stage == canonical);

            return query
                .OrderByDescending(application => application.LastUpdate)
                .ThenBy(application => application.Company, StringComparer.OrdinalIgnoreCase)
                .ThenBy(application => application.Role, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public JobApplication Add(
        string company,
        string role,
        string? stage,
        DateOnly dateApplied,
        string? payRange,
        string? link,
        string? notes)
    {
        company = Required(company, "company");
        role = Required(role, "role");
        var canonical = string.IsNullOrWhiteSpace(stage) ? StageNames.Applied : StageNames.Require(stage);

        lock (_gate)
        {
            var file = Load();
            var application = new JobApplication
            {
                Id = NewId(file.Applications),
                Company = company,
                Role = role,
                Stage = canonical,
                DateApplied = dateApplied,
                LastUpdate = dateApplied,
                PayRange = BlankToNull(payRange),
                Link = BlankToNull(link),
                StagesReached = InitialHistory(canonical),
            };

            var note = BlankToNull(notes);
            if (note is not null)
                application.Notes.Add(new Note { Date = dateApplied, Text = note });

            file.Applications.Add(application);
            Save(file);
            return application;
        }
    }

    public JobApplication UpdateStatus(
        string? id,
        string? company,
        string? role,
        string stage,
        string note,
        DateOnly noteDate)
    {
        var canonical = StageNames.Require(stage);
        note = Required(note, "note");

        lock (_gate)
        {
            var file = Load();
            var application = Find(file, id, company, role);
            application.Stage = canonical;
            if (!application.StagesReached.Contains(canonical))
                application.StagesReached.Add(canonical);

            application.Notes.Add(new Note
            {
                Date = noteDate,
                Text = $"{canonical}: {note}",
            });

            if (noteDate > application.LastUpdate)
                application.LastUpdate = noteDate;

            Save(file);
            return application;
        }
    }

    public PipelineStats GetStats(DateOnly today)
    {
        lock (_gate)
        {
            var applications = Load().Applications;
            var counts = new Dictionary<string, int>();
            foreach (var stage in StageNames.All)
                counts[stage] = applications.Count(application => application.Stage == stage);

            var conversions = new List<StageConversion>();
            for (var i = 0; i < StageNames.Funnel.Count - 1; i++)
            {
                var from = StageNames.Funnel[i];
                var to = StageNames.Funnel[i + 1];
                var reachedFrom = applications.Where(application => application.StagesReached.Contains(from)).ToList();
                var reachedTo = reachedFrom.Count(application => ReachedLater(application, from, to));
                double? rate = reachedFrom.Count == 0
                    ? null
                    : Math.Round(100.0 * reachedTo / reachedFrom.Count, 1);

                conversions.Add(new StageConversion(from, to, reachedFrom.Count, reachedTo, rate));
            }

            var stale = applications
                .Select(application => (Application: application, Days: today.DayNumber - application.LastUpdate.DayNumber))
                .Where(item => item.Days >= StageNames.StaleAfterDays)
                .OrderByDescending(item => item.Days)
                .ThenBy(item => item.Application.Company, StringComparer.OrdinalIgnoreCase)
                .Select(item => new StaleApplication(
                    item.Application.Id,
                    item.Application.Company,
                    item.Application.Role,
                    item.Application.Stage,
                    item.Application.LastUpdate,
                    item.Days,
                    StageNames.IsOpen(item.Application.Stage)))
                .ToList();

            return new PipelineStats(
                today,
                applications.Count,
                applications.Count(application => StageNames.IsOpen(application.Stage)),
                counts,
                conversions,
                StageNames.StaleAfterDays,
                stale,
                "countsByStage is each application's current stage. " +
                "conversionRates follow Applied → Recruiter screen → Interview → Offer → Accepted. " +
                "reachedFrom is how many entered the earlier stage. reachedTo is how many of those later entered the next stage. " +
                "A skipped stage is not filled in. Rejected and Withdrawn are outcomes, not funnel steps. " +
                "stale is every application whose lastUpdate is 10 or more days before asOf. " +
                "stillOpen is false for Accepted, Rejected, and Withdrawn.");
        }
    }

    private PipelineFile Load()
    {
        if (!File.Exists(_path))
            return new PipelineFile();

        try
        {
            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
                return new PipelineFile();

            var file = JsonSerializer.Deserialize<PipelineFile>(json, PipelineJson.Options) ?? new PipelineFile();
            file.Applications ??= [];
            foreach (var application in file.Applications)
                Normalize(application);

            return file;
        }
        catch (JsonException ex)
        {
            throw new McpException($"Could not read {_path}. It is not valid pipeline JSON. {ex.Message}");
        }
        catch (IOException ex)
        {
            throw new McpException($"Could not read {_path}. {ex.Message}");
        }
    }

    private void Save(PipelineFile file)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(file, PipelineJson.Options) + Environment.NewLine;
        var temp = _path + ".tmp";
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch (IOException)
            {
                // The original save error is the one the tool should report.
            }

            throw new McpException($"Could not save {_path}. {ex.Message}");
        }
    }

    private static void Normalize(JobApplication application)
    {
        application.Id = application.Id?.Trim() ?? "";
        application.Company = application.Company?.Trim() ?? "";
        application.Role = application.Role?.Trim() ?? "";
        application.Notes ??= [];
        application.StagesReached ??= [];

        if (StageNames.TryCanonicalize(application.Stage, out var stage))
            application.Stage = stage;

        var history = new List<string>();
        foreach (var entry in application.StagesReached)
        {
            if (StageNames.TryCanonicalize(entry, out var reached) && !history.Contains(reached))
                history.Add(reached);
        }

        if (history.Count == 0 && !string.IsNullOrWhiteSpace(application.Stage))
            history.Add(application.Stage);
        else if (!string.IsNullOrWhiteSpace(application.Stage) && !history.Contains(application.Stage))
            history.Add(application.Stage);

        application.StagesReached = history;
    }

    private static JobApplication Find(PipelineFile file, string? id, string? company, string? role)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            var match = file.Applications.FirstOrDefault(application =>
                string.Equals(application.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                throw new McpException(
                    $"No application with id \"{id.Trim()}\". Call list_applications to see current ids.");
            }

            return match;
        }

        var companyName = company?.Trim();
        var roleName = role?.Trim();
        if (string.IsNullOrEmpty(companyName) || string.IsNullOrEmpty(roleName))
        {
            throw new McpException(
                "Pass id, or pass both company and role. Call list_applications to look up the id.");
        }

        var matches = file.Applications
            .Where(application =>
                string.Equals(application.Company, companyName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(application.Role, roleName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
            return matches[0];

        if (matches.Count == 0)
        {
            throw new McpException(
                $"No application for {companyName} / {roleName}. Call list_applications to see what is tracked.");
        }

        var choices = string.Join(
            "; ",
            matches.Select(application =>
                $"{application.Id} ({application.Stage}, updated {application.LastUpdate:yyyy-MM-dd})"));
        throw new McpException(
            $"More than one application matches {companyName} / {roleName}. Pass an id. Matches: {choices}");
    }

    private static List<string> InitialHistory(string stage)
    {
        if (stage == StageNames.Applied)
            return [StageNames.Applied];

        return [StageNames.Applied, stage];
    }

    private static string NewId(IReadOnlyList<JobApplication> applications)
    {
        var used = applications.Select(application => application.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            if (used.Add(id))
                return id;
        }

        return Guid.NewGuid().ToString("N");
    }

    private static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new McpException($"{name} is required.");

        return value.Trim();
    }

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ReachedLater(JobApplication application, string from, string to)
    {
        var fromIndex = application.StagesReached.IndexOf(from);
        var toIndex = application.StagesReached.IndexOf(to);
        return fromIndex >= 0 && toIndex > fromIndex;
    }
}

internal static class PipelineJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(object value) =>
        JsonSerializer.Serialize(value, value.GetType(), Options);
}
