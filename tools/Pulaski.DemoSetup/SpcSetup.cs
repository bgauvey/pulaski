using Pulaski.Contracts.Common;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Execution;
using Pulaski.Contracts.Processes;
using Pulaski.Contracts.Quality;
using Pulaski.Contracts.ReferenceData;
using Pulaski.Contracts.Scheduling;
using Pulaski.Contracts.WorkOrders;

namespace Pulaski.DemoSetup;

/// <summary>
/// A live control chart for the demo (D-099, D-101), set up as a quality engineer and a planner would through the API: the extruder's process
/// values as characteristics with a released specification and manual control limits, sampled from their tags every minute; and a released
/// process for the extrusion line with a work order whose job is started, since results are sampled only while a job runs on the line. What
/// exists already (from an interrupted run) is kept.
/// </summary>
public sealed class SpcSetup(PulaskiApi api)
{
    private const string Made = "made by the demo setup";

    public async Task RunAsync(EquipmentDto site, IReadOnlyList<EquipmentDto> equipment, Guid edgeNodeId, CancellationToken ct)
    {
        var line = equipment.SingleOrDefault(e => e.SiteId == site.Id && e.Code == DemoPlan.LineCode)
            ?? throw new InvalidOperationException($"There is no line {DemoPlan.LineCode}.");
        var material = (await api.GetAsync<IReadOnlyList<MaterialDto>>("api/materials", ct)).SingleOrDefault(m => m.Code == DemoPlan.MaterialCode)
            ?? throw new InvalidOperationException($"There is no material {DemoPlan.MaterialCode}; the demo seed makes it.");
        var tags = (await api.GetEdgeNodeAsync(edgeNodeId, ct)).Tags;

        var characteristics = await CharacteristicsAsync(ct);
        await SpecificationAsync(material, characteristics, ct);
        foreach (var plan in DemoPlan.Characteristics)
        {
            var id = characteristics[plan.Code];
            await LimitsAsync(id, plan, material, ct);
            var tag = tags.SingleOrDefault(t => t.Address == plan.TagAddress && t.Role == "process_value")
                ?? throw new InvalidOperationException($"{plan.TagAddress} isn't bound.");
            var bindings = await api.GetAsync<IReadOnlyList<TagBindingDto>>($"api/quality/characteristics/{id}/tags", ct);
            if (!bindings.Any(b => b.EquipmentTagId == tag.Id))
            {
                await api.SendAsync<CreatedResponse>(HttpMethod.Post, $"api/quality/characteristics/{id}/tags",
                    new AddTagBindingRequest { EquipmentTagId = tag.Id, IntervalMinutes = 1 }, ct);
                Console.WriteLine($"{plan.Code} is sampled from {plan.TagAddress} every minute.");
            }
        }

        var processId = await ProcessAsync(site, line, material, ct);
        await OrderAsync(site, processId, material, ct);
    }

    private async Task<Dictionary<string, Guid>> CharacteristicsAsync(CancellationToken ct)
    {
        var existing = await api.GetAsync<IReadOnlyList<CharacteristicDto>>("api/quality/characteristics", ct);
        var uoms = await api.GetAsync<IReadOnlyList<UomDto>>("api/uoms", ct);
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var plan in DemoPlan.Characteristics)
        {
            if (existing.FirstOrDefault(c => c.Code == plan.Code) is { } found)
            {
                ids[plan.Code] = found.Id;
                continue;
            }

            var created = await api.SendAsync<CreatedResponse>(HttpMethod.Post, "api/quality/characteristics", new CreateCharacteristicRequest
            {
                Code = plan.Code,
                Name = plan.Name,
                Description = $"The extruder's {plan.Name.ToLowerInvariant()}, from the process simulator ({Made})",
                DataKind = "variable",
                UomId = plan.UomCode is { } code ? uoms.Single(u => u.Code == code).Id : null,
                Decimals = plan.Decimals,
            }, ct);
            ids[plan.Code] = created.Id;
            Console.WriteLine($"Characteristic {plan.Code} added (individuals and moving range).");
        }

