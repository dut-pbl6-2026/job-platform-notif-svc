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
