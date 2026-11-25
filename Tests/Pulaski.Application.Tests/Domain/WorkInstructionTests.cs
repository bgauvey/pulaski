using Pulaski.Domain;
using Pulaski.Domain.Instructions;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Work instructions are written in drafts and released in versions, with files, for operations, materials or lines (#126a).</summary>
public class WorkInstructionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_instruction_has_a_code_of_its_own_and_a_title()
    {
        var wi = WorkInstruction.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), " wi-ext-001 ", " Start the extruder ");
        Assert.Equal(("WI-EXT-001", "Start the extruder", true), (wi.Code, wi.Title, wi.IsActive));
        Assert.Throws<DomainRuleException>(() => WorkInstruction.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "bad code", "x"));
        Assert.Throws<DomainRuleException>(() => wi.Edit(" ", true));
        Assert.False(wi.Edit("Start", false).IsActive);
    }

    [Fact]
    public void A_draft_is_written_released_once_and_then_fixed()
    {
        var draft = InstructionVersion.FirstDraft(Guid.CreateVersion7(), Guid.CreateVersion7());
        Assert.Throws<DomainRuleException>(() => draft.Release(T0, "pe", null, files: 0));
        Assert.Equal(T0, draft.Release(T0, "pe", null, files: 1).ReleasedAt);

        var written = draft.SaveDraft("# Start\r\n1. Heat the zones  \r\n");
        Assert.Equal("# Start\n1. Heat the zones", written.Body);
        Assert.Throws<DomainRuleException>(() => written.SaveDraft(new string('x', 20_001)));

        var released = written.Release(T0, "pe", " First issue ", files: 0);
        Assert.Equal(("released", "First issue", "pe"), (released.Status, released.Note, released.ReleasedBy));
        Assert.Throws<DomainRuleException>(() => released.SaveDraft("Changed"));
        Assert.Throws<DomainRuleException>(() => released.Release(T0, "pe", null, 0));

        var next = released.NextDraft(Guid.CreateVersion7());
        Assert.Equal((2, "draft", "# Start\n1. Heat the zones"), (next.Number, next.Status, next.Body));
        Assert.Equal("obsolete", released.MakeObsolete().Status);
    }

    [Fact]
    public void Files_are_pdf_png_or_jpeg_by_their_bytes_and_limited()
    {
        byte[] pdf = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31];
        Assert.Equal("application/pdf", InstructionFiles.Check("setup.pdf", pdf, 0));
        Assert.Throws<DomainRuleException>(() => InstructionFiles.Check("notes.txt", "hello"u8, 0));
        Assert.Throws<DomainRuleException>(() => InstructionFiles.Check(" ", pdf, 0));
        Assert.Throws<DomainRuleException>(() => InstructionFiles.Check("setup.pdf", pdf, InstructionFiles.MaxPerVersion));
        Assert.Throws<DomainRuleException>(() => InstructionFiles.Check("empty.pdf", [], 0));
    }

    [Fact]
    public void A_target_names_an_operation_a_material_or_a_line()
    {
        var process = Guid.CreateVersion7();
        var op = InstructionTarget.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "operation", process, 20, null, null);
        Assert.Equal((process, 20), (op.ProcessId!.Value, op.OperationNumber!.Value));
        Assert.True(op.SameAs(op with { Id = Guid.CreateVersion7() }));
        Assert.Throws<DomainRuleException>(() => InstructionTarget.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "operation", process, null, null, null));
        Assert.Throws<DomainRuleException>(() => InstructionTarget.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "material", null, null, null, null));
        Assert.Throws<DomainRuleException>(() => InstructionTarget.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "site", null, null, null, Guid.CreateVersion7()));
        Assert.Equal("line", InstructionTarget.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "line", null, null, null, Guid.CreateVersion7()).Kind);
    }
}
