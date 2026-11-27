using Pulaski.Application.Andon;
using Pulaski.Application.Boms;
using Pulaski.Application.Execution;
using Pulaski.Application.Handover;
using Pulaski.Application.Inventory;
using Pulaski.Application.Labor;
using Pulaski.Application.MachineData;
using Pulaski.Application.Processes;
using Pulaski.Application.Quality;
using Pulaski.Application.Security;
using Pulaski.Application.Shifts;
using Pulaski.Application.WorkOrders;
using Pulaski.Contracts.Boms;
using Pulaski.Contracts.MachineData;
using Pulaski.Contracts.Processes;
using Pulaski.Contracts.Shifts;
using Pulaski.Domain.MachineData;

namespace Pulaski.DemoHistory;

/// <summary>
/// The demo plant's last two shifts, as its people and machines would have recorded them (D-101): a night crew and a day crew on extrusion
/// line 2 and the packer, with work orders made, dispatched, run and closed; production and scrap reports (one corrected, one reversed);
/// resin received, consumed, split, moved, spilled, held, released, rejected and shipped; line 2's machine states, stops (coded by the
/// machine and by people, entered, split), counts and alarms through its own edge node; manual quality results breaking control limits,
/// with an SPC alert closed and one left open; andon calls answered and waiting; labor with breaks, approved and corrected; and handover
/// notes and open items. Extrusion line 1 runs the demo's live job (DEMO-0001), so it gets only people, calls and notes.
///
/// Every step goes through the application's own command handler with the clock set to its moment, so the rows and events are what the
/// application writes. Steps run in time order; the current shift's are squeezed into the part of it gone by (<see cref="HistoryWindow"/>).
/// </summary>
public sealed class History(Commands commands, Lookup db, HistoryClock clock, HistoryWindow window)
{
    public const string ScheduleCode = "DEMO-3X8";
    private const string Password = "Pulaski-demo-2";
    private const string Admin = "admin";

    // Who works: each crew has a supervisor and an operator who sign in, and three people on the floor; quality, maintenance and planning
    // work across the shifts.
    private const string Quality = "k.nguyen";
    private const string Maintenance = "t.brandt";
    private const string Planner = "j.hale";

    private sealed record Crew(string Code, string Shift, string ShiftName, (string User, string Name) Supervisor, (string User, string Name) Operator, string[] Floor);

    private static readonly Crew[] Crews =
    [
        new("A", "DAY", "day", ("m.okafor", "Maya Okafor"), ("s.moreno", "Sofia Moreno"), ["Priya Shah", "Ben Carter", "Grace Liu"]),
        new("B", "EVE", "evening", ("e.novak", "Eva Novak"), ("o.haddad", "Omar Haddad"), ["Jun Park", "Lena Fischer", "Marco Rossi"]),
        new("C", "NGT", "night", ("d.lindqvist", "Dan Lindqvist"), ("r.patel", "Ravi Patel"), ["Luis Ortega", "Ana Kowalski", "Tariq Aziz"]),
    ];

    private Crew _prev = null!, _cur = null!;

    private static readonly string[] TagRoles = ["state", "unit_mode", "processed_count", "alarm"];

    private readonly List<(DateTimeOffset At, int Order, string Label, Func<Task> Run)> _steps = [];
    private readonly Dictionary<string, Guid> _id = new(StringComparer.Ordinal);
    private readonly Random _random = new(20261124);
    private MachineFeed _line2 = null!;
    private Guid _site, _ext01, _ext02, _pack1, _rmStore, _fgStage, _hopper;
    private Guid _meter, _kg, _white, _gray, _pvc, _tio2;
    private Guid _scheduleId;
    private string _prefix = "";
    private int _lotSeq;

    /// <summary>Line 2's lots as they're made, for the packer to take: number, id, made at, and whether quality wants to see it first.</summary>
    private readonly List<(string Number, Guid Id, DateTimeOffset At, bool Suspect)> _line2Lots = [];

    public async Task RunAsync()
    {
        await ReadPlantAsync();
        PlanMachine();
        PlanSetup();
        PlanPreviousShift();
        PlanCurrentShift();
        PlanMachineBatches();

        // In time order, including the steps that steps add (a report's later correction) as they come due.
        var end = window.Now - HistoryWindow.Margin;
        var done = new HashSet<int>();
        Console.WriteLine($"Recording the history from {window.PreviousStart:u} to {end:u}.");
        while (_steps.Where(s => s.At <= end && !done.Contains(s.Order)).OrderBy(s => s.At).ThenBy(s => s.Order).FirstOrDefault() is { Run: not null } step)
        {
            done.Add(step.Order);
            clock.Set(step.At);
            try
            {
                await step.Run();
            }
            catch (Exception ex)
            {
                var detail = ex is Pulaski.Application.Errors.ValidationException invalid
                    ? " " + string.Join("; ", invalid.Errors.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}"))
                    : "";
                throw new InvalidOperationException($"{step.At:u} {step.Label}: {ex.Message}{detail}", ex);
            }
        }

        Console.WriteLine($"Recorded {done.Count} steps.");
    }

    private void At(DateTimeOffset at, string label, Func<Task> run) => _steps.Add((at, _steps.Count, label, run));

