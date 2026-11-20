using Pulaski.Application.Abstractions;
using Pulaski.Application.Execution;
using Pulaski.Application.Security;
using Pulaski.Contracts.Execution;

namespace Pulaski.Api.Security;

/// <summary>What every operator station action needs (#147), injected as one.</summary>
internal sealed class StationServices(
    BadgeTickets tickets, ISecurityReadStore security, TimeProvider time, IQueryHandler<GetStationQuery, StationDto> station)
{
    public BadgeTickets Tickets { get; } = tickets;
    public ISecurityReadStore Security { get; } = security;
    public TimeProvider Time { get; } = time;
    public IQueryHandler<GetStationQuery, StationDto> Station { get; } = station;
}
