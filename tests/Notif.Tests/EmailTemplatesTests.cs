using Notif.Core.Templates;
using Xunit;

namespace Notif.Tests;

public class EmailTemplatesTests
{
    [Fact]
    public void SubmittedToRecruiter_ContainsIdsAndLink()
    {
        var appId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var (subject, html, text) = EmailTemplates.SubmittedToRecruiter("Backend Dev", appId, applicantId, jobId);
        Assert.Contains("Backend Dev", subject);
        Assert.Contains(appId.ToString(), html);
        Assert.Contains($"/api/applications/{appId}", text);
    }

    [Fact]
    public void SubmittedToRecruiter_WithBaseUrl_ProducesAbsoluteLink()
    {
        var appId = Guid.NewGuid();
        var (_, html, text) = EmailTemplates.SubmittedToRecruiter(
            "Backend Dev", appId, Guid.NewGuid(), Guid.NewGuid(), "https://app.example.com");
        Assert.Contains($"https://app.example.com/api/applications/{appId}", html);
        Assert.Contains($"https://app.example.com/api/applications/{appId}", text);
    }

    [Fact]
    public void SubmittedToRecruiter_EmptyBaseUrl_ProducesRelativeLink()
    {
        var appId = Guid.NewGuid();
        var (_, html, text) = EmailTemplates.SubmittedToRecruiter(
            "Backend Dev", appId, Guid.NewGuid(), Guid.NewGuid(), "");
        // Relative link starts directly with /api
        Assert.Contains($"/api/applications/{appId}", html);
        Assert.DoesNotContain("https://", html);
    }

    [Fact]
    public void StatusChangedToApplicant_NormalizesStatus()
    {
        var (subject, html, _) = EmailTemplates.StatusChangedToApplicant("Backend Dev", "Accepted");
        Assert.Contains("accepted", subject);
        Assert.Contains("Backend Dev", html);
    }

    [Fact]
    public void SubmittedToRecruiter_EmptyTitle_FallsBack()
    {
        var (subject, _, _) = EmailTemplates.SubmittedToRecruiter("", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Contains("a job", subject);
    }
}