    private async Task ReadPlantAsync()
    {
        _site = await db.IdAsync("SELECT id FROM core.equipment WHERE code = 'PLT1' AND level = 'site'");
        _ext01 = await db.EquipmentAsync("EXT-01");
        _ext02 = await db.EquipmentAsync("EXT-02");
        _pack1 = await db.EquipmentAsync("PACK1");
        _rmStore = await db.EquipmentAsync("RM-STORE");
        _fgStage = await db.EquipmentAsync("FG-STAGE");
        _hopper = await db.EquipmentAsync("EXT-01-HOP");
        _meter = await db.IdAsync("SELECT id FROM core.uom WHERE code = 'm'");
        _kg = await db.IdAsync("SELECT id FROM core.uom WHERE code = 'kg'");
        _white = await db.MaterialAsync("PRF-4410-WH");
        _gray = await db.MaterialAsync("PRF-4410-GY");
        _pvc = await db.MaterialAsync("PVC-K67");
        _tio2 = await db.MaterialAsync("TIO2-MB");
        _prev = CrewAt(window.PreviousStart);
        _cur = CrewAt(window.CurrentStart);
        _prefix = TimeZoneInfo.ConvertTime(window.PreviousStart, SiteTime.Zone).ToString("yyMMdd", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Crew CrewAt(DateTimeOffset start) =>
        TimeZoneInfo.ConvertTime(start, SiteTime.Zone).Hour switch { 6 => Crews[0], 14 => Crews[1], _ => Crews[2] };

    private DateTimeOffset P(int h, int m = 0) => window.Previous(h, m);

    private DateTimeOffset C(int h, int m = 0) => window.Current(h, m);

    private string NextLot(string line) => $"{line}-{_prefix}-{++_lotSeq:000}";

    // ---------------------------------------------------------------- line 2's machine

    private void PlanMachine()
    {
        _line2 = new MachineFeed(P(0), window.Now - HistoryWindow.Margin, 700m * 0.92m / 60m, seed: 7);
        _line2
            // Previous shift: white order HIST-1001, a starved spell, a micro-stop, a melt pump fault, a jam, then the change to gray.
            .State(P(0, 15), MachineFeed.Starting).State(P(0, 20), MachineFeed.Execute)
            .Stop(P(1, 10), TimeSpan.FromMinutes(12), MachineFeed.Suspended)
            .Stop(P(2, 5), TimeSpan.FromMinutes(4), MachineFeed.Stopped)
            .State(P(3, 0), MachineFeed.Aborted).State(P(3, 21), MachineFeed.Resetting).State(P(3, 23), MachineFeed.Starting).State(P(3, 25), MachineFeed.Execute)
            .Alarm(P(2, 59), P(3, 20), 4107)
            .Stop(P(4, 30), TimeSpan.FromMinutes(18), MachineFeed.Stopped)
            .State(P(5, 50), MachineFeed.Idle).State(P(6, 10), MachineFeed.Starting).State(P(6, 15), MachineFeed.Execute)
            .Stop(P(7, 10), TimeSpan.FromMinutes(22), MachineFeed.Stopped)
            // Current shift: gray HIST-1002 to its end, the change back to white HIST-1003, which is still running.
            // The first two hours of the shift hold one of each kind of stop, so a short current shift has them too.
            .Stop(C(0, 18), TimeSpan.FromMinutes(8), MachineFeed.Suspended)
            .Stop(C(0, 55), TimeSpan.FromMinutes(12), MachineFeed.Stopped)
            .State(C(1, 40), MachineFeed.Aborted).State(C(1, 50), MachineFeed.Starting).State(C(1, 52), MachineFeed.Execute)
            .Alarm(C(1, 39), C(1, 49), 4210)
            .State(C(2, 55), MachineFeed.Idle).State(C(3, 25), MachineFeed.Starting).State(C(3, 30), MachineFeed.Execute)
            .Stop(C(6, 0), TimeSpan.FromMinutes(6), MachineFeed.Stopped);
    }

    // The edge forwards what it collected every quarter of an hour (off the quarter, so it doesn't land on the people's steps), and whenever
    // someone looks at line 2's stops (FlushAsync), as a screen showing them would.
    private void PlanMachineBatches()
    {
        _signals = [.. _line2.Signals()];
        _stream = Guid.CreateVersion7(window.PreviousStart);
        for (var at = P(0, 7); at < window.Now; at += TimeSpan.FromMinutes(15))
        {
            At(at, "line 2's edge forwards its signals", () => FlushAsync());
        }

        At(window.Now - HistoryWindow.Margin, "line 2's edge forwards its last signals", () => FlushAsync());
    }

    private List<(DateTimeOffset At, string Role, decimal Value)> _signals = [];
    private Guid _stream;
    private long _sequence;
    private DateTimeOffset _sentTo = DateTimeOffset.MinValue;

    private async Task FlushAsync()
    {
        var to = clock.Now;
        var batch = _signals.Where(s => s.At > _sentTo && s.At <= to)
            .Select(s => new EdgeSignalDto { Sequence = ++_sequence, TagId = _id["tag:" + s.Role], OccurredAt = s.At, Value = s.Value })
            .ToList();
        _sentTo = to;
        if (batch.Count > 0)
        {
            await commands.SendAsync("line2-edge", new IngestEdgeSignalsCommand(_id["edge"], _stream, "demo-history", batch));
        }
    }

    // ---------------------------------------------------------------- set-up, just before the previous shift

    private void PlanSetup()
    {
        var at = P(0) - TimeSpan.FromMinutes(30);
        At(at, "shift schedule and crews", async () =>
        {
            foreach (var (code, name) in new[] { ("A", "Crew A (days)"), ("B", "Crew B (evenings)"), ("C", "Crew C (nights)") })
            {
                _id["crew:" + code] = (await commands.SendAsync(Admin, new CreateCrewCommand(_site, code, name))).Id;
            }
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(window.Now, SiteTime.Zone).DateTime);
            _scheduleId = (await commands.SendAsync(Admin, new CreateShiftScheduleCommand(_site, ScheduleCode, "Three shifts, every day",
                "Day, evening and night shifts of eight hours (made by the demo history)", 1, today.AddDays(-28),
                [
                    new ShiftDto { Code = "DAY", Name = "Day", Start = "06:00", Minutes = 480, Days = [0], Crew = "A", Breaks = [new PlannedBreakDto { Name = "Lunch", Start = "10:00", Minutes = 30 }] },
                    new ShiftDto { Code = "EVE", Name = "Evening", Start = "14:00", Minutes = 480, Days = [0], Crew = "B", Breaks = [new PlannedBreakDto { Name = "Lunch", Start = "18:00", Minutes = 30 }] },
                    new ShiftDto { Code = "NGT", Name = "Night", Start = "22:00", Minutes = 480, Days = [0], Crew = "C", Breaks = [new PlannedBreakDto { Name = "Lunch", Start = "02:00", Minutes = 30 }] },
                ]))).Id;
            await commands.SendAsync(Admin, new SetEquipmentCalendarCommand(_site, await db.VersionAsync("core.equipment", _site), "schedule", _scheduleId));
        });

        At(at, "users and people", async () =>
        {
            var number = 1100;
            foreach (var crew in Crews)
            {
                foreach (var (user, name, role) in new[] { (crew.Supervisor.User, crew.Supervisor.Name, "supervisor"), (crew.Operator.User, crew.Operator.Name, "operator") })
                {
                    commands.KnowUser(user, (await commands.SendAsync(Admin, new CreateUserCommand(user, name, null, Password, role, _site))).Id);
                    _id["person:" + name] = (await commands.SendAsync(Admin, new CreatePersonCommand(_site, $"{++number}", name, _id["crew:" + crew.Code], user))).Id;
                }

                foreach (var name in crew.Floor)
                {
                    _id["person:" + name] = (await commands.SendAsync(Admin, new CreatePersonCommand(_site, $"{++number}", name, _id["crew:" + crew.Code], null))).Id;
                }

                number += 100 - (number % 100);
            }

            foreach (var (user, name, role) in new[] { (Quality, "Kim Nguyen", "quality"), (Maintenance, "Tom Brandt", "operator"), (Planner, "Jordan Hale", "planner") })
            {
                commands.KnowUser(user, (await commands.SendAsync(Admin, new CreateUserCommand(user, name, null, Password, role, _site))).Id);
                _id["person:" + name] = (await commands.SendAsync(Admin, new CreatePersonCommand(_site, $"{++number}", name, null, user))).Id;
            }
        });

        At(at, "andon call types", async () =>
        {
            foreach (var (code, name, minutes, order) in new[] { ("MAINT", "Maintenance", 10, 1), ("QUALITY", "Quality", 15, 2), ("MATERIAL", "Material", 20, 3), ("SUPERVISOR", "Supervisor", 30, 4) })
            {
                _id["andon:" + code] = (await commands.SendAsync(Admin, new CreateAndonTypeCommand(_site, code, name, minutes, order))).Id;
            }
        });

        At(at, "line 2's edge node and tags", async () =>
        {
            _id["edge"] = (await commands.SendAsync(Admin, new CreateEdgeNodeCommand(_site, "LINE2-EDGE", "Line 2 edge",
                "Extrusion line 2's panel PC; its history was recorded by the demo history, nothing reads it live"))).Id;
            var connection = (await commands.SendAsync(Admin, new CreateEdgeConnectionCommand(_id["edge"], "LINE2-PLC", "Line 2 controller", "sparkplug_b",
                new Dictionary<string, string> { ["host"] = "broker", ["port"] = "1883", ["groupId"] = "Plant1" }))).Id;
            foreach (var role in TagRoles)
            {
                var metric = role switch { "state" => "Status/StateCurrent", "unit_mode" => "Status/UnitModeCurrent", "alarm" => "Admin/Alarm/0/ID", _ => "Admin/ProdProcessedCount/0/AccCount" };
                _id["tag:" + role] = (await commands.SendAsync(Admin, new CreateEquipmentTagCommand(_ext02, role, connection, $"Plant1/Line2/EXT-02:{metric}", 1, 0,
                    role == "processed_count" ? 2_147_483_647 : null))).Id;
            }

            var profile = await db.IdAsync("SELECT id FROM core.machine_profile WHERE code = 'PACKML-SIM'");
            await commands.SendAsync(Admin, new SetEquipmentMachineDataCommand(_ext02, await db.VersionAsync("core.equipment", _ext02), profile,
                StateDerivations.OwnTags, null, 300));
        });

        At(at, "the two-line process, its BOMs and line 2's control limits", async () =>
        {
            var process = await commands.SendAsync(Admin, new CreateProcessCommand(_site, "DEMO-PROFILE", "Extrude and pack profile",
                "Extrude on line 2, then bundle and wrap on the packer (made by the demo history)"));
            _id["process"] = process.Id;
            await commands.SendAsync(Admin, new SetProcessScopeCommand(process.Id, await db.VersionAsync("core.process", process.Id), false, [], [_white, _gray]));
            var draft = await db.IdAsync("SELECT id FROM core.process_version WHERE process_id = @id AND status = 'draft'", new { id = process.Id });
            Guid extrude = Guid.CreateVersion7(clock.Now), pack = Guid.CreateVersion7(clock.Now);
            var saved = await commands.SendAsync(Admin, new SaveProcessDraftCommand(process.Id, draft, await db.VersionAsync("core.process_version", draft), new ProcessDefinitionDto
            {
                Operations =
                [
                    new ProcessOperationDto { Id = extrude, Number = 10, Name = "Extrude", Equipment = [new OperationEquipmentDto(_ext02, 100)] },
                    new ProcessOperationDto { Id = pack, Number = 20, Name = "Bundle and wrap", X = 240, Equipment = [new OperationEquipmentDto(_pack1, 100)] },
                ],
                Dependencies = [new ProcessDependencyDto(Guid.CreateVersion7(clock.Now), extrude, pack, "after_start", TimeSpan.FromMinutes(30))],
            }));
            Problems("process", saved.Problems.Select(p => p.Message));
            await commands.SendAsync(Admin, new ReleaseProcessVersionCommand(process.Id, draft, saved.Version, "Demo process (made by the demo history)"));

            foreach (var (material, tio2) in new[] { (_white, 32m), (_gray, 6m) })
            {
                var bom = (await commands.SendAsync(Admin, new CreateBomCommand(material, process.Id))).Id;
                var version = await db.IdAsync("SELECT id FROM core.bom_version WHERE bom_id = @bom AND status = 'draft'", new { bom });
                var bomSaved = await commands.SendAsync(Admin, new SaveBomDraftCommand(bom, version, await db.VersionAsync("core.bom_version", version), new BomDefinitionDto
                {
                    BaseQty = 1000,
                    BaseUomId = _meter,
                    Items =
                    [
                        new BomItemDto { Id = Guid.CreateVersion7(clock.Now), LineNo = 10, ComponentMaterialId = _pvc, QtyPer = 868, UomId = _kg, ScrapFactor = 0.02m, ConsumptionMethod = "declared", OperationNumber = 10 },
                        new BomItemDto { Id = Guid.CreateVersion7(clock.Now), LineNo = 20, ComponentMaterialId = _tio2, QtyPer = tio2, UomId = _kg, ConsumptionMethod = "declared", OperationNumber = 10 },
                    ],
                }));
                Problems("BOM", bomSaved.Problems.Select(p => p.Message));
                await commands.SendAsync(Admin, new ReleaseBomVersionCommand(bom, version, bomSaved.Version, "Demo BOM (made by the demo history)"));
            }

            // Line 2's own limits (a line's limits win over the material's), from the same design as line 1's.
            foreach (var material in new[] { _white, _gray })
            {
                await commands.SendAsync(Quality, new SetManualControlLimitsCommand(await Characteristic("MELT-TEMP"), material, _ext02,
                    215, 218.6, 211.4, 1.354, 4.42, 0, null, "Line 2's melt temperature: 215 °C, sigma 1.2 (made by the demo history)"));
                await commands.SendAsync(Quality, new SetManualControlLimitsCommand(await Characteristic("PROFILE-WIDTH"), material, _ext02,
                    42.0, 42.12, 41.88, 0.0451, 0.147, 0, null, "Line 2's profile width: 42.00 mm, sigma 0.04 (made by the demo history)"));
            }
        });

        At(at, "OEE target and expected resin", async () =>
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(window.Now, SiteTime.Zone).DateTime);
            var targetVersion = await db.OneAsync<long?>("SELECT max(xmin::text::bigint) FROM core.oee_target WHERE site_id = @site", new { site = _site }) ?? 0;
            await commands.SendAsync(Admin, new SetOeeTargetCommand(_site, null, today.AddDays(-28), new OeeTargetValues(85, 90, 98, 75), targetVersion));
            await commands.SendAsync(Planner, new CreateExpectedReceiptCommand(_site, _tio2, 750, _kg, window.Now.AddDays(2), "PO-4471", "Northfield Colors",
                "TiO2 masterbatch, 30 bags"));
            await commands.SendAsync(Planner, new CreateExpectedReceiptCommand(_site, _pvc, 18_000, _kg, window.Now.AddDays(1), "PO-4468", "Lakeside Polymers",
                "PVC K67, one bulk truck"));
        });
    }

    // ---------------------------------------------------------------- previous shift

    private void PlanPreviousShift()
    {
        var prevSupervisor = _prev.Supervisor.User;
        var prevOperator = _prev.Operator.User;
        At(P(0, 2), $"{_prev.ShiftName} crew checks in", async () =>
        {
            await commands.SendAsync(prevSupervisor, new CheckInCommand(_ext02, [_id["person:" + _prev.Operator.Name], _id["person:" + _prev.Floor[0]]], null));
            await commands.SendAsync(prevSupervisor, new CheckInCommand(_pack1, [_id["person:" + _prev.Floor[1]]], null));
        });

        At(P(0, 5), "resin received", async () =>
        {
            foreach (var (material, number, qty, supplier) in new[] { (_pvc, "PVC-24-1187", 5000m, "LP-77812"), (_pvc, "PVC-24-1188", 5000m, "LP-77813"), (_tio2, "TIO-0932", 500m, "NC-2209") })
            {
                var lot = await commands.SendAsync(prevOperator, new ReceiveLotCommand(material, _rmStore, number, qty, _kg, supplier, window.Now.AddDays(300),
                    "Delivered on the evening truck"));
                _id["lot:" + number] = lot.LotId;
            }
        });

        At(P(0, 10), "work orders HIST-1001 and HIST-1002 made and dispatched", async () =>
        {
            await OrderAsync(prevSupervisor, "HIST-1001", _white, 4000, P(0, 15), "White 4410 for Harbor Windows, two pallets", dispatch: true);
            await OrderAsync(prevSupervisor, "HIST-1002", _gray, 3000, P(6, 0), "Gray 4410 stock replenishment", dispatch: true);
        });

        // Line 2 runs white, then gray.
        At(P(0, 20), "HIST-1001 starts on line 2", () => StartAsync(prevOperator, "HIST-1001", "EXT-02"));
        Consume(P(0, 40), prevOperator, "HIST-1001", "EXT-02", "PVC-24-1187", 1500);
        Consume(P(0, 40), prevOperator, "HIST-1001", "EXT-02", "TIO-0932", 60);
        Report(P(1, 20), prevOperator, "HIST-1001", P(0, 20), "STARTUP", 0.05m, "Start-up scrap while the profile settled");
        Report(P(2, 20), prevOperator, "HIST-1001", P(1, 20), "SURFACE", 0.015m, null, correctTo: (P(2, 30), "Typed the scrap as good: 40 m had surface marks"));
        Report(P(3, 0), prevOperator, "HIST-1001", P(2, 20), "DIMENSION", 0.01m, null);
        At(P(3, 1), "HIST-1001 paused for a breakdown", async () =>
            await commands.SendAsync(prevOperator, new PauseJobCommand(await JobAsync("HIST-1001", "EXT-02"), await JobVersionAsync("HIST-1001", "EXT-02"),
                await db.ReasonAsync("run_end", "BREAKDOWN"), "Melt pump overpressure, extruder tripped")));
        At(P(3, 25), "HIST-1001 restarted", () => StartAsync(prevOperator, "HIST-1001", "EXT-02"));
        Report(P(4, 20), prevOperator, "HIST-1001", P(3, 25), "STARTUP", 0.03m, "Restart after the pump repair", suspect: false);
        Consume(P(4, 25), prevOperator, "HIST-1001", "EXT-02", "PVC-24-1187", 1700);
        Report(P(5, 20), prevOperator, "HIST-1001", P(4, 20), "DIMENSION", 0.04m, "Width running high", suspect: true);
        Report(P(5, 50), prevOperator, "HIST-1001", P(5, 20), "DIMENSION", 0.02m, null);
        At(P(5, 51), "HIST-1001 complete on line 2", () => CompleteAsync(prevOperator, "HIST-1001", "EXT-02"));
        Changeover(P(5, 52), P(6, 15), prevOperator, "HIST-1002");
        Consume(P(6, 20), prevOperator, "HIST-1002", "EXT-02", "PVC-24-1188", 1200);
        Consume(P(6, 20), prevOperator, "HIST-1002", "EXT-02", "TIO-0932", 8);
        Report(P(7, 15), prevOperator, "HIST-1002", P(6, 15), "CHANGEOVER", 0.06m, "Purge to gray");
        Report(P(7, 50), prevOperator, "HIST-1002", P(7, 15), "SURFACE", 0.01m, null);

        // The packer takes line 2's lots and bundles them.
        At(P(1, 0), "HIST-1001 starts on the packer", () => StartAsync(prevSupervisor, "HIST-1001", "PACK1"));
        Pack(P(2, 40), prevSupervisor, "HIST-1001");
        Pack(P(3, 40), prevSupervisor, "HIST-1001");
        Pack(P(5, 0), prevSupervisor, "HIST-1001");
        Pack(P(6, 30), prevSupervisor, "HIST-1001");
        At(P(6, 40), "HIST-1001 complete on the packer", () => CompleteAsync(prevSupervisor, "HIST-1001", "PACK1"));
        // The packer may have stops of its own from the live simulator: the jam goes into the first 20 minutes free of them.
        At(P(6, 50), "a jam on the packer entered by hand", async () =>
        {
            for (var from = P(3, 30); from + TimeSpan.FromMinutes(20) <= P(6, 40); from += TimeSpan.FromMinutes(10))
            {
                var to = from + TimeSpan.FromMinutes(20);
                if (await db.OneAsync<int>("""
                    SELECT count(*)::int FROM ts.downtime WHERE equipment_id = @line AND superseded_at IS NULL
                      AND started_at < @to AND coalesce(ended_at, 'infinity') > @from
                    """, new { line = _pack1, from, to }) == 0)
                {
                    await commands.SendAsync(prevSupervisor, new EnterStopCommand(_pack1, from, to, await db.ReasonAsync("utilization", "JAM"),
                        "Film roll ran out mid-wrap and jammed the sealer"));
                    return;
                }
            }
        });

        // Stops: the machine's own reasons stand; people code the rest, and split one.
        At(P(4, 55), "the jam on line 2 coded", () => CodeStopAsync(prevOperator, _ext02, P(4, 30), "JAM", "Profile caught in the puller tracks"));
        At(P(6, 20), "the change to gray coded", () => CodeStopAsync(prevOperator, _ext02, P(5, 50), "SETUP", "Changeover white to gray: purge"));
        At(P(7, 45), "a stop on line 2 split and coded", async () =>
        {
            var stop = await StopAsync(_ext02, P(7, 10));
            var split = await commands.SendAsync(prevSupervisor, new SplitStopCommand(stop.Id, stop.StartedAt, P(7, 20),
                "Two problems: the operator adjusted the die, then waited for a quality check"));
            var parts = split.Stops.OrderBy(s => s.StartedAt).ToList();
            await commands.SendAsync(prevSupervisor, new CodeDowntimeCommand(parts[0].Id, parts[0].StartedAt, await db.ReasonAsync("utilization", "ADJUST"), "Die bolt adjustment"));
            await commands.SendAsync(prevSupervisor, new CodeDowntimeCommand(parts[1].Id, parts[1].StartedAt, await db.ReasonAsync("utilization", "QUALITY"), "Waited for the width check"));
        });

        // Andon.
        Andon(prevOperator, "EXT-02", "MATERIAL", "Hopper low, the resin hasn't come from stores", P(1, 8), (prevSupervisor, P(1, 15)), P(1, 22), "Resin brought up from stores");
        Andon(prevOperator, "EXT-02", "MAINT", "Melt pump pressure alarm, extruder tripped", P(2, 58), (Maintenance, P(3, 6)), P(3, 24), "Pressure transducer replaced; pump checked");
        Andon(prevSupervisor, "PACK1", "QUALITY", "Bundle wrap loose on the last pallet", P(5, 10), (Quality, P(5, 40)), P(6, 0), "Film tension reset; pallet rewrapped");
        Andon(prevOperator, "EXT-01", "SUPERVISOR", "Need sign-off on line 1's start-up checks", P(6, 40), (prevSupervisor, P(6, 55)), P(7, 5), "Checks signed off");

        // Quality: width and melt temperature every 20 minutes on line 2; the width drifts high before the half-way pallet.
        foreach (var (at, job) in Every(P(0, 40), P(5, 40), "HIST-1001").Concat(Every(P(6, 40), P(7, 55), "HIST-1002")))
        {
            var drift = at >= P(4, 40) && at <= P(5, 25) ? (at >= P(5, 20) ? 0.145m : 0.095m) : 0m;
            Sample(at, prevOperator, job, "EXT-02", "PROFILE-WIDTH", 42.00m + drift, 0.04m, 2);
            Sample(at.AddMinutes(1), prevOperator, job, "EXT-02", "MELT-TEMP", 215m, 0.8m, 1);
        }

        At(P(5, 30), "suspect lots held", async () =>
        {
            foreach (var lot in _line2Lots.Where(l => l.Suspect))
            {
                await commands.SendAsync(Quality, new ChangeLotStatusCommand(lot.Id, await db.VersionAsync("core.lot", lot.Id), "on_hold",
                    await db.ReasonAsync("hold", "SUSPECT"), "Width ran high while this was made; measure before packing"));
            }
        });
        At(P(5, 45), "the width alert closed", async () =>
        {
            var alert = await db.IdAsync("SELECT id FROM core.spc_alert WHERE equipment_id = @line AND characteristic_id = @width AND status = 'open'",
                new { line = _ext02, width = await Characteristic("PROFILE-WIDTH") });
            await commands.SendAsync(Quality, new CloseSpcAlertCommand(_site, alert, await db.ReasonAsync("spc_cause", "TOOLING"),
                "Die land cleaned and the puller speed trimmed; width back on 42.00"));
        });
        At(P(6, 45), "a held lot rejected", async () =>
        {
            var lot = _line2Lots.First(l => l.Suspect);
            await commands.SendAsync(Quality, new RejectLotCommand(lot.Id, await db.VersionAsync("core.lot", lot.Id), await db.ReasonAsync("scrap", "DIMENSION"),
                "Measured 42.14-42.16 mm: outside the specification"));
        });

        At(P(6, 0), "resin spilled", async () =>
        {
            var lot = _id["lot:PVC-24-1187"];
            await commands.SendAsync(prevOperator, new WriteOffLotCommand(lot, await db.VersionAsync("core.lot", lot), 25, _kg, await db.ReasonAsync("inventory_adjustment", "SPILL"),
                "Torn bag at the hopper loader"));
        });

        // Labor: breaks, then the crew checks out.
        Break(P(4, 0), P(4, 30), _prev.Operator.Name, false, prevSupervisor);
        Break(P(4, 30), P(5, 0), _prev.Floor[0], false, prevSupervisor);
        Break(P(4, 5), P(4, 35), _prev.Floor[1], false, prevSupervisor);
        Break(P(6, 0), P(6, 15), _prev.Floor[1], true, prevSupervisor);
        At(P(7, 57), $"{_prev.ShiftName} crew checks out", async () =>
        {
            await commands.SendAsync(prevSupervisor, new CheckOutCommand(_ext02, null));
            await commands.SendAsync(prevSupervisor, new CheckOutCommand(_pack1, null));
        });

        // Handover.
        Note(P(7, 30), prevSupervisor, "EXT-02", "note", "Running gray HIST-1002 at about 650 m/h. Melt pump transducer replaced at the 3-hour mark; watch the pressure.");
        Note(P(7, 35), prevSupervisor, "EXT-02", "open_item", "Die land needs a full clean at the next changeover: width drifted high mid-shift.", key: "die");
        Note(P(7, 40), prevSupervisor, "PACK1", "open_item", "Only 3 rolls of stretch film left; more requested from stores.", key: "film");
        Note(P(7, 42), prevOperator, "PACK1", "open_item", "Bundle counter misses about 1 in 200 bundles; maintenance to look at the sensor.", key: "counter");
        Note(P(7, 50), prevOperator, "EXT-01", "note", "No orders of ours on line 1 this shift; it is kept for DEMO-0001.");
    }

    // ---------------------------------------------------------------- current shift

    private void PlanCurrentShift()
    {
        var curSupervisor = _cur.Supervisor.User;
        var curOperator = _cur.Operator.User;
        At(C(0, 3), $"{_cur.ShiftName} crew checks in", async () =>
        {
            await commands.SendAsync(curSupervisor, new CheckInCommand(_ext02, [_id["person:" + _cur.Operator.Name], _id["person:" + _cur.Floor[0]]], null));
            await commands.SendAsync(curSupervisor, new CheckInCommand(_pack1, [_id["person:" + _cur.Floor[1]]], null));
            await commands.SendAsync(curSupervisor, new CheckInCommand(_ext01, [_id["person:" + _cur.Floor[2]]], null));
        });

        At(C(0, 40), $"the {_prev.ShiftName} shift's labor approved and one session corrected", async () =>
        {
            var session = await db.OneAsync<SessionRow>(
                "SELECT id, production_date AS ProductionDate, started_at AS StartedAt FROM core.labor_session WHERE person_id = @person AND equipment_id = @line ORDER BY started_at LIMIT 1",
                new { person = _id["person:" + _prev.Floor[0]], line = _ext02 }) ?? throw new InvalidOperationException("A session to correct is missing.");
            await commands.SendAsync(curSupervisor, new ApproveShiftCommand(new ShiftKey(_site, session.ProductionDate, _scheduleId, _prev.Shift),
                "Checked against the line logs"));
            await commands.SendAsync(curSupervisor, new CorrectLaborSessionCommand(session.Id, false, _ext02, null, "direct", null,
                Utc(session.StartedAt), P(7, 40), 100, $"{_prev.Floor[0]} left 17 minutes early for a medical appointment"));
        });

        At(C(0, 15), "resin split to line 1's hopper", async () =>
        {
            var lot = _id["lot:PVC-24-1188"];
            await commands.SendAsync(curOperator, new MoveLotCommand(lot, await db.VersionAsync("core.lot", lot), _hopper, 1500, _kg, "Top-up for line 1"));
        });

        // Line 2: the rest of gray, then white again.
        Report(C(0, 45), curOperator, "HIST-1002", P(7, 50), "SURFACE", 0.01m, null);
        Report(C(1, 45), curOperator, "HIST-1002", C(0, 45), "DIMENSION", 0.012m, null);
        Report(C(2, 50), curOperator, "HIST-1002", C(1, 45), "SURFACE", 0.01m, null);
        At(C(2, 54), "HIST-1002 complete on line 2", () => CompleteAsync(curOperator, "HIST-1002", "EXT-02"));
        Changeover(C(2, 56), C(3, 30), curOperator, "HIST-1003");
        Consume(C(3, 35), curOperator, "HIST-1003", "EXT-02", "PVC-24-1188", 1400);
        Consume(C(3, 35), curOperator, "HIST-1003", "EXT-02", "TIO-0932", 50);
        Report(C(4, 30), curOperator, "HIST-1003", C(3, 30), "CHANGEOVER", 0.07m, "Purge back to white");
        Report(C(5, 40), curOperator, "HIST-1003", C(4, 30), "DIMENSION", 0.012m, null);
        Report(C(6, 40), curOperator, "HIST-1003", C(5, 40), "DIMENSION", 0.01m, null);
        Report(C(7, 40), curOperator, "HIST-1003", C(6, 40), "SURFACE", 0.008m, null);

        // The packer.
        At(C(0, 30), "HIST-1002 starts on the packer", () => StartAsync(curSupervisor, "HIST-1002", "PACK1"));
        Pack(C(1, 20), curSupervisor, "HIST-1002", reverse: (C(1, 30), "Bundles were counted twice: the operator reported them again from the floor sheet"));
        Pack(C(2, 30), curSupervisor, "HIST-1002");
        Pack(C(3, 40), curSupervisor, "HIST-1002");
        At(C(3, 50), "HIST-1002 complete on the packer", () => CompleteAsync(curSupervisor, "HIST-1002", "PACK1"));
        // The packer follows line 2 by the process's 30 minutes at least, however squeezed the shift is.
        At(C(4, 30), "HIST-1003 starts on the packer", () => StartAsync(curSupervisor, "HIST-1003", "PACK1"));
        Pack(C(5, 50), curSupervisor, "HIST-1003");
        Pack(C(7, 0), curSupervisor, "HIST-1003");

        // Orders: the night's white order closed, the next ones made.
        At(C(1, 0), "work orders for the day", async () =>
        {
            var closed = await db.IdAsync("SELECT id FROM core.work_order WHERE order_number = 'HIST-1001'");
            await commands.SendAsync(Planner, new ChangeWorkOrderStatusCommand(closed, await db.VersionAsync("core.work_order", closed), "closed", "Packed and counted"));
            await OrderAsync(Planner, "HIST-1003", _white, 5000, C(3, 30), "White 4410 for Harbor Windows, release 2", dispatch: true);
            await OrderAsync(Planner, "HIST-1004", _gray, 6000, window.CurrentStart.AddHours(16), "Gray 4410 for Midway Supply", dispatch: true);
            await OrderAsync(Planner, "HIST-1006", _white, 8000, window.CurrentStart.AddHours(40), "White 4410 forecast, not yet confirmed", dispatch: false);
            var canceled = await OrderAsync(Planner, "HIST-1005", _gray, 2000, window.CurrentStart.AddHours(24), "Gray 4410 sample run", dispatch: false);
            foreach (var job in await db.ListAsync<Guid>("SELECT id FROM core.job WHERE work_order_id = @canceled ORDER BY job_number", new { canceled }))
            {
                await commands.SendAsync(Planner, new Pulaski.Application.Scheduling.CancelJobCommand(job, await db.VersionAsync("core.job", job), "Not needed", Replace: false));
            }

            await commands.SendAsync(Planner, new ChangeWorkOrderStatusCommand(canceled, await db.VersionAsync("core.work_order", canceled), "cancelled", "The customer took the sample from stock"));
        });

        At(C(1, 5), "packed profile shipped", async () =>
        {
            var lot = await db.IdAsync("""
                SELECT l.id FROM core.lot l JOIN core.job j ON j.id = l.produced_by_job_id JOIN core.work_order w ON w.id = j.work_order_id
                WHERE w.order_number = 'HIST-1001' AND j.equipment_id = @pack AND l.status = 'available' AND l.quantity > 0 ORDER BY l.lot_number LIMIT 1
                """, new { pack = _pack1 });
            await commands.SendAsync(curSupervisor, new ShipLotCommand(lot, await db.VersionAsync("core.lot", lot), null, null, "SHP-58812", "Harbor Windows, truck 2"));
        });

        At(C(1, 15), "the held lot that measured well released", async () =>
        {
            foreach (var lot in _line2Lots.Where(l => l.Suspect).Skip(1))
            {
                await commands.SendAsync(Quality, new ChangeLotStatusCommand(lot.Id, await db.VersionAsync("core.lot", lot.Id), "available", null, "Measured 42.06 mm: within the specification"));
            }
        });

        // Stops.
        At(C(3, 35), "the change back to white coded", () => CodeStopAsync(curOperator, _ext02, C(2, 55), "SETUP", "Changeover gray to white, die land cleaned"));
        At(C(2, 0), "the fault on line 2 commented", () => CodeStopAsync(Maintenance, _ext02, C(1, 40), "FAULT", "Screen changer hydraulic fault; reset"));

        // Andon: answered, one being worked on, and two waiting.
        Andon(curOperator, "EXT-02", "MATERIAL", "TiO2 masterbatch running low at the line", C(0, 20), (Planner, C(0, 26)), C(0, 38), "Bag brought from stores");
        Andon(curOperator, "EXT-02", "MAINT", "Screen changer fault", C(1, 40), (Maintenance, C(1, 46)), C(2, 10), "Hydraulic valve reset; screen changed");
        // And, whenever the history runs, calls still being answered and waiting: raised in the last half hour.
        var late = window.Now - HistoryWindow.Margin;
        Andon(curSupervisor, "PACK1", "MATERIAL", "Stretch film down to the last roll", Later(C(0, 5), late - TimeSpan.FromMinutes(24)),
            (Planner, Later(C(0, 7), late - TimeSpan.FromMinutes(17))), null, null);
        Andon(curSupervisor, "PACK1", "QUALITY", "Label print faint on the last two bundles", Later(C(0, 8), late - TimeSpan.FromMinutes(20)), null, null, null);
        Andon(curOperator, "EXT-01", "SUPERVISOR", "Need a decision on line 1's next order", Later(C(0, 9), late - TimeSpan.FromMinutes(6)), null, null, null);

        // Quality: the width back on target, a melt temperature spike early in the shift (its alert left open for the quality team).
        foreach (var (at, job) in Every(C(0, 10), C(2, 40), "HIST-1002").Concat(Every(C(4, 0), C(7, 50), "HIST-1003")))
        {
            Sample(at, curOperator, job, "EXT-02", "PROFILE-WIDTH", 42.00m, 0.035m, 2);
            var spike = at >= C(0, 45) && at < C(1, 15) ? 4.6m : 0m;
            Sample(at.AddMinutes(1), curOperator, job, "EXT-02", "MELT-TEMP", 215m + spike, 0.8m, 1);
        }

        // Labor.
        // One of the crew leaves line 1 for an hour's training on the packer, then goes back.
        At(C(1, 0), "training on the packer", async () =>
        {
            var person = _id["person:" + _cur.Floor[2]];
            await ClockOffAsync(curSupervisor, person, "direct");
            await commands.SendAsync(curSupervisor, new ClockOnCommand(person, new LaborInput(_pack1, null, "training", null, 100, null,
                $"Film change and sealer reset with {_cur.Floor[1]}")));
        });
        At(C(2, 0), "training ends", async () =>
        {
            var person = _id["person:" + _cur.Floor[2]];
            await ClockOffAsync(curSupervisor, person, "training");
            await commands.SendAsync(curSupervisor, new ClockOnCommand(person, new LaborInput(_ext01, null, "direct", null, 100, null, null)));
        });
        Break(C(4, 0), C(4, 30), _cur.Operator.Name, false, curSupervisor);
        Break(C(4, 30), C(5, 0), _cur.Floor[0], false, curSupervisor);
        Break(C(4, 0), C(4, 30), _cur.Floor[1], false, curSupervisor);
        Break(C(4, 10), C(4, 40), _cur.Floor[2], false, curSupervisor);

        // Handover.
        At(C(0, 30), "film delivered: the item closed", () => CloseNoteAsync(curSupervisor, "film", "Two pallets of film delivered to the packer"));
        At(C(3, 40), "die cleaned: the item closed", () => CloseNoteAsync(curOperator, "die", "Die land cleaned at the change back to white"));
        Note(C(0, 35), curOperator, "EXT-02", "note", "Took over gray HIST-1002 running at rate. Melt temperature jumped after the start of shift; quality told.");
        Note(C(1, 30), curSupervisor, "EXT-01", "open_item", "DEMO-0001 runs to Thursday; decide whether HIST-1006 goes on line 1 after it.");
    }

    // ---------------------------------------------------------------- steps

    private async Task<Guid> OrderAsync(string actor, string number, Guid material, decimal qty, DateTimeOffset start, string notes, bool dispatch)
    {
        var order = await commands.SendAsync(actor, new CreateWorkOrderCommand(_id["process"], number, material, qty, _meter, start, start.AddDays(3), 0, notes));
        if (dispatch)
        {
            await commands.SendAsync(actor, new Pulaski.Application.Scheduling.DispatchWorkOrderCommand(order.Id, await db.VersionAsync("core.work_order", order.Id), "Released to the floor"));
        }

        return order.Id;
    }

    private Task<Guid> JobAsync(string order, string line) => db.IdAsync("""
        SELECT j.id FROM core.job j JOIN core.work_order w ON w.id = j.work_order_id JOIN core.equipment e ON e.id = j.equipment_id
        WHERE w.order_number = @order AND e.code = @line
        """, new { order, line });

    private async Task<long> JobVersionAsync(string order, string line) => await db.VersionAsync("core.job", await JobAsync(order, line));

    // The packer only bundles, so it skips the changeover rules; line 2's changeovers run as their own jobs (see Changeover).
    private async Task StartAsync(string actor, string order, string line) =>
        await commands.SendAsync(actor, new StartJobCommand(await JobAsync(order, line), await JobVersionAsync(order, line), SkipChangeover: line == "PACK1"));

    /// <summary>Line 2 changes material: starting the order runs its changeover job first; when that is done, the order itself starts.</summary>
    private void Changeover(DateTimeOffset from, DateTimeOffset to, string actor, string order)
    {
        At(from, $"changeover to {order} on line 2", () => StartAsync(actor, order, "EXT-02"));
        At(to, $"{order} starts on line 2 after its changeover", async () =>
        {
            var changeover = await db.IdAsync("SELECT id FROM core.job WHERE job_number = @number", new { number = $"{order}-10-CO" });
            await commands.SendAsync(actor, new CompleteJobCommand(changeover, await db.VersionAsync("core.job", changeover)));
            await StartAsync(actor, order, "EXT-02");
        });
    }

    private async Task CompleteAsync(string actor, string order, string line) =>
        await commands.SendAsync(actor, new CompleteJobCommand(await JobAsync(order, line), await JobVersionAsync(order, line)));

    private void Consume(DateTimeOffset at, string actor, string order, string line, string lotNumber, decimal qty) =>
        At(at, $"{lotNumber} into {order}", async () =>
            await commands.SendAsync(actor, new ConsumeLotCommand(_id["lot:" + lotNumber], await JobAsync(order, line), qty, _kg)));

    /// <summary>Line 2's output since the last report, from its counter: good to staging as a new lot, the given share as scrap.</summary>
    private void Report(DateTimeOffset at, string actor, string order, DateTimeOffset since, string scrapReason, decimal scrapShare, string? comment,
        bool suspect = false, (DateTimeOffset At, string Why)? correctTo = null)
    {
        At(at, $"{order} reported on line 2", async () =>
        {
            var made = _line2.Counted(since, at);
            var scrap = Math.Round(made * scrapShare);
            var number = NextLot("E2");
            var report = await commands.SendAsync(actor, new ReportProductionCommand(await JobAsync(order, "EXT-02"), null, made - scrap, scrap, _meter,
                await db.ReasonAsync("scrap", scrapReason), _fgStage, number, null, comment));
            _line2Lots.Add((number, report.Lots.Single().LotId, at, suspect));
            if (correctTo is { } correction)
            {
                At(correction.At, $"{order}'s report corrected", async () =>
                    await commands.SendAsync(actor, new CorrectProductionReportCommand(await JobAsync(order, "EXT-02"), report.ReportId, made - scrap - 40, scrap + 40,
                        await db.ReasonAsync("scrap", "SURFACE"), await db.ReasonAsync("inventory_adjustment", "COUNT"), correction.Why)));
            }
        });
    }

    /// <summary>The packer takes line 2's lots made at least 40 minutes ago that are available, and reports what it bundled.</summary>
    private void Pack(DateTimeOffset at, string actor, string order, (DateTimeOffset At, string Why)? reverse = null)
    {
        At(at, $"{order} packed", async () =>
        {
            var job = await JobAsync(order, "PACK1");

            // No more than the packer could bundle in its run so far, at 90% of its 2,000 m/h.
            var run = await db.OneAsync<RunRow>("SELECT started_at AS StartedAt, good_qty + scrap_qty AS Reported FROM core.job_run WHERE job_id = @job AND ended_at IS NULL", new { job })
                ?? throw new InvalidOperationException($"{order} isn't running on the packer.");
            var room = Math.Floor((decimal)(at - Utc(run.StartedAt)).TotalHours * 2000m * 0.9m - run.Reported);
            var taken = 0m;
            foreach (var lot in _line2Lots.Where(l => l.At <= at.AddMinutes(-40)))
            {
                var row = await db.OneAsync<LotRow>("SELECT status, quantity FROM core.lot WHERE id = @id", new { id = lot.Id });
                var take = Math.Min(row?.Quantity ?? 0, room - taken);
                if (row is not { Status: "available" } || take <= 0)
                {
                    continue;
                }

                await commands.SendAsync(actor, new ConsumeLotCommand(lot.Id, job, take, _meter, "Bundled"));
                taken += take;
            }

            if (taken == 0)
            {
                return;
            }

            var bundled = Math.Floor(taken / 6m) * 6m;
            var report = await commands.SendAsync(actor, new ReportProductionCommand(job, null, bundled, taken - bundled, _meter,
                await db.ReasonAsync("scrap", "OTHER"), _fgStage, NextLot("P1"), null, "Offcuts short of a full bundle"));
            if (reverse is { } undo)
            {
                At(undo.At, $"{order}'s packer report reversed", async () =>
                    await commands.SendAsync(actor, new ReverseProductionReportCommand(job, report.ReportId, await db.ReasonAsync("inventory_adjustment", "COUNT"), undo.Why)));
            }
        });
    }

    private void Sample(DateTimeOffset at, string actor, string order, string line, string characteristic, decimal center, decimal spread, int decimals)
    {
        var value = Math.Round(center + (decimal)Gaussian() * spread, decimals);
        At(at, $"{characteristic} measured", async () =>
            await commands.SendAsync(actor, new RecordQualityResultCommand(_site, await Characteristic(characteristic), null, await JobAsync(order, line), at,
                [value], null, null, "manual", null)));
    }

    private void Andon(string actor, string line, string type, string note, DateTimeOffset raised, (string Who, DateTimeOffset At)? acknowledged,
        DateTimeOffset? resolved, string? resolution)
    {
        var key = $"{line}:{type}:{raised:O}";
        At(raised, $"andon {type} on {line}", async () =>
            _id["call:" + key] = (await commands.SendAsync(actor, new RaiseAndonCallCommand(await db.EquipmentAsync(line), _id["andon:" + type], note,
                line == "EXT-01" ? null : $"{line}-STATION"))).Id);
        if (acknowledged is { } ack)
        {
            At(ack.At, $"andon {type} on {line} acknowledged", async () => await commands.SendAsync(ack.Who, new AcknowledgeAndonCallCommand(_id["call:" + key])));
            if (resolved is { } done)
            {
                At(done, $"andon {type} on {line} resolved", async () => await commands.SendAsync(ack.Who, new ResolveAndonCallCommand(_id["call:" + key], resolution)));
            }
        }
    }

    private async Task ClockOffAsync(string actor, Guid person, string kind)
    {
        var session = await db.IdAsync("SELECT id FROM core.labor_session WHERE person_id = @person AND labor_type = @kind AND ended_at IS NULL",
            new { person, kind });
        await commands.SendAsync(actor, new ClockOffCommand(session, await db.VersionAsync("core.labor_session", session), null, false));
    }

    private void Break(DateTimeOffset from, DateTimeOffset to, string person, bool paid, string actor) =>
        At(to, $"{person}'s break", async () =>
            await commands.SendAsync(actor, new StartBreakCommand(_id["person:" + person], paid, from, to, paid ? "Paid rest break" : null)));

    private void Note(DateTimeOffset at, string actor, string line, string kind, string body, string? key = null) =>
        At(at, $"handover {kind} on {line}", async () =>
        {
            var id = (await commands.SendAsync(actor, new AddHandoverEntryCommand(await db.EquipmentAsync(line), kind, body))).Id;
            if (key is not null)
            {
                _id["note:" + key] = id;
            }
        });

    private async Task CloseNoteAsync(string actor, string key, string note) =>
        await commands.SendAsync(actor, new CloseHandoverItemCommand(_id["note:" + key], note));

    private async Task CodeStopAsync(string actor, Guid line, DateTimeOffset near, string reason, string comment)
    {
        var stop = await StopAsync(line, near);
        await commands.SendAsync(actor, new CodeDowntimeCommand(stop.Id, stop.StartedAt, await db.ReasonAsync("utilization", reason), comment));
    }

    private async Task<(Guid Id, DateTimeOffset StartedAt)> StopAsync(Guid line, DateTimeOffset near)
    {
        await FlushAsync();
        var row = await db.OneAsync<StopRow>("""
            SELECT id, started_at AS StartedAt FROM ts.downtime
            WHERE equipment_id = @line AND superseded_at IS NULL AND started_at BETWEEN @from AND @to ORDER BY started_at LIMIT 1
            """, new { line, from = near.AddMinutes(-3), to = near.AddMinutes(3) })
            ?? throw new InvalidOperationException($"No stop on line 2 near {near:u}.");
        return (row.Id, Utc(row.StartedAt));
    }

    private Task<Guid> Characteristic(string code) => db.IdAsync("SELECT id FROM core.quality_characteristic WHERE code = @code", new { code });

    private static IEnumerable<(DateTimeOffset At, string Order)> Every(DateTimeOffset from, DateTimeOffset to, string order)
    {
        for (var at = from; at <= to; at = at.AddMinutes(20))
        {
            yield return (at, order);
        }
    }

    private double Gaussian()
    {
        var u = 1 - _random.NextDouble();
        var v = _random.NextDouble();
        return Math.Clamp(Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v), -2.4, 2.4);
    }

    private static void Problems(string what, IEnumerable<string> problems)
    {
        var list = problems.ToList();
        if (list.Count > 0)
        {
            throw new InvalidOperationException($"The {what} can't be released: {string.Join("; ", list)}");
        }
    }

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record StopRow(Guid Id, DateTime StartedAt);

    private sealed record LotRow(string Status, decimal Quantity);

    private sealed record RunRow(DateTime StartedAt, decimal Reported);

    private sealed record SessionRow(Guid Id, DateOnly ProductionDate, DateTime StartedAt);
}
