using Pulaski.Application.Abstractions;
using Pulaski.Application.Instructions;
using Pulaski.Contracts.Instructions;
using Pulaski.Domain.Instructions;

namespace Pulaski.Testing;

/// <summary>In-memory work instructions (#126a): every site is known and every target exists.</summary>
public sealed class InMemoryInstructions : IInstructionRepository, IInstructionReadStore, IChecklistRepository
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, (WorkInstruction Value, long Version)> _instructions = [];
    private readonly Dictionary<Guid, (InstructionVersion Value, long Version)> _versions = [];
    private readonly List<(Guid Id, Guid VersionId, Guid ContentId, string FileName, string ContentType, int SortOrder, bool Removed)> _files = [];
    private readonly Dictionary<Guid, byte[]> _contents = [];
    private readonly Dictionary<Guid, InstructionTarget> _targets = [];
    private long _version;

    public Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<bool> CodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_instructions.Values.Any(i => i.Value.SiteId == siteId && i.Value.Code == code));
        }
    }

    public Task<Versioned<WorkInstruction>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_instructions.TryGetValue(id, out var i) ? new Versioned<WorkInstruction>(i.Value, i.Version) : null);
        }
    }

    public Task<long> InsertAsync(WorkInstruction instruction, DateTimeOffset at, CancellationToken cancellationToken) => UpdateAsync(instruction, at, cancellationToken);

    public Task<long> UpdateAsync(WorkInstruction instruction, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _instructions[instruction.Id] = (instruction, ++_version);
            return Task.FromResult(_version);
        }
    }

    public Task<Versioned<InstructionVersion>?> GetDraftAsync(Guid instructionId, CancellationToken cancellationToken) => Find(instructionId, InstructionVersionStatus.Draft);

    public async Task<InstructionVersion?> GetReleasedAsync(Guid instructionId, CancellationToken cancellationToken) =>
        (await Find(instructionId, InstructionVersionStatus.Released))?.Value;

    public Task<long> InsertVersionAsync(InstructionVersion version, DateTimeOffset at, CancellationToken cancellationToken) => UpdateVersionAsync(version, at, cancellationToken);

    public Task<long> UpdateVersionAsync(InstructionVersion version, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _versions[version.Id] = (version, ++_version);
            return Task.FromResult(_version);
        }
    }

    public Task<IReadOnlyList<(Guid Id, Guid ContentId, string FileName, string ContentType, int SortOrder)>> ListFilesAsync(Guid versionId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<(Guid, Guid, string, string, int)> files = [.. _files.Where(f => f.VersionId == versionId && !f.Removed).Select(f => (f.Id, f.ContentId, f.FileName, f.ContentType, f.SortOrder))];
            return Task.FromResult(files);
        }
    }

    public Task<Guid> InsertContentAsync(Guid id, byte[] content, string sha256, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _contents[id] = content;
            return Task.FromResult(id);
        }
    }

    public Task InsertFileAsync(Guid id, Guid versionId, Guid contentId, string fileName, string contentType, int sortOrder, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _files.Add((id, versionId, contentId, fileName, contentType, sortOrder, false));
            return Task.CompletedTask;
        }
    }

    public Task<bool> RemoveFileAsync(Guid versionId, Guid fileId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var i = _files.FindIndex(f => f.Id == fileId && f.VersionId == versionId && !f.Removed);
            if (i < 0)
            {
                return Task.FromResult(false);
            }

            _files[i] = _files[i] with { Removed = true };
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<InstructionTarget>> ListTargetsAsync(Guid instructionId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<InstructionTarget> list = [.. _targets.Values.Where(t => t.InstructionId == instructionId)];
            return Task.FromResult(list);
        }
    }

    public Task<bool> TargetExistsAsync(Guid siteId, InstructionTarget target, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task InsertTargetAsync(InstructionTarget target, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _targets[target.Id] = target;
            return Task.CompletedTask;
        }
    }

    public Task<bool> EndTargetAsync(Guid instructionId, Guid targetId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_targets.TryGetValue(targetId, out var t) || t.InstructionId != instructionId || !t.IsActive)
            {
                return Task.FromResult(false);
            }

            _targets[targetId] = t with { IsActive = false };
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<InstructionSummaryDto>> ListAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<InstructionSummaryDto> list = [.. _instructions.Values.Where(i => i.Value.SiteId == siteId).Select(i => new InstructionSummaryDto
            {
                Id = i.Value.Id,
                SiteId = i.Value.SiteId,
                Code = i.Value.Code,
                Title = i.Value.Title,
                IsActive = i.Value.IsActive,
                Version = i.Version,
            })];
            return Task.FromResult(list);
        }
    }

    public Task<InstructionDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_instructions.TryGetValue(id, out var i))
            {
                return Task.FromResult<InstructionDto?>(null);
            }

            return Task.FromResult<InstructionDto?>(new InstructionDto
            {
                Id = i.Value.Id,
                SiteId = i.Value.SiteId,
                Code = i.Value.Code,
                Title = i.Value.Title,
                IsActive = i.Value.IsActive,
                Version = i.Version,
                Versions = [.. _versions.Values.Where(v => v.Value.InstructionId == id).OrderByDescending(v => v.Value.Number).Select(v => new InstructionVersionDto
                {
                    Id = v.Value.Id, Number = v.Value.Number, Status = v.Value.Status, Body = v.Value.Body, Version = v.Version,
                })],
            });
        }
    }

    public Task<InstructionFileContent?> GetFileAsync(Guid instructionId, Guid fileId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var file = _files.FirstOrDefault(f => f.Id == fileId);
            return Task.FromResult(file.Id == fileId && file.Id != Guid.Empty ? new InstructionFileContent(file.FileName, file.ContentType, _contents[file.ContentId]) : null);
        }
    }

    /// <summary>The fake knows no jobs: tests of what a job shows run against the database.</summary>
    public Task<IReadOnlyList<JobInstructionDto>> ForJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<JobInstructionDto>>([]);

    // Checklists (#126c): the fake knows no jobs, so none apply and nothing is held up.
    public Task<ChecklistJob?> LockJobAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult<ChecklistJob?>(null);

    public Task<IReadOnlyList<ApplicableChecklist>> ApplicableAsync(Guid jobId, Guid lineId, string phase, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ApplicableChecklist>>([]);

    public Task<IReadOnlyList<Guid>> PassedAsync(Guid jobId, Guid lineId, string phase, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task<Guid?> OpenRunAsync(Guid jobId, Guid lineId, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

    public Task InsertAsync(ChecklistResponse response, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AttachToRunAsync(Guid jobId, Guid lineId, Guid runId, CancellationToken cancellationToken) => Task.CompletedTask;

    private Task<Versioned<InstructionVersion>?> Find(Guid instructionId, string status)
    {
        lock (_lock)
        {
            var found = _versions.Values.FirstOrDefault(v => v.Value.InstructionId == instructionId && v.Value.Status == status);
            return Task.FromResult(found.Value is null ? null : new Versioned<InstructionVersion>(found.Value, found.Version));
        }
    }
}
