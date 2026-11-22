using System.Net.Mail;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using ModelContextProtocol;

namespace JobPipelineMcp;

public sealed record InboxReceipt(
    string Subject,
    string From,
    DateOnly DateApplied,
    string? Company,
    string? Role,
    string? MessageId,
    bool Parsed,
    string Confidence);

public sealed class InboxReceiptImporter
{
    // Company captures end at ! or EOS only — not at ".", so Booking.com stays intact.
    private static readonly Regex ThankYouToAt = new(
        @"thank you for applying to\s+(?<role>.+?)\s+at\s+(?<company>.+?)\s*!?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ThankYouAtCompany = new(
        @"thank you for applying at\s+(?<company>.+?)\s*!?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ApplicationReceivedAt = new(
        @"application received\s*[-–—:|]\s*(?<role>.+?)\s+at\s+(?<company>.+?)\s*!?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ThankYouToDash = new(
        @"thank you for applying to\s+(?<company>[^-–—]+?)\s*[-–—]\s*(?<role>.+?)\s*!?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ThankYouToPosition = new(
        @"thank you for applying to(?:\s+the)?\s+(?<role>.+?)\s+position\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ThankYouToCompany = new(
        @"thank you for applying to\s+(?<company>.+?)\s*!?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ThanksForApplyingTo = new(
        @"thanks for applying to\s+(?<company>.+?)\s*!?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ApplicationDashRole = new(
        @"thank you for your application\s*[-–—:]\s*(?<role>.+?)(?:\s+at\s+(?<company>.+?))?\s*!?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CompanyThankYou = new(
        @"^(?<company>.+?)\s*[-–—]\s*thank you for applying",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OpeningAt = new(
        @"^(?<role>.+?)\s+opening at\s+(?<company>.+?)\s*!?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ConfirmationPipe = new(
        @"application confirmation\s*[|:-]\s*(?<company>.+?)\s*!?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Prefer labeled fields near the top of the body only.
    private static readonly Regex[] BodyRolePatterns =
    [
        new(@"(?m)^\s*(?:job\s+)?(?:title|role|position)\s*:\s*(?<role>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(?:job\s+)?(?:title|role|position)\s*:\s*(?<role>[^\n\r.]{3,80})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    private static readonly Regex TitleHint = new(
        @"\b(manager|director|engineer|engineering|developer|architect|lead|principal|vp|vice president|head of|staff|senior|junior)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> GenericLocalParts = new(StringComparer.OrdinalIgnoreCase)
    {
        "no-reply", "noreply", "do-not-reply", "donotreply", "notification", "notifications",
        "careers", "jobs", "recruiting", "talent", "hr", "mailer", "hello", "info",
        "talent.acquisition", "ad.hoc.talent.acquisition",
    };

    public async Task<IReadOnlyList<InboxReceipt>> FetchAsync(
        int daysBack,
        int maxMessages,
        CancellationToken cancellationToken)
    {
        var address = RequiredEnv("GMAIL_ADDRESS");
        var password = RequiredEnv("GMAIL_APP_PASSWORD").Replace(" ", "", StringComparison.Ordinal);
        var host = Environment.GetEnvironmentVariable("IMAP_HOST");
        if (string.IsNullOrWhiteSpace(host))
            host = "imap.gmail.com";
        var portText = Environment.GetEnvironmentVariable("IMAP_PORT");
        var port = int.TryParse(portText, out var parsedPort) ? parsedPort : 993;

        daysBack = Math.Clamp(daysBack, 1, 365);
        maxMessages = Math.Clamp(maxMessages, 1, 200);
        var since = DateTime.UtcNow.Date.AddDays(-daysBack);

        using var client = new ImapClient();
        try
        {
            await client.ConnectAsync(host, port, SecureSocketOptions.SslOnConnect, cancellationToken);
            await client.AuthenticateAsync(address, password, cancellationToken);
        }
        catch (Exception ex) when (ex is AuthenticationException or ImapCommandException or IOException)
        {
            throw new McpException(
                "Could not connect to Gmail IMAP. Check GMAIL_ADDRESS and GMAIL_APP_PASSWORD " +
                "(Google Account → Security → App passwords). " + ex.Message);
        }

        try
        {
            var folder = client.GetFolder(SpecialFolder.All) ?? client.Inbox;
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

            var raw =
                $"newer_than:{daysBack}d " +
                "(\"thank you for applying\" OR \"thanks for applying\" OR \"thank you for your application\" OR " +
                "\"application received\" OR \"application confirmation\" OR \"we received your application\")";
            UniqueId[] uids;
            try
            {
                uids = [.. await folder.SearchAsync(SearchQuery.GMailRawSearch(raw), cancellationToken)];
            }
            catch (ImapCommandException)
            {
                uids =
                [
                    .. await folder.SearchAsync(
                        SearchQuery.DeliveredAfter(since.AddDays(-1)).And(
                            SearchQuery.SubjectContains("Thank you for applying")
                                .Or(SearchQuery.SubjectContains("application received"))
                                .Or(SearchQuery.SubjectContains("We received your application"))),
                        cancellationToken)
                ];
            }

            var newestFirst = uids
                .Reverse()
                .Take(maxMessages)
                .ToList();

            var receipts = new List<InboxReceipt>(newestFirst.Count);
            foreach (var uid in newestFirst)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var message = await folder.GetMessageAsync(uid, cancellationToken);
                receipts.Add(Parse(message));
            }

            return receipts;
        }
        finally
        {
            if (client.IsConnected)
                await client.DisconnectAsync(true, cancellationToken);
        }
    }

    internal static InboxReceipt Parse(MimeMessage message)
    {
        var subject = message.Subject?.Trim() ?? "";
        var from = message.From.Mailboxes.FirstOrDefault()?.Address
            ?? message.From.ToString()
            ?? "";
        var date = DateOnly.FromDateTime(
            (message.Date != DateTimeOffset.MinValue ? message.Date : DateTimeOffset.UtcNow).LocalDateTime);
        var body = GetPlainText(message);

        var (company, role, companyFromSubject, roleFromSubject) = ParseSubject(subject, from);
        var roleFromBody = false;
        if (string.IsNullOrWhiteSpace(role) && !string.IsNullOrWhiteSpace(body))
        {
            role = ParseRoleFromBody(body);
            roleFromBody = role is not null;
        }

        if (string.IsNullOrWhiteSpace(company) && !string.IsNullOrWhiteSpace(body))
        {
            var bodyCompany = ParseCompanyFromBody(body);
            if (bodyCompany is not null)
            {
                company = bodyCompany;
                companyFromSubject = true; // labeled body field is explicit, not a sender guess
            }
        }

        company = NormalizeCompany(company);
        role = NormalizeRole(role, company);

        if (!IsPlausibleCompany(company))
            company = null;
        if (!IsPlausibleRole(role))
            role = null;

        var parsed = company is not null && role is not null;
        // High confidence only when both company and role came from the subject/body text —
        // never when company was inferred from the From: address (e.g. jane@northwind.com → Jane).
        var confidence = !parsed
            ? "none"
            : companyFromSubject && roleFromSubject && !roleFromBody && TitleHint.IsMatch(role!)
                ? "high"
                : companyFromSubject || roleFromSubject || (roleFromBody && TitleHint.IsMatch(role!))
                    ? "medium"
                    : "low";

        return new InboxReceipt(
            subject,
            from,
            date,
            company,
            role,
            message.MessageId,
            parsed,
            confidence);
    }

    // companyFromSubject / roleFromSubject: true when taken from subject (or labeled body), false when From: guess.
    private static (string? Company, string? Role, bool CompanyFromSubject, bool RoleFromSubject) ParseSubject(
        string subject,
        string from)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return (CompanyFromAddress(from), null, false, false);

        var opening = OpeningAt.Match(subject);
        if (opening.Success)
            return (opening.Groups["company"].Value, StripJobCode(opening.Groups["role"].Value), true, true);

        var received = ApplicationReceivedAt.Match(subject);
        if (received.Success)
            return (received.Groups["company"].Value, StripJobCode(received.Groups["role"].Value), true, true);

        var at = ThankYouToAt.Match(subject);
        if (at.Success)
            return (at.Groups["company"].Value, StripJobCode(at.Groups["role"].Value), true, true);

        var dash = ThankYouToDash.Match(subject);
        if (dash.Success)
            return (dash.Groups["company"].Value, dash.Groups["role"].Value, true, true);

        var appDash = ApplicationDashRole.Match(subject);
        if (appDash.Success)
        {
            var hasCompany = appDash.Groups["company"].Success && appDash.Groups["company"].Length > 0;
            var company = hasCompany ? appDash.Groups["company"].Value : CompanyFromAddress(from);
            return (company, StripJobCode(appDash.Groups["role"].Value), hasCompany, true);
        }

        var position = ThankYouToPosition.Match(subject);
        if (position.Success)
            return (CompanyFromAddress(from), position.Groups["role"].Value, false, true);

        var companyThanks = CompanyThankYou.Match(subject);
        if (companyThanks.Success)
            return (companyThanks.Groups["company"].Value, null, true, false);

        var confirm = ConfirmationPipe.Match(subject);
        if (confirm.Success)
            return (confirm.Groups["company"].Value, null, true, false);

        var atCompany = ThankYouAtCompany.Match(subject);
        if (atCompany.Success)
            return (atCompany.Groups["company"].Value, null, true, false);

        var thanksTo = ThanksForApplyingTo.Match(subject);
        if (thanksTo.Success)
            return (thanksTo.Groups["company"].Value, null, true, false);

        var companyOnly = ThankYouToCompany.Match(subject);
        if (companyOnly.Success)
        {
            var company = companyOnly.Groups["company"].Value.Trim();
            if (company.Length < 2)
                return (CompanyFromAddress(from), null, false, false);

            return (company, null, true, false);
        }

        if (subject.Contains("thank you for applying", StringComparison.OrdinalIgnoreCase) ||
            subject.Contains("thanks for applying", StringComparison.OrdinalIgnoreCase) ||
            subject.Contains("application received", StringComparison.OrdinalIgnoreCase) ||
            subject.Contains("thank you for your application", StringComparison.OrdinalIgnoreCase))
            return (CompanyFromAddress(from), null, false, false);

        return (null, null, false, false);
    }

    private static string? ParseRoleFromBody(string body)
    {
        var sample = body.Length > 2500 ? body[..2500] : body;
        foreach (var pattern in BodyRolePatterns)
        {
            var match = pattern.Match(sample);
            if (!match.Success)
                continue;

            var role = Clean(match.Groups["role"].Value);
            if (IsPlausibleRole(role))
                return role;
        }

        return null;
    }

    private static string? ParseCompanyFromBody(string body)
    {
        var sample = body.Length > 2500 ? body[..2500] : body;
        var match = Regex.Match(
            sample,
            @"(?m)^\s*company\s*:\s*(?<company>.+?)\s*$",
            RegexOptions.IgnoreCase);
        return match.Success ? Clean(match.Groups["company"].Value) : null;
    }

    private static string? NormalizeRole(string? role, string? company)
    {
        role = Clean(role);
        if (role is null)
            return null;

        // "Role | Role" duplicates
        var parts = role.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && string.Equals(parts[0], parts[1], StringComparison.OrdinalIgnoreCase))
            role = parts[0];
        else if (parts.Length >= 2)
            role = parts[0]; // "Manager | Owner/Investor" → keep primary title

        // "… at Company" trailing company echo
        if (company is not null)
        {
            var suffix = " at " + company;
            if (role.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                role = role[..^suffix.Length].Trim();
        }
        else
        {
            role = Regex.Replace(role, @"\s+at\s+[A-Z][\w .&-]{1,40}$", "", RegexOptions.None).Trim();
        }

        role = Regex.Replace(role, @"\s+role$", "", RegexOptions.IgnoreCase).Trim();
        role = StripJobCode(role);
        return Clean(role);
    }

    private static string? NormalizeCompany(string? company)
    {
        company = Clean(company);
        if (company is null)
            return null;

        company = company.TrimEnd('!');
        // "Ad.hoc.talent.acquisition" style mailbox leftovers
        if (company.Count(c => c == '.') >= 2)
            return null;

        return company;
    }

    private static bool IsPlausibleRole(string? role)
    {
        if (role is not { Length: >= 3 and <= 80 })
            return false;

        if (role.Contains(',') && role.Length > 60)
            return false;
        if (Regex.IsMatch(
                role,
                @"\b(we|you|your|our|us|thanks|thank|interest|review|contact|resume|application|selected|process|circle|grateful|excited|background|current|soon|opening)\b",
                RegexOptions.IgnoreCase))
            return false;
        if (role.StartsWith("position ", StringComparison.OrdinalIgnoreCase) ||
            role.StartsWith("role ", StringComparison.OrdinalIgnoreCase) ||
            role.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ||
            role.StartsWith("at ", StringComparison.OrdinalIgnoreCase))
            return false;

        // Require a title-ish signal so "current" / fragments never auto-qualify.
        if (!TitleHint.IsMatch(role))
            return false;

        return true;
    }

    private static bool IsPlausibleCompany(string? company)
    {
        if (company is not { Length: >= 2 and <= 60 })
            return false;
        if (Regex.IsMatch(company, @"\b(we|you|your|thanks|thank|interest|review|contact|current|role)\b", RegexOptions.IgnoreCase))
            return false;
        if (company.StartsWith("at ", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static string? CompanyFromAddress(string from)
    {
        if (string.IsNullOrWhiteSpace(from))
            return null;

        try
        {
            var address = new MailAddress(from);
            var local = address.User;
            var host = address.Host;

            if (host.Contains("myworkday.com", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("okta.com", StringComparison.OrdinalIgnoreCase))
            {
                return PrettifyToken(local);
            }

            if (!GenericLocalParts.Contains(local) && !local.Contains('.', StringComparison.Ordinal))
                return PrettifyToken(local);

            var domain = host.Split('.')[0];
            if (domain is "us" or "mail" or "email" or "app" or "my" or "e")
            {
                var parts = host.Split('.');
                domain = parts.Length >= 2 ? parts[^2] : domain;
            }

            if (domain is "greenhouse-mail" or "ashbyhq" or "smartrecruiters" or "bamboohr" or "paylocity")
                return null;

            return PrettifyToken(domain);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string StripJobCode(string role)
    {
        var cleaned = Regex.Replace(role.Trim(), @"^[A-Z]{1,5}\d+[-\s]+", "", RegexOptions.None);
        cleaned = Regex.Replace(cleaned, @"[-–—]\d{5,}$", "", RegexOptions.None);
        return cleaned;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = Regex.Replace(value.Trim(), @"\s+", " ");
        // Strip only trailing sentence punctuation; keep embedded periods (Booking.com).
        text = text.TrimEnd('!', ',', ':', ';', '"', '\'', '|', ' ');
        while (text.EndsWith('.') && !Regex.IsMatch(text, @"\.[A-Za-z]{2,}$"))
            text = text[..^1].TrimEnd();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string PrettifyToken(string token)
    {
        token = token.Replace('_', ' ').Replace('-', ' ').Trim();
        if (token.Length == 0)
            return token;

        if (token.Length <= 6 && token.All(char.IsLetter) && token == token.ToUpperInvariant())
            return token;

        return string.Join(
            ' ',
            token.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(part =>
                    part.Length <= 3 && part.All(char.IsLetter)
                        ? part.ToUpperInvariant()
                        : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }

    private static string GetPlainText(MimeMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.TextBody))
            return message.TextBody;

        if (!string.IsNullOrWhiteSpace(message.HtmlBody))
        {
            var html = message.HtmlBody;
            html = Regex.Replace(html, "(?is)<script.*?</script>", " ");
            html = Regex.Replace(html, "(?is)<style.*?</style>", " ");
            html = Regex.Replace(html, "(?is)<[^>]+>", " ");
            html = System.Net.WebUtility.HtmlDecode(html);
            return Regex.Replace(html, @"\s+", " ").Trim();
        }

        return "";
    }

    private static string RequiredEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new McpException(
                $"{name} is not set. For this POC, create a Gmail app password and set " +
                "GMAIL_ADDRESS and GMAIL_APP_PASSWORD on the server.");
        }

        return value.Trim();
    }
}