        return ids;
    }

    // The material's specification: both characteristics, melt temperature capable and the profile's width marginal.
    private async Task SpecificationAsync(MaterialDto material, Dictionary<string, Guid> characteristics, CancellationToken ct)
    {
        var summary = (await api.GetAsync<IReadOnlyList<SpecSummaryDto>>("api/quality/specs", ct)).FirstOrDefault(s => s.MaterialId == material.Id);
        var specId = summary?.Id ?? (await api.SendAsync<CreatedResponse>(HttpMethod.Post, "api/quality/specs", new CreateSpecRequest { MaterialId = material.Id }, ct)).Id;
        var spec = await api.GetAsync<SpecDto>($"api/quality/specs/{specId}", ct);
        if (spec.Versions.Any(v => v.Status == "released"))
        {
            return;
        }

        var draftId = spec.Versions.FirstOrDefault(v => v.Status == "draft")?.Id
            ?? (await api.SendAsync<CreatedResponse>(HttpMethod.Post, $"api/quality/specs/{specId}/drafts", new { }, ct)).Id;
        var draft = await api.GetAsync<SpecVersionDto>($"api/quality/specs/{specId}/versions/{draftId}", ct);
        var saved = await api.SendAsync<SaveSpecDraftResponse>(HttpMethod.Put, $"api/quality/specs/{specId}/versions/{draftId}", new SaveSpecDraftRequest
        {
            Version = draft.Version,
            Definition = new SpecDefinitionDto
            {
                Items = [.. DemoPlan.Characteristics.Select((c, i) => new SpecItemDto
                {
                    Id = Guid.NewGuid(),
                    CharacteristicId = characteristics[c.Code],
                    Lsl = c.Lsl,
                    Target = (decimal)c.Target,
                    Usl = c.Usl,
                    SortOrder = i + 1,
                })],
            },
        }, ct);
        if (saved.Problems.Count > 0)
        {
            throw new InvalidOperationException($"The specification can't be released: {string.Join("; ", saved.Problems.Select(p => p.Message))}");
        }

        await api.SendAsync<VersionResponse>(HttpMethod.Post, $"api/quality/specs/{specId}/versions/{draftId}/release",
            new ReleaseSpecVersionRequest { Version = saved.Version, Note = $"Demo specification ({Made})" }, ct);
        Console.WriteLine($"Specification for {material.Code} released.");
    }

    private async Task LimitsAsync(Guid characteristicId, DemoPlan.Characteristic plan, MaterialDto material, CancellationToken ct)
    {
        var limits = await api.GetAsync<IReadOnlyList<ControlLimitDto>>($"api/quality/characteristics/{characteristicId}/control-limits", ct);
        if (limits.Any(l => l.MaterialId == material.Id && l.EffectiveTo is null))
        {
            return;
        }

        var design = plan.Limits();
        await api.SendAsync<ControlLimitChangeResponse>(HttpMethod.Post, $"api/quality/characteristics/{characteristicId}/control-limits/manual", new SetManualControlLimitsRequest
        {
            MaterialId = material.Id,
            CenterLine = design.Center,
            Ucl = design.Ucl,
            Lcl = design.Lcl,
            DispersionCenter = design.MovingRange,
            DispersionUcl = design.MovingRangeUcl,
            DispersionLcl = 0,
            Note = $"From the process simulator's design: {plan.Target} with sigma {plan.Sigma} ({Made}). Work out limits from a baseline for a real process.",
        }, ct);
        Console.WriteLine($"Control limits for {plan.Code} on {material.Code}: {design.Lcl:0.###} to {design.Ucl:0.###}.");
    }

    // One operation on the extrusion line, for the demo's material.
    private async Task<Guid> ProcessAsync(EquipmentDto site, EquipmentDto line, MaterialDto material, CancellationToken ct)
    {
        var summary = (await api.GetAsync<IReadOnlyList<ProcessSummaryDto>>($"api/processes?siteId={site.Id}", ct)).FirstOrDefault(p => p.Code == DemoPlan.ProcessCode);
        var processId = summary?.Id ?? (await api.SendAsync<CreatedResponse>(HttpMethod.Post, "api/processes", new CreateProcessRequest
        {
            SiteId = site.Id,
            Code = DemoPlan.ProcessCode,
            Name = "Demo extrusion",
            Description = $"Extrude {material.Code} on {line.Code} ({Made})",
        }, ct)).Id;
        var process = await api.GetAsync<ProcessDto>($"api/processes/{processId}", ct);
        if (process.Versions.Any(v => v.Status == "released"))
        {
            return processId;
        }

        if (!process.MakesMaterials.Any(m => m.Id == material.Id))
        {
            await api.SendAsync<VersionResponse>(HttpMethod.Put, $"api/processes/{processId}/scope",
                new SetProcessScopeRequest { Version = process.Version, MaterialIds = [material.Id] }, ct);
        }

        var draftId = process.Versions.First(v => v.Status == "draft").Id;
        var draft = await api.GetAsync<ProcessVersionDto>($"api/processes/{processId}/versions/{draftId}", ct);
        var saved = await api.SendAsync<SaveProcessDraftResponse>(HttpMethod.Put, $"api/processes/{processId}/versions/{draftId}", new SaveProcessDraftRequest
        {
            Version = draft.Version,
            Definition = new ProcessDefinitionDto
            {
                Operations = [new ProcessOperationDto { Id = Guid.NewGuid(), Number = 10, Name = "Extrude", Equipment = [new OperationEquipmentDto(line.Id, 100)] }],
            },
        }, ct);
        if (saved.Problems.Count > 0)
        {
            throw new InvalidOperationException($"The process can't be released: {string.Join("; ", saved.Problems.Select(p => p.Message))}");
        }

        await api.SendAsync<ReleaseProcessVersionResponse>(HttpMethod.Post, $"api/processes/{processId}/versions/{draftId}/release",
            new ReleaseProcessVersionRequest { Version = saved.Version, Note = $"Demo process ({Made})" }, ct);
        Console.WriteLine($"Process {DemoPlan.ProcessCode} released: operation 10 on {line.Code}.");
        return processId;
    }

    // The order the line runs: dispatched, and its job started, so the line is running a job while the process values are sampled.
    private async Task OrderAsync(EquipmentDto site, Guid processId, MaterialDto material, CancellationToken ct)
    {
        var orders = await api.GetAsync<IReadOnlyList<WorkOrderDto>>($"api/work-orders?siteId={site.Id}", ct);
        if (orders.Any(o => o.OrderNumber == DemoPlan.OrderNumber))
        {
            return;
        }

        var created = await api.SendAsync<CreatedResponse>(HttpMethod.Post, "api/work-orders", new CreateWorkOrderRequest
        {
            ProcessId = processId,
            OrderNumber = DemoPlan.OrderNumber,
            MaterialId = material.Id,
            PlannedQty = DemoPlan.OrderQuantity,
            UomId = material.BaseUomId,
            PlannedStart = DateTimeOffset.UtcNow,
            Notes = $"The demo's running order ({Made}).",
        }, ct);
        var dispatched = await api.SendAsync<DispatchWorkOrderResponse>(HttpMethod.Post, $"api/work-orders/{created.Id}/dispatch",
            new WorkOrderPlanningRequest { Version = created.Version, Reason = "Demo" }, ct);
        var job = dispatched.Jobs.Single();
        await api.SendAsync<ExecutionResponse>(HttpMethod.Post, $"api/jobs/{job.JobId}/start", new StartJobRequest { Version = job.Version }, ct);
        Console.WriteLine($"Work order {DemoPlan.OrderNumber} for {DemoPlan.OrderQuantity:0} {material.BaseUomCode} of {material.Code} dispatched; its job is running on {DemoPlan.LineCode}.");
    }
}
