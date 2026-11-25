using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Andon;
using Pulaski.Application.Errors;
using Pulaski.Application.Execution;
using Pulaski.Application.Labor;
using Pulaski.Application.Localization;
using Pulaski.Application.MachineData;
using Pulaski.Application.Maintenance;
using Pulaski.Application.Security;
using Pulaski.Contracts.Andon;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Execution;
using Pulaski.Contracts.Labor;
using Pulaski.Contracts.MachineData;
using Pulaski.Contracts.Maintenance;
using Pulaski.Domain.Labor;
using Pulaski.Domain.Security;
using Pulaski.Domain;

namespace Pulaski.Api.Endpoints;

/// <summary>Shop-floor terminals and badges (D-049): managing them, and what a terminal does.</summary>
internal static class TerminalEndpoints
{
    /// <summary>Registering terminals and issuing badges: signed-in users with the permission.</summary>
    public static IEndpointRouteBuilder MapTerminalAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        var terminals = routes.MapGroup("/terminals").WithTags("Terminals");

        terminals.MapGet("/", async Task<Ok<IReadOnlyList<TerminalDto>>> (Guid siteId, IQueryHandler<ListTerminalsQuery, IReadOnlyList<TerminalDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListTerminalsQuery(siteId), ct)))
        .RequirePermission(Permissions.Terminals, Site.Query());

        terminals.MapPost("/", async Task<Created<CreatedResponse>> (CreateTerminalRequest request, ICommandHandler<CreateTerminalCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new CreateTerminalCommand(request.Code, request.Name, request.ScopeEquipmentId, request.DefaultEquipmentId, request.AuthMode)
            {
                Language = request.Language,
                Region = request.Region,
            }, ct);
            return TypedResults.Created($"/api/terminals/{created.Id}", created);
        })
        .RequirePermission(Permissions.Terminals, Site.FromBody<CreateTerminalRequest>(SiteOf.Equipment, r => r.ScopeEquipmentId));

        terminals.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (Guid id, UpdateTerminalRequest request, ICommandHandler<UpdateTerminalCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdateTerminalCommand(id, request.Version, request.Name, request.ScopeEquipmentId, request.DefaultEquipmentId,
                request.AuthMode, request.IsActive)
            {
                Language = request.Language,
                Region = request.Region,
            }, ct)))
        .RequirePermission(Permissions.Terminals, Site.Of(SiteOf.Terminal));

        terminals.MapPost("/{id:guid}/enrolment-code", async Task<Ok<EnrolmentCodeResponse>> (Guid id, ICommandHandler<IssueEnrolmentCodeCommand, EnrolmentCodeResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new IssueEnrolmentCodeCommand(id), ct)))
        .RequirePermission(Permissions.Terminals, Site.Of(SiteOf.Terminal));

        var badges = routes.MapGroup("/badges").WithTags("Terminals");

        badges.MapGet("/", async Task<Ok<IReadOnlyList<BadgeDto>>> (Guid siteId, IQueryHandler<ListBadgesQuery, IReadOnlyList<BadgeDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListBadgesQuery(siteId), ct)))
        .RequireAny(Site.Query(), Permissions.People, Permissions.LaborView);

        badges.MapPost("/", async Task<Created<CreatedResponse>> (IssueBadgeRequest request, ICommandHandler<IssueBadgeCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new IssueBadgeCommand(request.PersonId, request.BadgeValue, request.Pin), ct);
            return TypedResults.Created($"/api/badges/{created.Id}", created);
        })
        .RequirePermission(Permissions.People, Site.FromBody<IssueBadgeRequest>(SiteOf.Person, r => r.PersonId));

        badges.MapPost("/{id:guid}/revoke", async Task<Ok<VersionResponse>> (Guid id, RevokeBadgeRequest request, ICommandHandler<RevokeBadgeCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new RevokeBadgeCommand(id, request.Reason), ct)))
        .RequirePermission(Permissions.People, Site.Of(SiteOf.Badge));

        return routes;
    }

    /// <summary>
    /// What a terminal does (D-049). Not for users: enrolling needs only the one-time code; everything else the terminal's
    /// secret, and acting for a person the ticket their badge gave it.
    /// </summary>
    public static IEndpointRouteBuilder MapTerminalEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/enrol", async Task<Ok<TerminalEnrolledResponse>> (EnrolTerminalRequest request, ICommandHandler<EnrolTerminalCommand, TerminalEnrolledResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new EnrolTerminalCommand(request.Code), ct)))
        .WithTags("Terminal");

        var terminal = routes.MapGroup("").WithTags("Terminal").AddEndpointFilter<TerminalFilter>();

        terminal.MapGet("/", async Task<Ok<TerminalStatusDto>> (HttpContext http, IQueryHandler<GetTerminalStatusQuery, TerminalStatusDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetTerminalStatusQuery(TerminalFilter.Current(http), null), ct)));

        // The line display board (#120): the terminal's lines as they stand now, read-only; ?line= narrows it to one line.
        terminal.MapGet("/board", async Task<Ok<LineDisplayDto>> (string? line, HttpContext http, IQueryHandler<GetLineDisplayQuery, LineDisplayDto> handler, CancellationToken ct) =>
        {
            var current = TerminalFilter.Current(http);
            var board = await handler.HandleAsync(new GetLineDisplayQuery(current.SiteId, current.ScopeEquipmentId, current.Name, line), ct);
            return TypedResults.Ok(board with { Language = current.Language, Region = current.Region });
        });

        // An operator station (#146): sign on by badge for StationIdle, then read the station by that ticket. An expired or foreign ticket
        // reads as waiting rather than failing, so the screen goes back to its badge prompt by itself.
        terminal.MapPost("/station/sign-on", async Task<Ok<StationResponse>> (
            StationSignOnRequest request, HttpContext http, BadgeTickets tickets, ICommandHandler<IdentifyBadgeCommand, BadgeIdentity> identify,
            IQueryHandler<GetStationQuery, StationDto> station, CancellationToken ct) =>
        {
            var current = TerminalFilter.Current(http);
            var who = await identify.HandleAsync(new IdentifyBadgeCommand(current, request.BadgeValue, request.Pin), ct);
            if (who.PersonId is not { } personId)
            {
                return TypedResults.Ok(new StationResponse { NeedsPin = true });
            }

            var (ticket, expires) = tickets.Issue(current.Id, personId, TerminalRules.StationIdle);
            return TypedResults.Ok(new StationResponse
            {
                Ticket = ticket,
                TicketExpiresAt = expires,
                Station = await station.HandleAsync(new GetStationQuery(current, personId, request.Line), ct),
            });
        });

        terminal.MapPost("/station", async Task<Ok<StationResponse>> (
            StationRequest request, HttpContext http, BadgeTickets tickets, IQueryHandler<GetStationQuery, StationDto> station, CancellationToken ct) =>
        {
            var current = TerminalFilter.Current(http);
            var personId = request.Ticket is { Length: > 0 } ticket ? tickets.PersonOrNull(ticket, current.Id) : null;
            return TypedResults.Ok(new StationResponse
            {
                Ticket = personId is null ? null : request.Ticket,
                Station = await station.HandleAsync(new GetStationQuery(current, personId, request.Line), ct),
            });
        });

        // Live changes for boards and stations (#143b, D-117): those at the terminal's site, as server-sent events like /api/live.
        terminal.MapGet("/live", (HttpContext http, Application.Live.LiveHub hub, TimeProvider time, CancellationToken ct) =>
        {
            var site = TerminalFilter.Current(http).SiteId;
            return LiveEndpoints.StreamAsync(http, hub, time, change => change.SiteId is null || change.SiteId == site, ct);
        }).ExcludeFromDescription();

        // A file of the running job's instructions (#126b): only one the station shows, for a line of the terminal's.
        terminal.MapGet("/station/instructions/{id:guid}/files/{fileId:guid}", async (
            Guid id, Guid fileId, string? line, HttpContext http, IQueryHandler<GetStationQuery, StationDto> station,
            IQueryHandler<Application.Instructions.GetInstructionFileQuery, Application.Instructions.InstructionFileContent> files, CancellationToken ct) =>
        {
            var current = TerminalFilter.Current(http);
            var shown = await station.HandleAsync(new GetStationQuery(current, null, line), ct);
            if (!shown.Instructions.Any(i => i.InstructionId == id && i.Files.Any(f => f.Id == fileId)))
            {
                throw new NotFoundException("That file isn't one of the running job's instructions.");
            }

            var file = await files.HandleAsync(new Application.Instructions.GetInstructionFileQuery(id, fileId), ct);
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return TypedResults.File(file.Content, file.ContentType, file.FileName);
        });

        // Running jobs at the station (#147): as the badged person's user (#150), only a job of the station's line.
        terminal.MapPost("/station/start", (StationJobRequest request, HttpContext http, StationServices station, ICommandHandler<StartJobCommand, ExecutionResponse> handler, CancellationToken ct) =>
            StationJobAsync(http, station, request, UserMessage.Of("start jobs"), () => handler.HandleAsync(new StartJobCommand(request.JobId, request.Version), ct), ct));

        terminal.MapPost("/station/pause", (StationJobRequest request, HttpContext http, StationServices station, ICommandHandler<PauseJobCommand, ExecutionResponse> handler, CancellationToken ct) =>
            StationJobAsync(http, station, request, UserMessage.Of("pause jobs"),
                () => handler.HandleAsync(new PauseJobCommand(request.JobId, request.Version, request.ReasonCodeId, Text(request.Comment)), ct), ct));

        terminal.MapPost("/station/complete", (StationJobRequest request, HttpContext http, StationServices station, ICommandHandler<CompleteJobCommand, ExecutionResponse> handler, CancellationToken ct) =>
            StationJobAsync(http, station, request, UserMessage.Of("complete jobs"),
                () => handler.HandleAsync(new CompleteJobCommand(request.JobId, request.Version, request.ReasonCodeId, Text(request.Comment)), ct), ct));

        // Reporting at the station (#148): the running job only; from the machine, the figures the station showed, counted to their time,
        // which the report checks against the run's machine coverage as the Floor's does (D-097).
        terminal.MapPost("/station/report", async Task<Ok<StationActionResponse>> (
            StationReportRequest request, HttpContext http, StationServices station, ICommandHandler<ReportProductionCommand, ReportProductionResponse> report,
            CancellationToken ct) =>
        {
            var (terminal, _, ticket, expires) = await StationActing.BeginAsync(
                http, station.Tickets, station.Security, station.Time, request.Ticket, Domain.Security.Permissions.ProductionReport, UserMessage.Of("report production"));
            var personId = station.Tickets.PersonOf(ticket, terminal.Id);
            var before = await station.Station.HandleAsync(new GetStationQuery(terminal, personId, request.Line), ct);
            if (before.RunningJob?.JobId != request.JobId || before.Report is not { } form)
            {
                throw new DomainRuleException("That job isn't running on this station's line; report it from the Floor.");
            }

            if (form.Job.BatchSize is not null)
            {
                throw new DomainRuleException($"{form.Job.MaterialCode} is made in batches; report its batches on the Floor.");
            }

            if (request.FromMachine && request.MachineThrough is null)
            {
                throw new DomainRuleException("The machine's count has no time; count again.");
            }

            var (good, scrap) = (request.GoodQty, request.ScrapQty);
            var command = new ReportProductionCommand(
                request.JobId, null, good, scrap, request.FromMachine ? request.UomId : null, scrap > 0 ? request.ScrapReasonCodeId : null,
                good > 0 ? request.LocationId : null, Comment: Text(request.Comment))
            {
                MachineThrough = request.FromMachine ? request.MachineThrough : null,
            };
            var result = await report.HandleAsync(command, ct);
            return TypedResults.Ok(new StationActionResponse
            {
                Notes = [StationReporting.Done(good, scrap, request.FromMachine ? form.Machine?.UomCode : form.Job.UomCode, result), .. result.Warnings.Select(Notes.Of)],
                Ticket = ticket,
                TicketExpiresAt = expires,
                Station = await station.Station.HandleAsync(new GetStationQuery(terminal, personId, request.Line), ct),
            });
        });

        // Coding a stop at the station (#149): only one of its stops to code, as the signed-on person.
        terminal.MapPost("/station/code-stop", async Task<Ok<StationActionResponse>> (
            StationStopRequest request, HttpContext http, StationServices station, ICommandHandler<CodeDowntimeCommand, DowntimeCodedResponse> code,
            CancellationToken ct) =>
        {
            var (terminal, _, ticket, expires) = await StationActing.BeginAsync(
                http, station.Tickets, station.Security, station.Time, request.Ticket, Domain.Security.Permissions.DowntimeCode, UserMessage.Of("code stops"));
            var personId = station.Tickets.PersonOf(ticket, terminal.Id);
            var before = await station.Station.HandleAsync(new GetStationQuery(terminal, personId, request.Line), ct);
            var stop = before.StopsToCode.FirstOrDefault(s => s.Id == request.StopId)
                ?? throw new DomainRuleException("That stop isn't waiting for a reason on this station's line; code it on Machines.");
            await code.HandleAsync(new CodeDowntimeCommand(stop.Id, stop.StartedAt, request.ReasonCodeId, Text(request.Comment)), ct);
            var reason = before.CodingReasons.GetValueOrDefault(stop.EquipmentId)?.FirstOrDefault(r => r.Id == request.ReasonCodeId)?.Name;
            return TypedResults.Ok(new StationActionResponse
            {
                Notes = [reason is null ? Notes.Of($"{stop.EquipmentCode}'s stop is coded.") : Notes.Of($"{stop.EquipmentCode}'s stop is coded as {reason}.")],
                Ticket = ticket,
                TicketExpiresAt = expires,
                Station = await station.Station.HandleAsync(new GetStationQuery(terminal, personId, request.Line), ct),
            });
        });

        // A checklist at the station (#126c): one of the job's its instructions show, as the signed-on person; setup needs a role that runs
        // jobs, first article one that reports.
        terminal.MapPost("/station/checklist", async Task<Ok<StationActionResponse>> (
            StationChecklistRequest request, HttpContext http, StationServices station,
            ICommandHandler<Application.Instructions.CompleteChecklistCommand, Contracts.Instructions.ChecklistCompletedResponse> complete, CancellationToken ct) =>
        {
            var setup = request.Phase == Domain.Instructions.ChecklistPhases.Setup;
            var (terminal, _, ticket, expires) = await StationActing.BeginAsync(
                http, station.Tickets, station.Security, station.Time, request.Ticket,
                setup ? Domain.Security.Permissions.JobExecute : Domain.Security.Permissions.ProductionReport,
                setup ? UserMessage.Of("answer setup checklists") : UserMessage.Of("answer first-article checklists"));
            var personId = station.Tickets.PersonOf(ticket, terminal.Id);
            var before = await station.Station.HandleAsync(new GetStationQuery(terminal, personId, request.Line), ct);
            if (before.InstructionsJobId != request.JobId || !before.Instructions.Any(i => i.InstructionId == request.InstructionId && i.Checklists.Any(c => c.Phase == request.Phase)))
            {
                throw new DomainRuleException("That checklist isn't one this station shows; answer it on the job's page.");
            }

            var done = await complete.HandleAsync(new Application.Instructions.CompleteChecklistCommand(request.JobId, request.InstructionId, request.Phase, request.Answers), ct);
            return TypedResults.Ok(new StationActionResponse
            {
                Notes = [done.Passed ? Notes.Of($"The checklist passed.")
                    : done.FailedItems.Count == 1 ? Notes.Of($"The checklist failed on item {done.FailedItems[0]}: put it right and answer it again.")
                    : Notes.Of($"The checklist failed on items {string.Join(", ", done.FailedItems)}: put them right and answer it again.")],
                Ticket = ticket,
                TicketExpiresAt = expires,
                Station = await station.Station.HandleAsync(new GetStationQuery(terminal, personId, request.Line), ct),
            });
        });

        // A second sign-off at the station (#126d): of one of the shown job's checklists waiting for it, as the signed-on person.
        terminal.MapPost("/station/checklist/sign-off", async Task<Ok<StationActionResponse>> (
            StationChecklistSignOffRequest request, HttpContext http, StationServices station,
            ICommandHandler<Application.Instructions.SignOffChecklistCommand, Contracts.Instructions.ChecklistSignedOffResponse> signOff, CancellationToken ct) =>
        {
            var (terminal, _, ticket, expires) = await StationActing.BeginAsync(
                http, station.Tickets, station.Security, station.Time, request.Ticket, Domain.Security.Permissions.ChecklistSignOff,
                UserMessage.Of("sign off checklists"));
            var personId = station.Tickets.PersonOf(ticket, terminal.Id);
            var before = await station.Station.HandleAsync(new GetStationQuery(terminal, personId, request.Line), ct);
            if (!before.Instructions.Any(i => i.Checklists.Any(c => c.ResponseId == request.ResponseId && c.SignOff == "pending")))
            {
                throw new DomainRuleException("That checklist isn't one waiting for a sign-off at this station; sign it off on the job's page.");
            }

            var done = await signOff.HandleAsync(new Application.Instructions.SignOffChecklistCommand(request.ResponseId, request.Approve, Text(request.Note)), ct);
            return TypedResults.Ok(new StationActionResponse
            {
                Notes = [done.SignOff == "signed" ? Notes.Of($"The checklist is signed off.") : Notes.Of($"The checklist is rejected: it is to be answered again.")],
                Ticket = ticket,
                TicketExpiresAt = expires,
                Station = await station.Station.HandleAsync(new GetStationQuery(terminal, personId, request.Line), ct),
            });
        });

        // A sample at the station (#136b): one the running job's plans ask for, recorded against the job as the signed-on person, at the line.
        terminal.MapPost("/station/sample", async Task<Ok<StationActionResponse>> (
            StationSampleRequest request, HttpContext http, StationServices station,
            ICommandHandler<Application.Quality.RecordQualityResultCommand, Contracts.Quality.QualityResultResponse> record, CancellationToken ct) =>
        {
            var (terminal, _, ticket, expires) = await StationActing.BeginAsync(
                http, station.Tickets, station.Security, station.Time, request.Ticket, Domain.Security.Permissions.QualityRecord, UserMessage.Of("record quality results"));
            var personId = station.Tickets.PersonOf(ticket, terminal.Id);
            var before = await station.Station.HandleAsync(new GetStationQuery(terminal, personId, request.Line), ct);
            if (!before.Samples.Any(s => s.JobId == request.JobId && s.CharacteristicId == request.CharacteristicId))
            {
                throw new DomainRuleException("That isn't a sample the running job's plan asks for; record it on the Quality page.");
            }

            var attribute = before.Samples.First(s => s.CharacteristicId == request.CharacteristicId).DataKind == "attribute";
            var result = await record.HandleAsync(new Application.Quality.RecordQualityResultCommand(
                terminal.SiteId, request.CharacteristicId, null, request.JobId, null,
                attribute ? null : request.Values, attribute ? request.Inspected : null, attribute ? request.Defects : null, "manual", Text(request.Comment)), ct);
            return TypedResults.Ok(new StationActionResponse
            {
                Notes = [result.Conforms switch
                {
                    true => Notes.Of($"Recorded: in specification."),
                    false => Notes.Of($"Recorded: out of specification. Tell quality."),
                    _ => Notes.Of($"Recorded."),
                }, .. result.Notes],
                Ticket = ticket,
                TicketExpiresAt = expires,
                Station = await station.Station.HandleAsync(new GetStationQuery(terminal, personId, request.Line), ct),
            });
        });

        // Andon at the station (#157): call for help on its line, or answer one of its line's calls, as the signed-on person.
        terminal.MapPost("/station/andon/call", async Task<Ok<StationActionResponse>> (
            StationAndonRequest request, HttpContext http, StationServices station, ICommandHandler<RaiseAndonCallCommand, AndonCallResponse> raise,
            CancellationToken ct) =>
        {
            var (terminal, _, ticket, expires) = await StationActing.BeginAsync(
                http, station.Tickets, station.Security, station.Time, request.Ticket, Domain.Security.Permissions.AndonCall, UserMessage.Of("call for help"));
            var personId = station.Tickets.PersonOf(ticket, terminal.Id);
            var before = await station.Station.HandleAsync(new GetStationQuery(terminal, personId, request.Line), ct);
            var type = before.AndonTypes.FirstOrDefault(t => t.Id == request.TypeId);
            if (before.Line is null || type is null)
            {
                throw new DomainRuleException("This station doesn't offer that call; choose one of its buttons.");
            }

            var result = await raise.HandleAsync(new RaiseAndonCallCommand(before.Line.LineId, type.Id, Text(request.Text), terminal.Code), ct);
            return await StationAndonAsync(station, terminal, personId, request.Line, ticket, expires, StationAndon.Called(type.Name, result), ct);
        });

        // Maintenance at the station (#127): request it for the line, or for one of its stops to code (the stop's machine), as the signed-on person.
        terminal.MapPost("/station/maintenance/request", async Task<Ok<StationActionResponse>> (
            StationMaintenanceRequest request, HttpContext http, StationServices station,
            ICommandHandler<RaiseMaintenanceRequestCommand, MaintenanceRequestResponse> raise, CancellationToken ct) =>
        {
            var (terminal, _, ticket, expires) = await StationActing.BeginAsync(
                http, station.Tickets, station.Security, station.Time, request.Ticket, Domain.Security.Permissions.MaintenanceRequest,
                UserMessage.Of("request maintenance"));
            var personId = station.Tickets.PersonOf(ticket, terminal.Id);
            var before = await station.Station.HandleAsync(new GetStationQuery(terminal, personId, request.Line), ct);
            if (before.Line is null)
            {
                throw new DomainRuleException("This station has no line to request maintenance for.");
            }

            var stop = request.StopId is { } stopId
                ? before.StopsToCode.FirstOrDefault(s => s.Id == stopId) ?? throw new DomainRuleException("That stop isn't waiting on this station's line any more.")
                : null;
            var result = await raise.HandleAsync(new RaiseMaintenanceRequestCommand(
                stop?.EquipmentId ?? before.Line.LineId, request.Urgent ? "high" : null, Text(request.Text), stop?.Id, stop?.StartedAt, terminal.Code), ct);
            var note = result.AlreadyOpen
                ? Notes.Of($"This stop already has {result.Number}, still waiting for maintenance.")
                : Notes.Of($"{result.Number} is waiting for maintenance.");
            return await StationAndonAsync(station, terminal, personId, request.Line, ticket, expires, note, ct);
        });

        terminal.MapPost("/station/andon/acknowledge", async Task<Ok<StationActionResponse>> (
            StationAndonRequest request, HttpContext http, StationServices station,
            ICommandHandler<AcknowledgeAndonCallCommand, AndonCallResponse> acknowledge, CancellationToken ct) =>
        {
            var (terminal, personId, call, ticket, expires) = await StationCallAsync(http, station, request, UserMessage.Of("answer calls for help"), ct);
            await acknowledge.HandleAsync(new AcknowledgeAndonCallCommand(call.Id), ct);
            return await StationAndonAsync(station, terminal, personId, request.Line, ticket, expires, Notes.Of($"You're on the {call.TypeName} call."), ct);
        });

        terminal.MapPost("/station/andon/resolve", async Task<Ok<StationActionResponse>> (
            StationAndonRequest request, HttpContext http, StationServices station,
            ICommandHandler<ResolveAndonCallCommand, AndonCallResponse> resolve, CancellationToken ct) =>
        {
            var (terminal, personId, call, ticket, expires) = await StationCallAsync(http, station, request, UserMessage.Of("answer calls for help"), ct);
            await resolve.HandleAsync(new ResolveAndonCallCommand(call.Id, Text(request.Text)), ct);
            return await StationAndonAsync(station, terminal, personId, request.Line, ticket, expires, Notes.Of($"The {call.TypeName} call is resolved."), ct);
        });

        terminal.MapPost("/identify", async Task<Ok<BadgeIdentityResponse>> (
            IdentifyBadgeRequest request, HttpContext http, BadgeTickets tickets, ICommandHandler<IdentifyBadgeCommand, BadgeIdentity> identify,
            IQueryHandler<GetTerminalStatusQuery, TerminalStatusDto> status, CancellationToken ct) =>
        {
            var current = TerminalFilter.Current(http);
            var who = await identify.HandleAsync(new IdentifyBadgeCommand(current, request.BadgeValue, request.Pin), ct);
            if (who.PersonId is not { } personId)
            {
                return TypedResults.Ok(new BadgeIdentityResponse { NeedsPin = true });
            }

            var (ticket, expires) = tickets.Issue(current.Id, personId);
            return TypedResults.Ok(new BadgeIdentityResponse
            {
                Ticket = ticket,
                TicketExpiresAt = expires,
                Status = await status.HandleAsync(new GetTerminalStatusQuery(current, personId), ct),
            });
        });

        // The badged person's status again, by the ticket their badge gave (#84): the screen asks between steps, such as after choosing a kind.
        terminal.MapPost("/person", async Task<Ok<TerminalStatusDto>> (
            TerminalActionRequest request, HttpContext http, BadgeTickets tickets, IQueryHandler<GetTerminalStatusQuery, TerminalStatusDto> status, CancellationToken ct) =>
        {
            var current = TerminalFilter.Current(http);
            return TypedResults.Ok(await status.HandleAsync(new GetTerminalStatusQuery(current, tickets.PersonOf(request.Ticket, current.Id)), ct));
        });

        terminal.MapPost("/clock-on", (TerminalActionRequest request, HttpContext http, BadgeTickets tickets, ICommandHandler<TerminalClockOnCommand, LaborResponse> handler, CancellationToken ct) =>
            ActAsync(http, tickets, request, (t, person) => handler.HandleAsync(new TerminalClockOnCommand(t, person, request.EquipmentId ?? throw Missing("EquipmentId"), request.LaborType, request.ReasonCodeId), ct)));

        terminal.MapPost("/clock-off", (TerminalActionRequest request, HttpContext http, BadgeTickets tickets, ICommandHandler<TerminalClockOffCommand, LaborResponse> handler, CancellationToken ct) =>
            ActAsync(http, tickets, request, (t, person) => handler.HandleAsync(new TerminalClockOffCommand(t, person, request.SessionId ?? throw Missing("SessionId")), ct)));

        terminal.MapPost("/breaks/start", (TerminalActionRequest request, HttpContext http, BadgeTickets tickets, ICommandHandler<TerminalStartBreakCommand, LaborResponse> handler, CancellationToken ct) =>
            ActAsync(http, tickets, request, (t, person) => handler.HandleAsync(new TerminalStartBreakCommand(t, person, request.IsPaid), ct)));

        terminal.MapPost("/breaks/end", (TerminalActionRequest request, HttpContext http, BadgeTickets tickets, ICommandHandler<TerminalEndBreakCommand, LaborResponse> handler, CancellationToken ct) =>
            ActAsync(http, tickets, request, (t, person) => handler.HandleAsync(new TerminalEndBreakCommand(t, person), ct)));

        return routes;
    }

    private static async Task<Ok<LaborResponse>> ActAsync(
        HttpContext http, BadgeTickets tickets, TerminalActionRequest request, Func<TerminalRecord, Guid, Task<LaborResponse>> act)
    {
        var terminal = TerminalFilter.Current(http);
        return TypedResults.Ok(await act(terminal, tickets.PersonOf(request.Ticket, terminal.Id)));
    }

    private static async Task<Ok<StationActionResponse>> StationJobAsync(
        HttpContext http, StationServices services, StationJobRequest request, UserMessage what, Func<Task<ExecutionResponse>> act, CancellationToken ct)
    {
        var (terminal, _, ticket, expires) = await StationActing.BeginAsync(
            http, services.Tickets, services.Security, services.Time, request.Ticket, Domain.Security.Permissions.JobExecute, what);
        var personId = services.Tickets.PersonOf(ticket, terminal.Id);
        var before = await services.Station.HandleAsync(new GetStationQuery(terminal, personId, request.Line), ct);
        if (before.Line is null || !StationJobs.IsOnLine(before, request.JobId))
        {
            throw new DomainRuleException("That job isn't on this station's line; act on it from the Floor.");
        }

        var result = await act();
        return TypedResults.Ok(new StationActionResponse
        {
            Notes = result.Notes,
            Ticket = ticket,
            TicketExpiresAt = expires,
            Station = await services.Station.HandleAsync(new GetStationQuery(terminal, personId, request.Line), ct),
        });
    }

    // A call to answer must be one of the station's line's calls.
    private static async Task<(TerminalRecord Terminal, Guid PersonId, AndonCallDto Call, string Ticket, DateTimeOffset Expires)> StationCallAsync(
        HttpContext http, StationServices services, StationAndonRequest request, UserMessage what, CancellationToken ct)
    {
        var (terminal, _, ticket, expires) = await StationActing.BeginAsync(
            http, services.Tickets, services.Security, services.Time, request.Ticket, Domain.Security.Permissions.AndonRespond, what);
        var personId = services.Tickets.PersonOf(ticket, terminal.Id);
        var before = await services.Station.HandleAsync(new GetStationQuery(terminal, personId, request.Line), ct);
        var call = before.AndonCalls.FirstOrDefault(c => c.Id == request.CallId)
            ?? throw new DomainRuleException("That call isn't waiting on this station's line any more.");
        return (terminal, personId, call, ticket, expires);
    }

    private static async Task<Ok<StationActionResponse>> StationAndonAsync(
        StationServices services, TerminalRecord terminal, Guid personId, string? line, string ticket, DateTimeOffset expires, MessageDto note, CancellationToken ct) =>
        TypedResults.Ok(new StationActionResponse
        {
            Notes = [note],
            Ticket = ticket,
            TicketExpiresAt = expires,
            Station = await services.Station.HandleAsync(new GetStationQuery(terminal, personId, line), ct),
        });

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ValidationException Missing(string field) => new(new Dictionary<string, string[]> { [field] = ["Choose one."] });
}
