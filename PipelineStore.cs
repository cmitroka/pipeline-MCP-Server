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

    public JobApplication? FindByCompanyRole(string company, string role)
    {
        company = company.Trim();
        role = role.Trim();

        lock (_gate)
        {
            return Load().Applications.FirstOrDefault(application =>
                string.Equals(application.Company, company, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(application.Role, role, StringComparison.OrdinalIgnoreCase));
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

    public JobApplication UpdateApplication(
        string id,
        string? company,
        string? title,
        string? status,
        double? salary,
        string? location,
        string? appliedDate,
        string? notes,
        string? url)
    {
        id = Required(id, "id");

        var hasCompany = !string.IsNullOrWhiteSpace(company);
        var hasTitle = !string.IsNullOrWhiteSpace(title);
        var hasStatus = !string.IsNullOrWhiteSpace(status);
        var hasSalary = salary.HasValue;
        var hasLocation = location is not null;
        var hasAppliedDate = !string.IsNullOrWhiteSpace(appliedDate);
        var hasNotes = !string.IsNullOrWhiteSpace(notes);
        var hasUrl = url is not null;

        if (!hasCompany && !hasTitle && !hasStatus && !hasSalary &&
            !hasLocation && !hasAppliedDate && !hasNotes && !hasUrl)
        {
            throw new McpException("Nothing to update. Pass at least one of: company, title, status, salary, location, appliedDate, notes, url.");
        }

        DateOnly? parsedApplied = null;
        if (hasAppliedDate)
        {
            if (!DateOnly.TryParseExact(
                    appliedDate!.Trim(),
                    "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var date))
            {
                throw new McpException($"appliedDate must be YYYY-MM-DD. Received \"{appliedDate.Trim()}\".");
            }

            parsedApplied = date;
        }

        string? canonicalStatus = null;
        if (hasStatus)
            canonicalStatus = StageNames.Require(status);

        if (hasSalary && salary is <= 0)
            throw new McpException("salary must be a positive number.");

        lock (_gate)
        {
            var file = Load();
            var application = Find(file, id, company: null, role: null);

            if (hasCompany)
                application.Company = company!.Trim();

            if (hasTitle)
                application.Role = title!.Trim();

            if (canonicalStatus is not null)
            {
                application.Stage = canonicalStatus;
                if (!application.StagesReached.Contains(canonicalStatus))
                    application.StagesReached.Add(canonicalStatus);
            }

            if (hasSalary)
            {
                if (!string.IsNullOrWhiteSpace(application.PayRange))
                {
                    throw new McpException(
                        "salary can only be set when it was not set at creation. " +
                        $"This application already has payRange \"{application.PayRange}\".");
                }

                application.PayRange = salary!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (hasLocation)
                application.Location = BlankToNull(location);

            if (parsedApplied is not null)
                application.DateApplied = parsedApplied.Value;

            if (hasUrl)
                application.Link = BlankToNull(url);

            var updatedOn = DateOnly.FromDateTime(DateTime.UtcNow);
            if (hasNotes)
            {
                application.Notes.Add(new Note
                {
                    Date = updatedOn,
                    Text = notes!.Trim(),
                });
            }

            application.LastUpdate = updatedOn;

            Save(file);
            return application;
        }
    }

    public JobApplication UpsertInterview(
        string applicationId,
        int round,
        DateOnly? interviewDate,
        string? notes)
    {
        applicationId = Required(applicationId, "id");
        if (round < 1)
            throw new McpException("round must be 1 or greater (1 = first interview).");

        lock (_gate)
        {
            var file = Load();
            var application = Find(file, applicationId, company: null, role: null);
            application.Interviews ??= [];

            var existing = application.Interviews.FirstOrDefault(item => item.Round == round);
            if (existing is null)
            {
                existing = new InterviewRound
                {
                    Id = NewId(application.Interviews.Select(item => item.Id).ToList()),
                    Round = round,
                    Contacts = [],
                };
                application.Interviews.Add(existing);
            }

            if (interviewDate.HasValue)
                existing!.Date = interviewDate;

            if (notes is not null)
                existing!.Notes = BlankToNull(notes);

            var updatedOn = DateOnly.FromDateTime(DateTime.UtcNow);
            existing!.LastUpdate = updatedOn;

            application.Stage = StageNames.Interview;
            if (!application.StagesReached.Contains(StageNames.Interview))
                application.StagesReached.Add(StageNames.Interview);

            application.LastUpdate = updatedOn;
            application.Interviews = application.Interviews.OrderBy(item => item.Round).ToList();

            Save(file);
            return application;
        }
    }

    public JobApplication UpsertInterviewContact(
        string applicationId,
        int round,
        string? contactId,
        string? name,
        string? title,
        string? notes)
    {
        applicationId = Required(applicationId, "id");
        if (round < 1)
            throw new McpException("round must be 1 or greater (1 = first interview).");

        var hasContactId = !string.IsNullOrWhiteSpace(contactId);
        var hasName = !string.IsNullOrWhiteSpace(name);
        if (!hasContactId && !hasName)
            throw new McpException("Pass name to add a contact, or contactId (and fields to change) to update one.");

        lock (_gate)
        {
            var file = Load();
            var application = Find(file, applicationId, company: null, role: null);
            application.Interviews ??= [];

            var interview = application.Interviews.FirstOrDefault(item => item.Round == round);
            if (interview is null)
            {
                // Creating a contact implies the round exists.
                interview = new InterviewRound
                {
                    Id = NewId(application.Interviews.Select(item => item.Id).ToList()),
                    Round = round,
                    Contacts = [],
                    LastUpdate = DateOnly.FromDateTime(DateTime.UtcNow),
                };
                application.Interviews.Add(interview);
            }

            interview.Contacts ??= [];
            InterviewContact contact;
            if (hasContactId)
            {
                contact = interview.Contacts.FirstOrDefault(item =>
                    string.Equals(item.Id, contactId!.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new McpException(
                        $"No contact with id \"{contactId!.Trim()}\" on interview round {round}. " +
                        "Call list_applications and use a contact id from interviews[].contacts.");

                if (hasName)
                    contact.Name = name!.Trim();
                if (title is not null)
                    contact.Title = BlankToNull(title);
                if (notes is not null)
                    contact.Notes = BlankToNull(notes);
            }
            else
            {
                contact = new InterviewContact
                {
                    Id = NewId(interview.Contacts.Select(item => item.Id).ToList()),
                    Name = name!.Trim(),
                    Title = BlankToNull(title),
                    Notes = BlankToNull(notes),
                };
                interview.Contacts.Add(contact);
            }

            var updatedOn = DateOnly.FromDateTime(DateTime.UtcNow);
            interview.LastUpdate = updatedOn;
            application.Stage = StageNames.Interview;
            if (!application.StagesReached.Contains(StageNames.Interview))
                application.StagesReached.Add(StageNames.Interview);
            application.LastUpdate = updatedOn;
            application.Interviews = application.Interviews.OrderBy(item => item.Round).ToList();

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
        application.Interviews ??= [];
        foreach (var interview in application.Interviews)
        {
            interview.Id = string.IsNullOrWhiteSpace(interview.Id)
                ? NewId(application.Interviews.Select(item => item.Id).ToList())
                : interview.Id.Trim();
            interview.Contacts ??= [];
            foreach (var contact in interview.Contacts)
            {
                contact.Id = string.IsNullOrWhiteSpace(contact.Id)
                    ? NewId(interview.Contacts.Select(item => item.Id).ToList())
                    : contact.Id.Trim();
                contact.Name = contact.Name?.Trim() ?? "";
            }
        }

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

    private static string NewId(IReadOnlyList<JobApplication> applications) =>
        NewId(applications.Select(application => application.Id).ToList());

    private static string NewId(IReadOnlyList<string> existingIds)
    {
        var used = existingIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
