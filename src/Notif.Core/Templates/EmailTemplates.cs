using System.Net;

namespace Notif.Core.Templates;

/// <summary>
/// HTML + plain-text email templates (PBL6-35, SRS NOTIF-01-04).
/// Recruiter template for application.submitted (NOTIF-01-03),
/// applicant template for application.status_changed (NOTIF-01-01).
/// </summary>
public static class EmailTemplates
{
    public const string ApplicationSubmitted = "application-submitted";
    public const string ApplicationStatusChanged = "application-status-changed";
    public const string JobApproved = "job-approved";
    public const string JobRejected = "job-rejected";

    public static (string Subject, string Html, string Text) SubmittedToRecruiter(
        string jobTitle, Guid applicationId, Guid applicantId, Guid jobId, string baseUrl = "")
    {
        var safeTitle = string.IsNullOrWhiteSpace(jobTitle) ? "a job" : jobTitle.Trim();
        var subject = $"New application for {safeTitle}";
        // baseUrl should be set to NOTIF_BASE_URL (e.g. "https://api.job-platform.com") so
        // that the link is absolute and works in email clients. Defaults to "" (relative) in
        // local/dev where baseUrl is not configured.
        var base_ = baseUrl.TrimEnd('/');
        var link = $"{base_}/api/applications/{applicationId}";
        var html = $"<p>You have a new application for <strong>{WebUtility.HtmlEncode(safeTitle)}</strong>.</p>"
            + $"<p>Applicant: <code>{applicantId}</code><br/>Job: <code>{jobId}</code></p>"
            + $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">View application</a></p>";
        var text = $"New application for {safeTitle}.\nApplicant: {applicantId}\nJob: {jobId}\nView: {link}";
        return (subject, html, text);
    }

    public static (string Subject, string Html, string Text) StatusChangedToApplicant(string jobTitle, string newStatus)
    {
        var safeTitle = string.IsNullOrWhiteSpace(jobTitle) ? "the job" : jobTitle.Trim();
        var safeStatus = string.IsNullOrWhiteSpace(newStatus) ? "updated" : newStatus.Trim().ToLowerInvariant();
        var subject = $"Your application for {safeTitle} has been {safeStatus}";
        var html = $"<p>Your application for <strong>{WebUtility.HtmlEncode(safeTitle)}</strong> has been <strong>{WebUtility.HtmlEncode(safeStatus)}</strong>.</p>"
            + "<p>Good luck with the next steps.</p>";
        var text = $"Your application for {safeTitle} has been {safeStatus}.\nGood luck with the next steps.";
        return (subject, html, text);
    }

    /// <summary>
    /// Recruiter notification for an admin-approved job (feat(admin) Slice 5, JOB-01).
    /// </summary>
    public static (string Subject, string Html, string Text) JobApprovedToRecruiter(
        string recruiterName, string jobTitle)
    {
        var greeting = Greeting(recruiterName);
        var safeTitle = string.IsNullOrWhiteSpace(jobTitle) ? "your job" : jobTitle.Trim();
        var subject = $"Your job \"{safeTitle}\" has been approved";
        var html = $"<p>{WebUtility.HtmlEncode(greeting)},</p>"
            + $"<p>Your job <strong>{WebUtility.HtmlEncode(safeTitle)}</strong> has been approved and is now live on the platform.</p>";
        var text = $"{greeting},\nYour job \"{safeTitle}\" has been approved and is now live on the platform.";
        return (subject, html, text);
    }

    /// <summary>
    /// Recruiter notification for an admin-rejected job (feat(admin) Slice 5, JOB-01).
    /// The reason is plain text from the admin and is HTML-encoded on render.
    /// </summary>
    public static (string Subject, string Html, string Text) JobRejectedToRecruiter(
        string recruiterName, string jobTitle, string? reason)
    {
        var greeting = Greeting(recruiterName);
        var safeTitle = string.IsNullOrWhiteSpace(jobTitle) ? "your job" : jobTitle.Trim();
        var safeReason = string.IsNullOrWhiteSpace(reason) ? "No reason provided." : reason.Trim();
        var subject = $"Your job \"{safeTitle}\" was not approved";
        var html = $"<p>{WebUtility.HtmlEncode(greeting)},</p>"
            + $"<p>Your job <strong>{WebUtility.HtmlEncode(safeTitle)}</strong> was not approved.</p>"
            + $"<p>Reason: {WebUtility.HtmlEncode(safeReason)}</p>";
        var text = $"{greeting},\nYour job \"{safeTitle}\" was not approved.\nReason: {safeReason}";
        return (subject, html, text);
    }

    private static string Greeting(string recruiterName) =>
        string.IsNullOrWhiteSpace(recruiterName) ? "Hello" : $"Hello {recruiterName.Trim()}";
}
