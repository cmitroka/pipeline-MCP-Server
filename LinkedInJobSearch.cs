using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using ModelContextProtocol;

namespace JobPipelineMcp;

public sealed record JobListing(
    string Title,
    string Company,
    string? Location,
    string Link,
    string? ListedAt);

public sealed class LinkedInJobSearch(HttpClient http)
{
    // Guest HTML card markup LinkedIn still serves for public job search.
    private static readonly Regex CardPattern = new(
        """
        data-entity-urn="urn:li:jobPosting:(?<id>\d+)"(?<body>[\s\S]*?)(?=data-entity-urn="urn:li:jobPosting:|$)
        """,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TitlePattern = new(
        """base-search-card__title[^>]*>(?<v>[\s\S]*?)</h3>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CompanyPattern = new(
        """base-search-card__subtitle[^>]*>\s*(?:<a[^>]*>)?(?<v>[\s\S]*?)(?:</a>)?\s*</h4>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LocationPattern = new(
        """job-search-card__location[^>]*>(?<v>[\s\S]*?)</span>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LinkPattern = new(
        """href="(?<v>https://www\.linkedin\.com/jobs/view/[^"?]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ListedAtPattern = new(
        """<time[^>]*(?:datetime="(?<dt>[^"]+)")?[^>]*>(?<v>[\s\S]*?)</time>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public const string DefaultKeywords =
        "\"software development manager\" OR \"software engineering manager\" OR \"director of software\"";

    public const string DefaultLocation = "Downingtown, Pennsylvania";
    public const int DefaultDistance = 50;
    public const string DefaultTimeFilter = "r604800"; // past week
    public const string DefaultWorkplaceTypes = "2,3"; // remote, hybrid
    public const string DefaultExperienceLevels = "4,5"; // mid-senior, director

    private const string GuestApiBase =
        "https://www.linkedin.com/jobs-guest/jobs/api/seeMoreJobPostings/search";

    public async Task<(IReadOnlyList<JobListing> Jobs, string SourceUrl)> SearchAsync(
        string? keywords,
        string? location,
        int? distance,
        string? timePosted,
        string? workplaceTypes,
        string? experienceLevels,
        string? searchUrl,
        int start,
        CancellationToken cancellationToken)
    {
        if (start < 0)
            throw new McpException("start must be 0 or greater.");

        var guestUrl = BuildGuestApiUrl(
            keywords,
            location,
            distance,
            timePosted,
            workplaceTypes,
            experienceLevels,
            searchUrl,
            start);

        using var request = new HttpRequestMessage(HttpMethod.Get, guestUrl);
        request.Headers.TryAddWithoutValidation(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new McpException($"LinkedIn guest search request failed. {ex.Message}");
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new McpException(
                "LinkedIn rate-limited the guest search (HTTP 429). Wait a minute and try again.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new McpException(
                $"LinkedIn guest search returned HTTP {(int)response.StatusCode}. " +
                "The public guest endpoint may be blocked or the query may be invalid.");
        }

        var jobs = ParseListings(html);
        return (jobs, guestUrl);
    }

    internal static string BuildGuestApiUrl(
        string? keywords,
        string? location,
        int? distance,
        string? timePosted,
        string? workplaceTypes,
        string? experienceLevels,
        string? searchUrl,
        int start)
    {
        if (!string.IsNullOrWhiteSpace(searchUrl))
        {
            if (!Uri.TryCreate(searchUrl.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new McpException("searchUrl must be an absolute http(s) LinkedIn jobs URL.");
            }

            if (!uri.Host.Contains("linkedin.com", StringComparison.OrdinalIgnoreCase))
                throw new McpException("searchUrl must be a linkedin.com jobs URL.");

            var query = QueryHelpers.ParseQuery(uri.Query)
                .ToDictionary(
                    pair => pair.Key,
                    pair => (string?)pair.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase);
            query["start"] = start.ToString();
            return QueryHelpers.AddQueryString(GuestApiBase, query);
        }

        var built = new Dictionary<string, string?>
        {
            ["keywords"] = string.IsNullOrWhiteSpace(keywords) ? DefaultKeywords : keywords.Trim(),
            ["location"] = string.IsNullOrWhiteSpace(location) ? DefaultLocation : location.Trim(),
            ["distance"] = (distance is null or <= 0 ? DefaultDistance : distance.Value).ToString(),
            ["f_TPR"] = string.IsNullOrWhiteSpace(timePosted) ? DefaultTimeFilter : timePosted.Trim(),
            ["f_WT"] = string.IsNullOrWhiteSpace(workplaceTypes) ? DefaultWorkplaceTypes : workplaceTypes.Trim(),
            ["f_E"] = string.IsNullOrWhiteSpace(experienceLevels) ? DefaultExperienceLevels : experienceLevels.Trim(),
            ["start"] = start.ToString(),
        };

        return QueryHelpers.AddQueryString(GuestApiBase, built);
    }

    internal static IReadOnlyList<JobListing> ParseListings(string html)
    {
        var results = new List<JobListing>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match card in CardPattern.Matches(html))
        {
            var body = card.Groups["body"].Value;
            var title = Clean(TitlePattern.Match(body).Groups["v"].Value);
            var company = Clean(CompanyPattern.Match(body).Groups["v"].Value);
            var location = Clean(LocationPattern.Match(body).Groups["v"].Value);
            var linkMatch = LinkPattern.Match(body);
            var link = linkMatch.Success
                ? linkMatch.Groups["v"].Value
                : $"https://www.linkedin.com/jobs/view/{card.Groups["id"].Value}";

            var listedMatch = ListedAtPattern.Match(body);
            string? listedAt = null;
            if (listedMatch.Success)
            {
                listedAt = string.IsNullOrWhiteSpace(listedMatch.Groups["dt"].Value)
                    ? Clean(listedMatch.Groups["v"].Value)
                    : listedMatch.Groups["dt"].Value.Trim();
            }

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(company))
                continue;

            if (!seen.Add(link))
                continue;

            results.Add(new JobListing(
                title,
                company,
                string.IsNullOrWhiteSpace(location) ? null : location,
                link,
                listedAt));
        }

        // Fallback when LinkedIn changes card wrappers but still emits job view links.
        if (results.Count == 0)
        {
            foreach (Match linkMatch in LinkPattern.Matches(html))
            {
                var link = linkMatch.Groups["v"].Value;
                if (!seen.Add(link))
                    continue;

                results.Add(new JobListing(
                    "(title unavailable)",
                    "(company unavailable)",
                    null,
                    link,
                    null));
            }
        }

        return results;
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var decoded = WebUtility.HtmlDecode(value);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }
}
