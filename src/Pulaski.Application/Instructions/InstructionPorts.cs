using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Instructions;
using Pulaski.Domain.Instructions;

namespace Pulaski.Application.Instructions;

/// <summary>A stored instruction file's bytes, to send.</summary>
public sealed record InstructionFileContent(string FileName, string ContentType, byte[] Content);

/// <summary>Write side for work instructions (#126a, D-116), in the current unit of work.</summary>
public interface IInstructionRepository
{
    Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken);

    Task<bool> CodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken);

    /// <summary>The instruction, locked: every change to it, its versions, files and targets takes this first.</summary>
    Task<Versioned<WorkInstruction>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<long> InsertAsync(WorkInstruction instruction, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateAsync(WorkInstruction instruction, DateTimeOffset at, CancellationToken cancellationToken);

    Task<Versioned<InstructionVersion>?> GetDraftAsync(Guid instructionId, CancellationToken cancellationToken);

    Task<InstructionVersion?> GetReleasedAsync(Guid instructionId, CancellationToken cancellationToken);

    Task<long> InsertVersionAsync(InstructionVersion version, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateVersionAsync(InstructionVersion version, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The files a version carries (not taken out), in their order.</summary>
    Task<IReadOnlyList<(Guid Id, Guid ContentId, string FileName, string ContentType, int SortOrder)>> ListFilesAsync(Guid versionId, CancellationToken cancellationToken);

    /// <summary>Stores a file's bytes once and gives its content id.</summary>
    Task<Guid> InsertContentAsync(Guid id, byte[] content, string sha256, CancellationToken cancellationToken);

    Task InsertFileAsync(Guid id, Guid versionId, Guid contentId, string fileName, string contentType, int sortOrder, CancellationToken cancellationToken);

    /// <summary>Takes a file out of a draft: false when it isn't one of the draft's.</summary>
    Task<bool> RemoveFileAsync(Guid versionId, Guid fileId, DateTimeOffset at, CancellationToken cancellationToken);

    Task<IReadOnlyList<InstructionTarget>> ListTargetsAsync(Guid instructionId, CancellationToken cancellationToken);

    /// <summary>Whether what a target names exists at the site: the process (and its operation number in a version), the material, or a line.</summary>
    Task<bool> TargetExistsAsync(Guid siteId, InstructionTarget target, CancellationToken cancellationToken);

    Task InsertTargetAsync(InstructionTarget target, DateTimeOffset at, CancellationToken cancellationToken);

    Task<bool> EndTargetAsync(Guid instructionId, Guid targetId, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Read side for work instructions (#126a).</summary>
public interface IInstructionReadStore
{
    Task<IReadOnlyList<InstructionSummaryDto>> ListAsync(Guid siteId, CancellationToken cancellationToken);

    Task<InstructionDto?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The file, if it is one of the instruction's versions'.</summary>
    Task<InstructionFileContent?> GetFileAsync(Guid instructionId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>The released instructions a job shows (#126b), in <see cref="Domain.Instructions.JobInstructions"/> order; empty for a job not found.</summary>
    Task<IReadOnlyList<JobInstructionDto>> ForJobAsync(Guid jobId, CancellationToken cancellationToken);
}
