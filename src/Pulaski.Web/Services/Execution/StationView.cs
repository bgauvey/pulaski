using Pulaski.Contracts.Execution;

namespace Pulaski.Web.Services.Execution;

/// <summary>What an operator station says about who is signed on (#150).</summary>
public static class StationView
{
    /// <summary>
    /// Why the signed-on person can't do everything here, in a sentence; null when they can run jobs, report and code stops, or no one is
    /// signed on.
    /// </summary>
    public static string? Limits(StationDto station)
    {
        if (station.PersonName is null)
        {
            return null;
        }

        if (!station.PersonIsUser)
        {
            return "You have no Pulaski user, so the station can't run jobs or record production for you. Ask a supervisor to link your badge to a user.";
        }

        var cannot = new List<string>();
        if (!station.CanRunJobs)
        {
            cannot.Add("start or stop jobs");
        }

        if (!station.CanReport)
        {
            cannot.Add("report production");
        }

        if (!station.CanCodeStops)
        {
            cannot.Add("code stops");
        }

        return cannot.Count switch
        {
            0 => null,
            3 => "Your role doesn't let you run jobs, report production or code stops at this site.",
            _ => $"Your role doesn't let you {string.Join(" or ", cannot)} at this site.",
        };
    }
}
