using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Tests;

public sealed class ProfileRefreshPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void User_is_due_when_document_missing()
    {
        var policy = new ProfileRefreshPolicy(new AppSettings());

        var due = policy.GetDueKinds(user: null, lessons: null, newestCorrectionAt: null, now: Now);

        Assert.Contains(ProfileKind.User, due);
        Assert.DoesNotContain(ProfileKind.Lessons, due);
    }

    [Fact]
    public void User_is_due_when_empty_even_if_timestamp_is_recent()
    {
        var policy = new ProfileRefreshPolicy(new AppSettings());
        var empty = new ProfileDocument(ProfileKind.User, "  ", Now, ProfileBackendKind.Local);

        var due = policy.GetDueKinds(empty, null, null, Now);

        Assert.Contains(ProfileKind.User, due);
    }

    [Fact]
    public void User_is_not_due_within_cooldown()
    {
        var policy = new ProfileRefreshPolicy(new AppSettings());
        var document = new ProfileDocument(
            ProfileKind.User,
            "# USER\n- prefers Chinese",
            Now.AddMinutes(-30),
            ProfileBackendKind.Local);

        var due = policy.GetDueKinds(document, null, null, Now);

        Assert.Empty(due);
    }

    [Fact]
    public void User_is_due_after_cooldown()
    {
        var policy = new ProfileRefreshPolicy(new AppSettings
        {
            Profile = { UserRefreshCooldown = TimeSpan.FromMinutes(30) }
        });
        var document = new ProfileDocument(
            ProfileKind.User,
            "# USER\n- prefers Chinese",
            Now.AddMinutes(-45),
            ProfileBackendKind.Local);

        var due = policy.GetDueKinds(document, null, null, Now);

        Assert.Contains(ProfileKind.User, due);
    }

    [Fact]
    public void Lessons_are_not_due_without_a_correction()
    {
        var policy = new ProfileRefreshPolicy(new AppSettings());
        var lessons = new ProfileDocument(
            ProfileKind.Lessons,
            "# LESSONS\n- something",
            Now.AddDays(-3),
            ProfileBackendKind.Local);

        var due = policy.GetDueKinds(null, lessons, newestCorrectionAt: null, Now);

        Assert.DoesNotContain(ProfileKind.Lessons, due);
    }

    [Fact]
    public void Lessons_are_due_when_a_correction_is_newer_than_the_document()
    {
        var policy = new ProfileRefreshPolicy(new AppSettings());
        var lessons = new ProfileDocument(
            ProfileKind.Lessons,
            "# LESSONS\n- something",
            Now.AddMinutes(-10),
            ProfileBackendKind.Local);

        var due = policy.GetDueKinds(null, lessons, Now.AddMinutes(-1), Now);

        Assert.Contains(ProfileKind.Lessons, due);
    }

    [Fact]
    public void Lessons_are_not_due_when_the_correction_predates_the_document()
    {
        var policy = new ProfileRefreshPolicy(new AppSettings());
        var lessons = new ProfileDocument(
            ProfileKind.Lessons,
            "# LESSONS\n- something",
            Now.AddMinutes(-10),
            ProfileBackendKind.Local);

        var due = policy.GetDueKinds(null, lessons, Now.AddHours(-2), Now);

        Assert.DoesNotContain(ProfileKind.Lessons, due);
    }

    [Fact]
    public void MarkAttempted_suppresses_a_repeat_before_the_cooldown_elapses()
    {
        var settings = new AppSettings();
        var policy = new ProfileRefreshPolicy(settings);
        var document = new ProfileDocument(
            ProfileKind.User,
            "# USER\n- prefers Chinese",
            Now.AddHours(-5),
            ProfileBackendKind.Local);

        Assert.Contains(ProfileKind.User, policy.GetDueKinds(document, null, null, Now));

        policy.MarkAttempted([ProfileKind.User], Now);

        Assert.DoesNotContain(ProfileKind.User, policy.GetDueKinds(document, null, null, Now.AddMinutes(5)));
        Assert.Contains(
            ProfileKind.User,
            policy.GetDueKinds(document, null, null, Now.AddHours(3)));
    }

    [Fact]
    public void MarkAttempted_suppresses_a_repeat_lessons_request_after_unchanged()
    {
        var policy = new ProfileRefreshPolicy(new AppSettings());
        var lessons = new ProfileDocument(
            ProfileKind.Lessons,
            "# LESSONS\n- something",
            Now.AddMinutes(-10),
            ProfileBackendKind.Local);
        var correctionAt = Now.AddMinutes(-1);

        Assert.Contains(ProfileKind.Lessons, policy.GetDueKinds(null, lessons, correctionAt, Now));

        policy.MarkAttempted([ProfileKind.Lessons], Now);

        // Same correction, no new write: nothing changed, so do not ask again.
        Assert.DoesNotContain(ProfileKind.Lessons, policy.GetDueKinds(null, lessons, correctionAt, Now.AddMinutes(1)));
    }

    [Fact]
    public async Task Concurrent_reads_return_consistent_results()
    {
        var policy = new ProfileRefreshPolicy(new AppSettings());
        var document = new ProfileDocument(
            ProfileKind.User,
            "# USER\n- prefers Chinese",
            Now.AddHours(-5),
            ProfileBackendKind.Local);

        var results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => policy.GetDueKinds(document, null, null, Now))));

        Assert.All(results, due => Assert.Single(due));
    }
}
