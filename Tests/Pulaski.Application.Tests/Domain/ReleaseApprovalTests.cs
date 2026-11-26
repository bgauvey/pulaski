using Pulaski.Domain;
using Pulaski.Domain.Approvals;

namespace Pulaski.Application.Tests.Domain;

/// <summary>A release request is decided by someone else, with a comment, once (#138).</summary>
public class ReleaseApprovalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);
    private static readonly string[] Kinds = ["process", "bom", "spec", "other"];

    private static ReleaseRequest Waiting() =>
        ReleaseRequest.Submit(Guid.CreateVersion7(), "process", Guid.CreateVersion7(), Guid.CreateVersion7(), [Guid.CreateVersion7()], "editor", T0, " New die ");

    [Fact]
    public void Someone_who_neither_submitted_nor_edited_it_approves_it_with_a_comment()
    {
        var request = Waiting();
        Assert.Equal(("pending", "New die"), (request.Status, request.Note));
        Assert.Throws<DomainRuleException>(() => request.Approve("editor", [], T0, "ok"));
        Assert.Throws<DomainRuleException>(() => request.Approve("other", ["other"], T0, "ok"));
        Assert.Throws<DomainRuleException>(() => request.Approve("qa", ["other"], T0, " "));
        var approved = request.Approve("qa", ["other"], T0, " Checked the routes ");
        Assert.Equal(("approved", "qa", "Checked the routes"), (approved.Status, approved.DecidedBy, approved.DecisionNote));
        Assert.Throws<DomainRuleException>(() => approved.Return("qa2", T0, "late"));
    }

    [Fact]
    public void An_approver_sends_it_back_with_why_and_only_its_submitter_withdraws_it()
    {
        var request = Waiting();
        Assert.Throws<DomainRuleException>(() => request.Return("editor", T0, "mine"));
        Assert.Throws<DomainRuleException>(() => request.Return("qa", T0, null));
        Assert.Equal("returned", request.Return("qa", T0, "Wrong line").Status);
        Assert.Throws<DomainRuleException>(() => request.Withdraw("qa", T0));
        Assert.Equal("withdrawn", request.Withdraw("editor", T0).Status);
    }

    [Fact]
    public void A_sites_settings_say_which_kinds_need_approval()
    {
        var settings = new ReleaseApprovalSettings(Guid.CreateVersion7(), Processes: true, Boms: false, Specs: true);
        Assert.Equal([true, false, true, false], Kinds.Select(settings.Requires));
        Assert.False(ReleaseApprovalSettings.Off(Guid.CreateVersion7()).Requires("process"));
    }
}
