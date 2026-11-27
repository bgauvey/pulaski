// What the training decks say. Modules are shared between roles; each role lists the modules its deck takes, in order.
// Every step and rule here comes from design/user_guide.md (and the default roles in Pulaski.Domain.Security.Roles):
// when a screen changes there, change it here too and rebuild (npm run build).

const modules = {};
const m = (id, title, subtitle, slides, notes) => { modules[id] = { title, subtitle, slides, notes }; };

// ===================== Shared basics =====================

m("basics", "Getting started", "Signing in, finding your way, and how Pulaski behaves", [
    {
        type: "steps", title: "Sign in",
        steps: [
            "Open Pulaski in a current Chrome, Edge, Firefox or Safari, at the address your administrator gives you.",
            "Type your **user name** and **password**. The first time, Pulaski asks you to choose your own password.",
            "If your organisation's sign-in is set up, click **Sign in with …** instead.",
            "You land on the **Overview**, or on the first screen your roles let you see (or the home page you chose).",
            "Click your name at the top right and **Sign out** when you leave a shared computer.",
        ],
        tips: [
            "Passwords are case-sensitive. **Five wrong tries lock you out for 15 minutes**; an administrator can reset your password, which unlocks you.",
            "A session lasts up to **12 hours**, then asks you to sign in again.",
            "**Forgotten your password?** links from the sign-in page when e-mail is set up.",
        ],
        notes: "A user who is switched off, or who has no role, can't sign in. Someone whose roles let them see nothing lands on 'Nothing to see here yet': ask an administrator for a role.",
    },
    {
        type: "cards", title: "Find your way around",
        cards: [
            { icon: "MdMenu", title: "The menu", text: "**Operate** holds the day-to-day pages; **Configure** the setup. You see only the pages your roles open. The ☰ button collapses it to icons." },
            { icon: "MdHelpOutline", title: "Help (?)", text: "Opens this guide's section for the screen you're on, beside it, and follows you as you move. **Open the whole guide** opens all of it." },
            { icon: "MdStarBorder", title: "Favourites (☆)", text: "Click the star to add the page you're on to **Favourites** at the top of the menu; up to 20, kept on any device." },
            { icon: "MdPerson", title: "Your menu", text: "Your name at the top right: **Preferences…** (language, region, colour theme, touch layout), **Make this page my home page**, **My alerts**, **Change your password**, **Sign out**." },
            { icon: "MdFilterList", title: "Filters are remembered", text: "List pages open with the filters you last used, on any device. A link that names its own filters wins." },
            { icon: "MdKeyboard", title: "With the keyboard", text: "The first Tab shows **Skip to content**. Tab and Shift+Tab reach everything; the control with focus is outlined." },
        ],
        notes: "Language and region are kept on your user, so they follow you to any computer. Theme and touch layout are kept on the device.",
    },
    {
        type: "cards", title: "How Pulaski behaves",
        cards: [
            { icon: "MdSync", title: "Screens update themselves", text: "Floor screens show changes elsewhere within seconds. Nothing reloads under you while you type in a form." },
            { icon: "MdSave", title: "How saving works", text: "Forms save when you press their button (**Create**, **Report**). Equipment and the process editor save as you go; the schedule acts on every drag." },
            { icon: "MdSchedule", title: "Site time", text: "Every time you see or type is in the **site's time zone**, not your computer's. Forms say which zone they use." },
            { icon: "MdPeople", title: "Changed by someone else", text: "If someone saved first, Pulaski refuses your change and says so. Nothing is lost: reload, look, and make your change again." },
            { icon: "MdErrorOutline", title: "Messages", text: "Red or yellow boxes say why something was refused, in plain words. Fix what it says and try again." },
            { icon: "MdBlock", title: "A greyed-out button", text: "Point at it, or Tab to it: a note says why it is off now (a lot on hold, a shift not ended). A button you may never use is left out." },
        ],
    },
    {
        type: "steps", title: "Your inbox",
        steps: [
            "Click **Inbox** at the top of the menu; its number says how many items wait for you.",
            "Items are grouped by what they are, oldest first, and each says where it is and how long it has waited.",
            "Click an item to open the page where it is decided, and decide it there. It leaves the inbox once decided.",
        ],
        tips: [
            "You see each kind only where your role decides it: partial batches, SPC alerts, labor, ERP changes, planning rates, maintenance requests, checklists, releases.",
            "**NCR actions to do** lists every open NCR action you own, whatever your role.",
            "The inbox updates by itself when something arrives, and every 30 seconds.",
        ],
        tipsIcon: "MdInbox",
    },
], "Everyone needs this part. For a group that has already been trained, skip to the role's own topics.");

m("alerts", "E-mails when something needs you", "My alerts", [
    {
        type: "steps", title: "Set up My alerts",
        steps: [
            "Click the warning-triangle button at the top right (**My alerts**).",
            "Under **Add an alert**, choose what to be told about (**Tell me when**), the **Site**, and **Where**: the whole site, or one area, line or stock location.",
            "Where it asks for **Minutes**, set them: how long a stop must last, how long it may stay without a reason, or how far ahead a shortage matters.",
            "Click **Add**. **Change** sets other minutes; **Switch off** stops an alert without losing it.",
        ],
        tips: [
            "Kinds: a long stop, a stop still without a reason, an andon call past its time, a control chart signal, a lot put on hold, a job short of a component.",
            "You're told only about what your roles let you see, and nothing is sent unless e-mail is set up for Pulaski.",
        ],
        tipsIcon: "MdNotificationsActive",
    },
]);

m("overview", "The Overview", "The plant at a glance: what runs, what doesn't, and what needs a person", [
    {
        type: "cards", title: "Read the Overview", pt: 15,
        cards: [
            { icon: "MdWarningAmber", title: "Needs attention", text: "Counts what needs a person, most urgent first: uncoded stops, open SPC alerts, orders behind, lots on hold, schedule conflicts, lines with no signal, labor to approve. Click a count to go to its list." },
            { icon: "MdInsights", title: "This shift", text: "OEE with availability, performance and quality; good output against the plan; scrap; downtime with its biggest reason. A dash means not measured; 0 means measured and none." },
            { icon: "MdViewTimeline", title: "Lines", text: "One row per line: its state now and why, the job now or next, a strip of the shift (green running, dark red down, hatched no signal), its OEE and any flags." },
            { icon: "MdTexture", title: "Hatching means no data", text: "A part with nothing to show yet is hatched and greyed, with a plug symbol saying what will fill it, so the page doesn't move when data arrives." },
        ],
        notes: "The Overview updates by itself within seconds of a change, and every 30 seconds. The pill at the top right says when it last updated, and turns grey with a warning if Pulaski can't reach the server for over a minute. With several sites, pick one at the top.",
    },
]);

// ===================== Planning =====================

m("planningIdeas", "How planning works", "You decide where and in what order; Pulaski works out when", [
    {
        type: "compare", title: "Who owns what on the schedule",
        columns: [
            { title: "You own", icon: "MdPanTool", items: ["**The line** each job is on.", "**The sequence**: what runs before what.", "**Pins**: lock, hold until, finish by.", "**The order's status**: dispatch, un-dispatch, firm."] },
            { title: "Pulaski owns", icon: "MdCalculate", color: "accent4", items: ["**The times**: when each job starts and ends, from its quantity, the line's rate, the shift calendar and changeovers.", "This **projection** is worked out again every time the schedule is read.", "Pulaski **never moves a job** by itself and **never picks a line** for you."] },
        ],
        notes: "You never drag a job to a time. You drag it to a place in a line's sequence, and the times follow.",
    },
    {
        type: "flow", title: "A work order's life",
        stages: [
            { label: "Planned", text: "You are still arranging it. Its jobs move freely; new jobs can be added." },
            { label: "Dispatched", text: "Published to the floor. Each job's projected times are saved as the **baseline**." },
            { label: "Firmed", text: "Frozen. Firmed jobs keep their start time; no un-dispatch." },
            { label: "In progress", text: "Its first job started (starting firms a dispatched order)." },
            { label: "Completed", text: "Its last open job completed. It follows its jobs." },
            { label: "Closed", text: "Closed on the Work orders screen; its completed jobs close with it." },
        ],
        note: "An order can be **cancelled** at most stages: cancel its open jobs first, then the order. **Un-dispatch** returns a dispatched order to planned and clears the baseline.",
    },
    {
        type: "cards", title: "Words you'll meet",
        cards: [
            { icon: "MdFactory", title: "Line and department", text: "A **line** runs jobs and can be planned to. A **department** (an area) can be planned to but doesn't run jobs: the floor picks its line." },
            { icon: "MdBolt", title: "Capability", text: "Says a material can run on a line, and how fast (its **ideal rate**). Pulaski never puts a job on a line without one." },
            { icon: "MdAltRoute", title: "Process", text: "A released route of numbered operations, each on lines or departments with shares totalling 100%. Every order is made from one." },
            { icon: "MdListAlt", title: "BOM", text: "What one material is made from. An order uses the released BOM to plan each job's components." },
            { icon: "MdWork", title: "Order, job, run", text: "A **work order** asks for a quantity; a **job** is its piece on one line; a **run** is the job actually running." },
            { icon: "MdSpeed", title: "Planning rate", text: "The rate the schedule uses: calculated (from the ideal rate or run history) unless someone set a **modified** rate." },
        ],
    },
]);

m("createOrder", "Create a work order", "What, how much and when; the process decides where", [
    {
        type: "steps", title: "Create a work order",
        steps: [
            "On the **Schedule**, click **New work order**. Choose the **Site** and type the **Order number** (unique within the site).",
            "Choose the **Material produced**, then the **Process**: the material's default is picked; a greyed-out one says why it can't run it.",
            "Type the **Planned quantity** in any unit that converts. For a batch material it must be whole batches; the hint shows the size.",
            "Set **Start** (no job is planned before it) and **Due**. A process that uses a department needs a due date.",
            "Set **Priority** and **Notes** if needed, and check the preview: the **jobs** it will create and the BOM that plans the **components**.",
            "Click **Create**. The Schedule opens with the new order in the side panel.",
        ],
        tips: [
            "Each job goes **at the end of its line**. Move it on the schedule if it should run sooner.",
            "Jobs are numbered from the order: **WO-1001-30**, or **WO-1001-10-1** and **-2** for an operation on two lines.",
            "Without a released BOM, the order plans no components; the preview warns you.",
        ],
        notes: "The order is created as planned. Jobs are held until the start time. A job on a department waits there in a window from its start to the due date, until the floor assigns it to a line.",
    },
    {
        type: "table", title: "If the order is refused",
        head: ["The message says…", "What to do"],
        italicFirst: true,
        rows: [
            ["…is not set up to run…; add a capability for it first", "Add a capability for the material on that line (the material's page), or pick another process."],
            ["…is a department, so its job needs a finish-by", "Enter a **Due** date."],
            ["…doesn't make…", "Add the material to the process's scope, or pick another process."],
            ["…doesn't convert to…, the base unit of…", "Enter the quantity in another unit, or add a conversion for the material on **Units**."],
            ["…was changed by someone else / was released again", "Try again: the process changed while you were filling in the form."],
            ["Duplicate order number", "Use another order number."],
        ],
        notes: "Everything is checked before anything is created, and every problem is listed together. Nothing is created until they are fixed.",
    },
]);

m("workOrders", "The Work orders screen", "How every order stands, and acting on one or many", [
    {
        type: "steps", title: "Find and act on orders",
        steps: [
            "Open **Operate → Work orders**. Use the **equipment picker** (top right) for the plant, a site, a department or a line.",
            "Click a tile (**Starting today**, **Behind**, **Need attention**, **Not on a line**…) to show only those orders; click again to clear.",
            "Search by **Order**, **Material** or **Line or department**, and choose a **Status** (open orders by default).",
            "Click **ⓘ** on a row for its details: due date, baseline, process and BOM versions, jobs, warnings and planned components.",
            "Tick one or more rows (or **Select visible**) and use **Actions**: the status changes allowed, **Print shop order**, **Show on the schedule**.",
        ],
        tips: [
            "A red flag marks an order **behind**; hover for why (*Projected to finish 3h after its due date*).",
            "With several selected, Actions says how many each change applies to (*3 of 5*); the rest are skipped.",
            "**Cancel…** asks you to confirm.",
        ],
    },
    {
        type: "steps", title: "Print a shop order",
        steps: [
            "Find the work order and open its **Actions** menu.",
            "Choose **Print shop order**. Pulaski downloads one PDF with a traveller sheet for each job.",
            "Print it for the floor. Check the schedule before running.",
        ],
        tips: [
            "Each sheet has the job's material, quantity, line, time, rate, due date, instructions and planned components.",
            "Its Code 128 barcode holds **order|OP-number** (WO-1001|OP-10): scanning it at a station offers to start the job.",
            "It lists the material's released specification, or says there is none.",
        ],
        tipsIcon: "MdPrint",
    },
]);

m("erpOrders", "Orders from the ERP", "Held orders, changes to accept or reject, and B2MML files", [
    {
        type: "cards", title: "When the ERP sends orders",
        cards: [
            { icon: "MdCallReceived", title: "Made like yours", text: "An ERP order is made from its process with a job per operation. Its row says **ERP** under Origin. Sending it again never makes a second." },
            { icon: "MdPauseCircleOutline", title: "Not whole batches", text: "Follows the material's or site's rule: refuse, round up or down, or import it **held**. A held order can't be dispatched: open it, type a whole-batch quantity and click **Set quantity**." },
            { icon: "MdChangeCircle", title: "The ERP changes it", text: "While planned, the change applies and its jobs are re-planned. Once dispatched the row shows **ERP change**: open it and **Accept**, or give a reason and **Reject**." },
            { icon: "MdCancel", title: "The ERP cancels it", text: "While planned, it is cancelled with its jobs. After that the cancellation waits for you like any change." },
        ],
        notes: "The ERP owns product, quantity, start, due date and priority. Pulaski owns the schedule, dispatch and status. Every step is recorded: imported, changed, change pending, accepted and rejected with your reason.",
    },
    {
        type: "steps", title: "Import orders from a B2MML file",
        steps: [
            "On **Operate → Work orders**, click **Import from the ERP**.",
            "Type the **External system** the orders come from, such as SAP-PRD, so the same file again doesn't make new orders.",
            "If the file doesn't name its site, choose the **Site**.",
            "Pick the **File**. Pulaski lists each order and what importing would do, or why it is refused. Nothing is saved yet.",
            "Click **Import**. Each order goes in on its own: a refused one is left out with its reason.",
        ],
        tips: [
            "**Download a template** in the same box gives a file to start from.",
            "Which process: the one the ERP names, else the material's default, else the only one that makes it.",
        ],
        tipsIcon: "MdUploadFile",
    },
]);

m("schedule", "The Schedule", "Four views of one plan; drag to change the sequence", [
    {
        type: "cards", title: "Four views of the same plan",
        cards: [
            { icon: "MdViewTimeline", title: "Gantt", text: "Lines down the left, time across. Drag a bar to resequence it or move it to another line; drag from **To place** to put an order on a line." },
            { icon: "MdCalendarMonth", title: "Calendar", text: "A month by day. Drag a planned job to a later day to **hold it until** then. Dragging earlier only clears a hold." },
            { icon: "MdViewKanban", title: "Kanban", text: "Orders as cards by status. Drag a card to **Dispatch**, **Un-dispatch** or **Firm** it; columns it can't go to dim." },
            { icon: "MdTableRows", title: "Table", text: "Every open job, sortable. Edit **Lock**, **Hold until** and **Finish by** in place; warnings show as chips." },
        ],
        notes: "The board re-reads everything every 10 seconds; cards that changed are briefly highlighted, so you notice other planners' changes. Updates pause while you drag. The schedule only shows equipment marked Schedulable.",
    },
    {
        type: "steps", title: "Move a job, and pin it",
        steps: [
            "Drag its bar on the **Gantt**: a marker shows the slot it goes into. Drop it on another line to move it there; times recalculate.",
            "Or click it to open the **side panel**: pick a **Line**, where it **Runs** (*Before* a job, or *At the end*) and click **Move**.",
            "Under pins, tick **Locked** to keep its place when others move.",
            "Set **Hold until** (it won't start before then, say for material) or **Finish by** (Pulaski warns if it runs past). Click **Set**.",
            "**Clear hold** and **Clear finish-by** remove them.",
        ],
        tips: [
            "A **firmed** job can change line only: it keeps its start time and slots in by time.",
            "While you drag an order from **To place**, each line previews where the job would land and which jobs it pushes later.",
            "A line that can't take a job says why.",
        ],
        tipsIcon: "MdDragIndicator",
    },
    {
        type: "steps", title: "Cancel and replace a job",
        steps: [
            "Open the job's side panel and click **Cancel job…**.",
            "Give a **reason**.",
            "For a production job, optionally tick **Replace it with a job for the quantity not yet made**.",
            "For a batch material, choose how the replacement is sized: **Round up** (the default), **Round down** (the order ends short) or **Exactly what is left**.",
            "Confirm, or **Keep it** to back out. The message says how much the order is short when it rounds down.",
        ],
        tips: [
            "Cancelling is the only schedule action that asks you to confirm.",
            "**Exactly what is left, even a partial batch** needs the planner's permission to plan partial batches.",
        ],
        tipsIcon: "MdCancel",
    },
]);

m("departments", "Departments, dispatch and links", "Plan to a department, publish the plan, and how operations wait for each other", [
    {
        type: "steps", title: "Plan a job to a department",
        steps: [
            "From the order's side panel, pick the department, set **Window opens** and **Finish by**, and click **Plan to …**. Or drag the order onto its row.",
            "The job waits as a band over its window. Each line that could run it shows a ghost estimate (*~14h here*).",
            "Pulaski warns, without blocking, if even the fastest line can't finish in the window or none can run it.",
            "The floor (or you) assigns it: **Run on …** in the side panel, or drag the band or a ghost onto the line.",
        ],
        tips: [
            "Use it for \"Extrusion, this weekend, done before Monday\": you set the window, the floor picks the line.",
            "A department job can move between its lines, or go back, until it starts.",
            "Pulaski checks each department job on its own; it doesn't share out capacity.",
        ],
        tipsIcon: "MdAccountTree",
    },
    {
        type: "cards", title: "Dispatch, firm, links and overlaps",
        cards: [
            { icon: "MdPublish", title: "Dispatch", text: "Publishes the order and saves every job's times as the **baseline**. Refused with no jobs, or a line that can no longer run the material." },
            { icon: "MdLock", title: "Firm", text: "Freezes the plan: no more un-dispatch, and each job is anchored at its baseline start." },
            { icon: "MdLink", title: "Process links", text: "A job waits for the operation it follows: **after start** or **after end**, plus lag. When the earlier one moves, the later one follows." },
            { icon: "MdLinkOff", title: "Link not met / cycle", text: "A firmed job its link can't allow, or jobs waiting for each other through a line's sequence. Resequence or move the earlier work." },
            { icon: "MdLayers", title: "Overlaps", text: "Only when work runs into a firmed job's fixed start. A red banner lists each pair with **Show**. Pulaski never fixes it: you move work or un-firm." },
            { icon: "MdPlaylistAdd", title: "New jobs", text: "Can only be added to a **planned** order." },
        ],
    },
]);

m("materialsPlanning", "Materials and rates", "Materials, where they can run, and the rates the schedule uses", [
    {
        type: "steps", title: "Create a material",
        steps: [
            "On **Configure → Materials**, click **New material**.",
            "Fill in **Code** (the item number, unique) and **Name**.",
            "Choose the **Type**, the **Class** and the **Base unit** stock is counted in.",
            "Optionally set a **Shelf life** in days, untick **Lot-tracked** for one balance per location, and add a **Description**.",
            "Click **Create and open**.",
        ],
        tips: [
            "Materials from the ERP arrive through the import: the ERP keeps their code, name, base unit and type.",
            "The base unit is fixed once the material is in use; lot tracking changes only with nothing on hand.",
        ],
        tipsIcon: "MdCategory",
    },
    {
        type: "cards", title: "Capabilities and planning rates",
        cards: [
            { icon: "MdBolt", title: "Where it can run", text: "On the material's page, **Where it can run** lists each line with its ideal rate and the **planning rate** the schedule uses (*calculated* or *modified*). Process engineers add lines there." },
            { icon: "MdEdit", title: "Modify a rate", text: "Set a **modified** planning rate with a reason when the line really runs differently; it beats the calculated one until you clear it." },
            { icon: "MdHistory", title: "Rates from run history", text: "Pulaski can work a rate out from past runs (good ÷ run hours, outliers left out, at least 5 runs and 8 h). You review it before it counts." },
            { icon: "MdFactCheck", title: "Review proposals", text: "**Configure → Planning rates** lists proposals. **Accept** makes it the calculated rate (checked again first); **Reject…** keeps the rate as it is." },
        ],
        notes: "Planning rates come from run standards. A modified rate always stays in force; accepting a proposal changes only the calculated rate. Proposals also appear in your Inbox.",
    },
]);

m("changeovers", "Changeover rules", "How long each change of material takes on a line", [
    {
        type: "steps", title: "Maintain changeover rules",
        steps: [
            "Open **Configure → Changeovers** and choose the site.",
            "Under **Add a rule**, choose the **Line** (or **All lines**), what it changes **From** and **To** (*Anything*, a material or a class) and the **Minutes**.",
            "Click **Add rule**. **Change** edits its time or note; **Switch off** stops using it.",
            "For many rules, **Import from a file** (CSV, checked before importing); **Export CSV** saves them for editing.",
        ],
        tips: [
            "The most specific rule wins: a material beats a class, which beats Anything; a rule for this line beats one for all lines.",
            "One rule per pattern. What a rule matches can't change: switch it off and add another.",
            "With no rule, the line's standard changeover on the capability is used.",
        ],
        tipsIcon: "MdSwapHoriz",
    },
]);

m("allocations", "Lots for upcoming jobs", "Allocations, shortages and picking lists", [
    {
        type: "steps", title: "Allocate lots to a job",
        steps: [
            "Open the job's inventory page (Inventory → **Jobs**, or a job on **Shortages**).",
            "Under **Allocations and suggested lots**, each component shows what it needs and what is **still to allocate**.",
            "Click **Allocate** on a suggestion, or **Allocate all suggested**. **Release** gives one back.",
            "**Refresh** puts the job's allocations right after stock or the schedule changed; **Refresh allocations** on Shortages does every open job.",
            "**Picking list** downloads a PDF of each lot to fetch, where it is, how much, for which job, nearest first.",
        ],
        tips: [
            "Suggestions: the lot that expires first first, then the site's preferences, then the oldest. Each says why.",
            "Allocating moves nothing: other jobs and Shortages treat the stock as taken.",
            "A lot put on hold or that would expire shows its allocation in amber until refreshed.",
        ],
        tipsIcon: "MdBookmarkAdded",
    },
    {
        type: "steps", title: "Check for shortages",
        steps: [
            "Open **Inventory → Shortages**: every component the site's open jobs still need, short ones first.",
            "Narrow it to a line or department, or to jobs needed in the next 24 hours, 3, 7 or 14 days.",
            "Choose the timing: **as the schedule has them now** (default) or **at their published times**.",
            "Click a component for each job that needs it: from stock, **incoming**, covered or short, and when it is needed.",
        ],
        tips: [
            "Each job takes available stock first, then deliveries due by the time it needs them. Held stock isn't counted.",
            "A **lead time** on the material flags jobs that supply asked for today couldn't reach.",
            "Not purchasing: nothing is ordered or rescheduled. Ordering stays in the ERP.",
        ],
        tipsIcon: "MdReportProblem",
    },
]);

// ===================== The floor =====================

m("floorIdeas", "The Floor", "The schedule says what should run; the floor records what does", [
    {
        type: "cards", title: "Ideas to know first",
        cards: [
            { icon: "MdPlayCircleOutline", title: "Runs", text: "Starting a job opens a **run**; pausing or completing ends it; resuming opens the next. Good, scrap and lots go into a run." },
            { icon: "MdLooksOne", title: "One run per line", text: "Pause or complete what is running before starting the next job on that line." },
            { icon: "MdEventAvailable", title: "When a job can start", text: "Its order must be dispatched or firmed, and its process links met. Pulaski never starts a job by itself; it says when one can." },
            { icon: "MdSpeed", title: "The rate check", text: "A run can't hold more than the line could make in it: length × ideal rate × the site's tolerance (110% unless changed)." },
            { icon: "MdSync", title: "The order follows", text: "The first job started puts the order **in progress**; the last one completed **completes** it." },
            { icon: "MdDashboard", title: "The board", text: "Each line is a card: the running job, good against plan, scrap, and how much more the run can hold. **Next** lists what follows, and why it can't start yet." },
        ],
        notes: "The board shows changes within seconds and refreshes every 15 seconds while no form is open. Departments with jobs waiting on them have a card of their own.",
    },
]);

m("runJobs", "Run a job", "Start, report, pause and complete", [
    {
        type: "steps", title: "Start a job",
        steps: [
            "On **Operate → Floor**, find the job under **Next** on its line's card.",
            "Click **Start** (or **Resume** for a paused job). It is active only when the job can start and the line is free.",
            "For a job waiting on a department, choose the **Line** it runs on, then click **Start**.",
            "If the material changes in automatic changeover mode, Pulaski starts a changeover job first (*WO-5002-10-CO*): complete it when the line is ready, then start the job.",
        ],
        tips: [
            "**Start** is refused until the job's **setup checklist** has passed on this line.",
            "Under **Next**, a job that can't start says why (*Waiting for operation 10 Extrude to end*) and when (*Ready at 14:30*).",
        ],
        tipsIcon: "MdPlayArrow",
    },
    {
        type: "steps", title: "Report production",
        steps: [
            "On the running line's card, click **Report**. The **Run** is the open run.",
            "Enter **Good** and **Scrap** in the job's unit (either can be zero).",
            "Check where good output **goes to**, and type a **Lot number** if you have one; left blank, Pulaski numbers it (*WO-5001-10-01*).",
            "Under **Made from**, optionally tick the lots this output came from.",
            "For scrap, choose a **Scrap reason**, then click **Report**. The card updates with the new totals.",
        ],
        tips: [
            "More than the run can hold is refused, and the message says how much more fits.",
            "Reporting waits until the **setup and first-article checklists** have passed on the line.",
            "With a label printer on the line, each lot's label prints once the lot is closed.",
        ],
        tipsIcon: "MdAddTask",
    },
    {
        type: "steps", title: "Report production from the machine",
        steps: [
            "Click **Report** on the line's card (or **Report production** on the job's page) and choose the **Run**.",
            "Click **From the machine**: Pulaski says what the line's counter counted since the last report from the machine.",
            "Click **Use these figures**. Good and scrap are filled in and marked *From the machine*. Change them if it miscounted.",
            "Choose the scrap reason and where good output goes, and click **Report**.",
        ],
        tips: [
            "**Never counted twice**: the next report from the machine starts after the counts this one took.",
            "Don't report the same output by hand as well.",
            "Batch materials are reported by batch weight, so they don't offer machine figures.",
        ],
        tipsIcon: "MdPrecisionManufacturing",
    },
    {
        type: "steps", title: "Pause, resume and complete",
        steps: [
            "**Pause** stops the job for now: choose why (*End of shift*, *Waiting for material*) or leave it blank, and click **Pause**. The line is free.",
            "**Resume** later opens a new run.",
            "**Complete** ends the job. Jobs waiting on it are named when they can now start.",
            "If it was the order's last open job, the order is completed.",
        ],
        tips: [
            "For a batch material, a batch cut short is reported only as the line stops: tick **A batch was in progress**, its quantity and where it goes.",
            "Each run can have one partial batch. Under the exact rule it goes on hold until approved.",
        ],
        tipsIcon: "MdPause",
    },
    {
        type: "steps", title: "Batches, checklists and instructions",
        steps: [
            "**Batch materials:** the report lists **Good batches**, each prefilled with the batch size. Change a weight if it differs; **Add batch** for each more.",
            "Rejected batches go under **Rejected batches** with a scrap reason. Each good batch becomes its own lot.",
            "**Work instructions** for the job show on its page and at the station: for its operation, its line and its material.",
            "**Checklists**: answer each question and click **Save the answers**. A *No* where *Yes* is expected, or a number outside its limits, fails it.",
        ],
        tips: [
            "Setup checklists are answered by whoever starts jobs; first article by whoever reports.",
            "Once per job and line: pausing and resuming needs neither again.",
            "A checklist may need a **second sign-off** by someone else before it counts.",
        ],
        tipsIcon: "MdChecklist",
    },
]);

m("floorTools", "Help, handover and touch", "Andon calls, handing over, maintenance requests and touch screens", [
    {
        type: "cards", title: "Calling for help and handing over",
        cards: [
            { icon: "MdCampaign", title: "Call for help (andon)", text: "At the line's station, badge in, tap **Call for help**, add a note, and tap the kind of help. The call turns red once nobody has come in its time." },
            { icon: "MdBuild", title: "Request maintenance", text: "For something to fix but not right now: **Request maintenance** (station, or Machines → Maintenance), say what needs looking at, and the priority. You get a number: MR-000042." },
            { icon: "MdSwapCalls", title: "Hand over", text: "**Operate → Handover**: under **Hand on to the next shift**, **Add a note**, or **Add an open item** someone must see to. **Close** an item when it's dealt with." },
            { icon: "MdTouchApp", title: "Touch screens", text: "Tablets switch to the touch layout by themselves: bigger buttons, a number keypad, reasons as buttons. On a panel that acts like a mouse: **Preferences… → Touch layout: On**." },
        ],
        notes: "Calling again for help that is still waiting doesn't make a second call. Open handover items stay at the top of their line, shift after shift, until someone closes them; nothing is deleted.",
    },
]);

m("handover", "Handing over", "Notes and open items for the next shift", [
    {
        type: "steps", title: "Hand over to the next shift",
        steps: [
            "Open **Operate → Handover**. Each line shows its **open items**, then what was written **This shift** and in the **Last shift**.",
            "Under **Hand on to the next shift**, write what happened and click **Add a note**.",
            "Or write what someone must see to and click **Add an open item**.",
            "When an open item is dealt with, click **Close**, optionally say what was done, and click **Close it**.",
        ],
        tips: [
            "Open items stay at the top of their line, shift after shift, until someone closes them.",
            "The Overview and line boards show a line's open items.",
            "Nothing is deleted.",
        ],
        tipsIcon: "MdSwapCalls",
    },
]);

m("station", "The operator station", "One touch screen at the line, signed on with your badge", [
    {
        type: "steps", title: "Work at the operator station",
        steps: [
            "Hold your **badge** to the reader (or type its number and press Enter), and your **PIN** if asked.",
            "**Start** (or **Resume**) the line's next ready job; **Pause** or **Complete** the running one, tapping a reason.",
            "**Report**: tap **Report the machine's count** when the line counts, or type **Good** and **Scrap** and tap **Report these figures**.",
            "When a stop needs its reason (shown at the top), tap **Give the reason** and its reason.",
            "Use **Scan** for a lot, container or shop order: it offers the likely action first; tap **Confirm**.",
            "Tap **Sign off** when you leave. After 15 minutes with nothing done it signs you off itself.",
        ],
        tips: [
            "What you may do is what your Pulaski user may do at the site, recorded under your name with the station named.",
            "Instructions for the running job (or the next) show without badging in.",
            "Partial batches, batch materials and back-dating are done on the Floor for now.",
        ],
        tipsIcon: "MdTabletAndroid",
    },
    {
        type: "steps", title: "When the network drops",
        steps: [
            "A red bar across the top says **No connection to Pulaski: nothing is saved until it is back**. The screen stays as it was.",
            "Anything you tap to send isn't sent: the bar says so, and what you typed stays in the form.",
            "When the connection is back, the bar goes and the screen reloads by itself (a station waits until you finish typing).",
            "Send again what wasn't sent.",
        ],
        tips: [
            "Nothing is kept on the device to send later.",
            "Machine counts and states are kept by the edge node, so **From the machine** still offers them afterwards.",
            "If the browser shows its own error page mid-send, check the job, stop or clock before sending again: it may have arrived.",
        ],
        tipsIcon: "MdWifiOff",
    },
]);

m("labourOwn", "Your time", "Clocking on and off, and breaks", [
    {
        type: "steps", title: "Clock yourself on and off",
        steps: [
            "Open **Operate → Floor** and click **Clock on** on the line you're working on.",
            "Click **Start break** when you go on a break, and **End break** when you're back. One break covers all your lines.",
            "Click **Clock off** when you leave the line. A break you're still on ends with it.",
            "**At a badge terminal:** badge in, then **Clock on** and a line, **Clock off**, or a break. For setup, indirect or training time, press that kind first, then the line.",
        ],
        tips: [
            "Pulaski refuses if you're already on another line with all your time: clock off there first.",
            "If the line needs a qualification you don't hold, a note says so; it never stops you.",
            "**Not me / cancel** on a terminal goes straight back.",
        ],
        tipsIcon: "MdBadge",
    },
]);

m("machinesView", "Machines and stops", "The status board, and giving stops their reason", [
    {
        type: "cards", title: "The status board",
        cards: [
            { icon: "MdCircle", color: "accent2", title: "Producing", text: "Green: the machine is executing." },
            { icon: "MdHourglassBottom", color: "accent3", title: "Waiting", text: "Starved or blocked: waiting on material upstream or space downstream." },
            { icon: "MdPanTool", color: "accent5", title: "Held", text: "Held by the machine or an operator." },
            { icon: "MdStopCircle", color: "accent6", title: "Stopped", text: "Down. The stop in progress shows its reason, or **uncoded**." },
            { icon: "MdPowerSettingsNew", color: "accent4", title: "Ready", text: "Idle, starting, resetting and the like." },
            { icon: "MdSignalWifiOff", color: "text1", title: "No signal", text: "Hatched: nothing heard. **… quiet** means its edge node hasn't been heard for 2 minutes." },
        ],
        notes: "Operate → Machines. Each tile shows the PackML state and for how long, the unit mode when it isn't Production, the stop in progress with its reason and stop code, the job running with its good count, and the speed. A line sits first in its row with its machines below. The board refreshes every 5 seconds.",
    },
    {
        type: "steps", title: "Code stops",
        steps: [
            "On **Operate → Machines**, open the **Stops** tab. **Only stops to code** shows stops with no reason, longer than a micro-stop.",
            "Choose the **Equipment** and **Period** (8 hours, 24 hours or 7 days).",
            "Click **Code** on a stop, choose the **Reason**, add a **Comment** if you like (some reasons need one), and click **Save**.",
            "Where the machine's stop code mapped a reason, **Confirm …** accepts it in one click.",
            "**Change** on a coded stop gives it another reason.",
        ],
        tips: [
            "Micro-stops (under the machine's threshold, 5 minutes by default) aren't availability loss; tick **Show micro-stops** to see them.",
            "**… stops to code** on a status tile opens that machine's stops.",
            "Uncoded down time shows as *uncoded* in the OEE report until someone codes it.",
        ],
        tipsIcon: "MdLabel",
    },
]);

m("inventoryBasics", "Inventory basics", "Finding stock, receiving, moving and consuming lots", [
    {
        type: "cards", title: "Ideas to know first",
        cards: [
            { icon: "MdInventory2", title: "Lots and balances", text: "A **lot** is one batch with its own number. Materials that aren't lot-tracked keep one **balance** per location (*Balance at RM-STORE*)." },
            { icon: "MdStraighten", title: "Base unit", text: "Quantities are kept in the material's base unit; enter any unit that converts." },
            { icon: "MdRemoveCircleOutline", title: "Never below zero", text: "Pulaski refuses to take out more than a lot holds and says how much is left." },
            { icon: "MdPanTool", title: "Statuses", text: "**Available** lots can be used. **On hold** and **quarantine** count as on hand but can't be consumed until released." },
            { icon: "MdEventBusy", title: "Expiry", text: "An **expired** lot is amber: it can't be consumed, shipped or merged, but can be moved, held, written off or counted." },
            { icon: "MdPlace", title: "Locations", text: "Some take one material at a time; in a silo or tank where lots mix, everything joins one balance." },
        ],
        notes: "Inventory has tabs: Stock, Lots, Locations, Jobs, Shortages, Journal, Counts and Reports, plus Scan, Containers, Feed points and Incoming. Pulaski remembers the site while you move between them.",
    },
    {
        type: "steps", title: "Find stock and receive a lot",
        steps: [
            "**Stock** lists every material by class: on hand, available, held, lots, locations and the next expiry. Type in **Find a material** or pick a location.",
            "**Lots** finds a lot by number, supplier lot, material, location, status or expiry; click one to open it.",
            "To receive, click **Receive**, choose the **material** and the **location** it goes to.",
            "For a lot-tracked material, enter the **lot number**, and the **supplier lot** and **expiry** if you have them.",
            "Enter the **quantity** and **unit**, and click **Receive**. A green bar links to the new lot.",
        ],
        tips: [
            "A lot number is unique for its material in the site.",
            "Receiving against an expected delivery (**Against expected receipt**) counts it toward that delivery.",
            "**No location holds inventory**: tick Holds inventory on a store on the Equipment page.",
        ],
        tipsIcon: "MdMoveToInbox",
    },
    {
        type: "steps", title: "Move and consume lots, or scan",
        steps: [
            "Open the lot's page. **Move** sends the whole lot to another location (for a balance, enter how much).",
            "**Consume** records that a job used some of it: choose the job (those planned to use it come first) and the quantity.",
            "Or on **Inventory → Jobs**, open the job and click **Consume a lot**: choose the material, lot, **Run** and quantity.",
            "**Inventory → Scan** with a scanner: scan a lot, then **Move** (scan where), **Consume…**, **Pack** or **Load**; a location offers **Receive here…**.",
        ],
        tips: [
            "Consumption needs a run: the job's open run, or an ended one for use found afterwards.",
            "Using more than the plan allows is recorded with a warning.",
            "A Pulaski label always matches exactly one thing; typed text may offer several to choose from.",
        ],
        tipsIcon: "MdQrCodeScanner",
    },
]);

m("inventoryIdeas", "Inventory", "Lots, balances, statuses and expiry", [modules.inventoryBasics.slides[0]]);

m("qualityFloor", "Quality at the line", "Recording results, samples due, and raising an NCR", [
    {
        type: "steps", title: "Record a quality result",
        steps: [
            "Open **Operate → Quality** (or **Quality results** on the job's page, which fills the job in).",
            "Under **Record a result**, find the **Lot** by scanning or typing its number, and click **Find**.",
            "Choose the **Characteristic**: those in the material's released specification come first, with their limits.",
            "Type the **Values** (several from one sample, separated by spaces), or how many were **Inspected** and **Defective**.",
            "Choose the **Source**, check **Sampled at**, and click **Record**. Pulaski says whether it is in specification.",
        ],
        tips: [
            "Out of specification on a characteristic that holds the lot: the lot goes on hold, and Pulaski says so.",
            "A floor card showing **1 sample due** (amber) or **overdue** (red) opens the job's **Samples**.",
            "If something is wrong, **Raise an NCR** from the lot's page or the result.",
        ],
        tipsIcon: "MdScience",
    },
]);

// ===================== Supervising =====================

function decisionSlides() {
    return [
    {
        type: "steps", title: "Partial batches and checklist sign-offs",
        steps: [
            "A partial batch waiting for approval is in your **Inbox**; the floor shows *A partial batch waits for approval*.",
            "On the job's page, under **Partial batches**: **Approve** releases the lot.",
            "**Reject** asks for a scrap reason, writes the lot off and moves its quantity from good to scrap.",
            "A checklist waiting for a **second sign-off**: read the answers on the job's page, then **Sign it off**, or type a **Note** and **Reject**.",
        ],
        tips: [
            "You can never decide a partial batch you reported, or sign off a checklist you answered.",
            "A rejected checklist needs answering again; the new answers wait for a sign-off in turn.",
            "Signing is a record of who did what, not an electronic signature.",
        ],
        tipsIcon: "MdApproval",
    },
    ];
}

m("decisions", "Partial batches and sign-offs", "Deciding on what the floor made", decisionSlides());

m("correctReports", "Correcting production reports", "Reverse a report whole, or correct it in one step", [
    {
        type: "steps", title: "Reverse or correct a production report",
        steps: [
            "Open the job's page and find the report under **Production reports**.",
            "**Reverse:** click **Reverse…**, choose a **Reason**, write a **Comment** and click **Reverse report**.",
            "**Correct:** click **Correct…**, change **Good**, **Scrap** or the scrap reason, give a reason and comment, and click **Correct report**.",
            "The old report is marked *reversed*; a correction puts a new one in the same run, at the same time.",
        ],
        tips: [
            "Nothing is deleted: the report's counts are offset and its lots give their quantity back.",
            "A report's lots must still hold what it gave them; Pulaski names what to reverse first.",
            "A correction makes new lots: print their labels again. Not after 31 days.",
        ],
        tipsIcon: "MdUndo",
    },
]);

m("supervisorJobs", "Putting production right", "Late reports, reversals, corrections, reopening and partial batches", [
    {
        type: "steps", title: "Report after the run, reverse or correct",
        steps: [
            "**After the run:** open the job's page, click **Report production**, and choose the **Run** (an ended one is *Recorded late*).",
            "**Reverse:** under **Production reports**, click **Reverse…**, choose a **Reason**, write a **Comment** and click **Reverse report**.",
            "**Correct:** click **Correct…**, change **Good**, **Scrap** or its reason, give a reason and comment, and click **Correct report**.",
            "**Reopen** a completed job whose order isn't closed: it goes back to paused.",
        ],
        tips: [
            "Nothing is deleted: a reversed report stays, marked *reversed*, and its lots give their quantity back.",
            "A report's lots must still hold what it gave them; Pulaski names what to reverse first.",
            "A correction makes new lots: print their labels again. Not after 31 days.",
        ],
        tipsIcon: "MdUndo",
    },
    ...decisionSlides(),
    {
        type: "steps", title: "Production by shift",
        steps: [
            "On **Operate → Floor**, click **Production by shift**.",
            "Choose **From** and **to** (production days, up to 93) and click **Show**.",
            "Each shift is a block with its crew, good and scrap per unit, then a row per line and job.",
            "**Against the jobs' runs** shows whether each job's reports all fall in these days (*Matches*).",
            "**Export CSV** saves every row, for the shift report or the morning meeting.",
        ],
        tips: [
            "A report counts in the shift running on its line when it was made.",
            "A night shift's output after midnight counts toward the day it began.",
            "Outside shifts, a report counts toward the site's calendar day.",
        ],
        tipsIcon: "MdTableChart",
    },
]);

m("supervisorLabour", "Labor", "Crews on lines, breaks, shift review and approval", [
    {
        type: "steps", title: "Check a crew onto a line",
        steps: [
            "Open **Operate → Labor**. **On now** shows each line with its shift, crew, job and who is on.",
            "Click **Check crew in** on the line. The crew on shift is ticked; people on another line are listed but not ticked.",
            "Untick anyone who isn't there, tick anyone else, and click **Check … in**.",
            "**Add someone** for other time: who, the kind of time, the reason (indirect) and their share, then **Clock on**.",
            "**End** ends someone's time; **Check everyone out** at the end of the shift.",
        ],
        tips: [
            "**Break** or **Paid break** next to someone; **End break** when they're back. Leaving their last line ends a break too.",
            "A missing qualification warns, never refuses.",
        ],
        tipsIcon: "MdGroups",
    },
    {
        type: "steps", title: "Review and approve a shift",
        steps: [
            "Open **Labor → Shift review** (or **Labor to approve** on the Overview) and choose the **Day**; the first shift waiting opens.",
            "Check people, times and hours. **Change** fixes a start, end, share, kind of time, job or reason; **End** someone still on.",
            "Add a missing break under **Breaks**: who, from, to, paid.",
            "Once the shift has ended, add a note if you like and click **Approve shift**. Anyone still on is clocked off.",
            "To fix an approved shift, **Correct** or **Remove** a row and say why; the original stays, struck through.",
        ],
        tips: [
            "Planned breaks are listed above the table, with anyone who worked through them.",
            "**Labor → Reports**: by job (efficiency against the standard crew), by person, by shift; **Export CSV** for payroll.",
        ],
        tipsIcon: "MdFactCheck",
    },
]);

m("labourApprove", "Approving labor", "Shift review, approval and labor reports", [
    {
        type: "steps", title: "Review and approve a shift",
        steps: [
            "Open **Labor → Shift review** (or **Labor to approve** on the Overview) and choose the **Day**; the first shift waiting opens.",
            "Check people, times and hours. **Change** fixes a start, end, share, kind of time, job or reason; **End** someone still on.",
            "Add a missing break under **Breaks**: who, from, to, paid.",
            "Once the shift has ended, add a note if you like and click **Approve shift**. Anyone still on is clocked off.",
            "To fix an approved shift, **Correct** or **Remove** a row and say why; the original stays, struck through.",
        ],
        tips: [
            "Planned breaks are listed above the table, with anyone who worked through them.",
            "**Labor → Reports**: by job (efficiency against the standard crew), by person, by shift; **Export CSV** for payroll.",
        ],
        tipsIcon: "MdFactCheck",
    },
]);

m("supervisorStops", "Putting stops right", "Entering, splitting and merging machine stops", [
    {
        type: "cards", title: "Enter, split, merge and more",
        cards: [
            { icon: "MdAddCircleOutline", title: "Enter a stop", text: "On **Stops**, **Enter a stop**: the machine, when it **Started** and **Ended**, the **Reason**, and **Why**. Not overlapping another, ended, at most 31 days back." },
            { icon: "MdCallSplit", title: "Split a stop", text: "A jam, then waiting for cartons: split it in two at a time, each part with its reason." },
            { icon: "MdCallMerge", title: "Merge stops", text: "One problem the machine reported as several stops: merge them into one." },
            { icon: "MdUndo", title: "Retime, void, undo", text: "Change a stop's times, void a stop entered by hand, or undo a split or merge whose stops are all current." },
        ],
        notes: "Nothing is deleted: a split or merged stop is kept as replaced, and the new stops take its place in the list and in utilization and OEE. Every change needs a reason in words. Closed stops only, up to 31 days back. Needs downtime.adjust (Supervisor).",
    },
]);

m("andonAnswer", "Answering calls", "Andon calls for help, and how fast they are answered", [
    {
        type: "steps", title: "Answer calls for help",
        steps: [
            "Open **Operate → Andon** (or badge in at the line's station). Each call shows its line, the help wanted, the note and how long it has waited.",
            "Click **I'm on it** (station: **I'm here**) when you go to a call.",
            "When it's done, click **Resolve**, say what was done, and click **Resolved**.",
            "At the foot of the page, choose **Last 7**, **30** or **90 days** for response times by line and kind of help, and the longest waits.",
        ],
        tips: [
            "A call turns red, **escalated**, once nobody has come within its type's time.",
            "**Resolve** works on a call nobody acknowledged: it counts as answered then.",
            "**Resolved in the last day** lists who resolved each call and what was done.",
        ],
        tipsIcon: "MdCampaign",
    },
]);

m("maintenanceWork", "Maintenance requests", "Working requests, and preventive plans by run hours or counts", [
    {
        type: "steps", title: "Work a maintenance request",
        steps: [
            "Open **Operate → Machines → Maintenance**: requests not closed, waiting ones and high priority first. They are in your **Inbox** too.",
            "Click **Start** when you begin: it shows **In progress** and who is on it.",
            "Click **Close**, say **What was done** (or why nothing was), and click **Close**.",
            "It moves to **Closed in the last 7 days**, with who closed it and how long it took.",
        ],
        tips: [
            "**Request maintenance** on a stop (Stops tab) asks for that stop's machine; one request per stop while it's open.",
            "Closing one nobody started starts it at the same moment.",
            "**Equipment** narrows the list to a line or machine; a line includes its machines' requests.",
        ],
        tipsIcon: "MdHandyman",
    },
    {
        type: "steps", title: "Plan preventive maintenance",
        steps: [
            "On the **Maintenance** tab, under **Preventive plans**, click **Add a plan**.",
            "Choose the **Line or machine** and say **What is done** (*Grease the gearbox*).",
            "Set **Every** so many **run hours** or **machine counts**, and the **Priority**. Click **Save**.",
            "**So far** shows how far its meter has got. When it reaches the interval, Pulaski raises a request marked **Preventive**.",
        ],
        tips: [
            "Run hours are time executing (PackML Execute); counts come from the machine's counter tag.",
            "While its request is open, the meter waits; it starts again when the request is closed.",
            "**Change** words, interval or priority, or switch a plan **Off**; plans are never deleted.",
        ],
        tipsIcon: "MdEventRepeat",
    },
]);

m("stockControl", "Stock control", "Holds, shipping, counts and the movement journal", [
    {
        type: "table", title: "What you can do on a lot's page",
        head: ["Button", "Does"],
        widths: [2.6, 13.333 - 1.2 - 2.6],
        rows: [
            ["**Move**", "Moves the whole lot to another location in the site (a balance: how much)."],
            ["**Hold…**", "Puts the lot on hold or in quarantine, with a hold reason. **Release** makes it available again."],
            ["**Ship**", "Sends stock out of the site, with a delivery or shipment number. At zero the lot is *shipped*."],
            ["**Split**", "Splits part of the lot into a new lot with the same material, status and expiry."],
            ["**Consume**", "Records that a job used some of the lot."],
            ["**Write up / Write off / Correct…**", "Fix quantities or details, with a reason (the Quality and Auditor roles)."],
            ["**Reverse** (history)", "Undoes a movement with a new one; nothing recorded changes."],
        ],
        notes: "Some reasons need a comment; the form says (required). A lot's page also shows its genealogy and its whole history, and As it was shows the lot at any past date and time.",
    },
    {
        type: "steps", title: "Count a location",
        steps: [
            "Open **Inventory → Counts**, choose the **location**, add a note (*month end*), and click **Start a count**.",
            "Count each lot and type what you find in **Counted** (0 for a lot that isn't there). **Difference** shows how far off it is.",
            "Click **Save counts** as you go; you can come back later.",
            "When every lot is counted, choose the **reason** (*Count correction*) and click **Post**. Lots that differ are written up or off.",
            "**Cancel count** drops it without changing anything.",
        ],
        tips: [
            "A lot that moved after you counted it is refused at posting, marked *changed since counted*: count it again.",
            "Stock you find that isn't in the count is received separately.",
            "**Inventory → Journal** lists every movement, newest first, filtered by material, lot, location or who.",
        ],
        tipsIcon: "MdChecklistRtl",
    },
]);

m("counts", "Stock counts", "Checking what is really at a location", [
    {
        type: "steps", title: "Count a location",
        steps: [
            "Open **Inventory → Counts**, choose the **location**, add a note (*month end*), and click **Start a count**.",
            "Count each lot and type what you find in **Counted** (0 for a lot that isn't there). **Difference** shows how far off it is.",
            "Click **Save counts** as you go; you can come back later.",
            "When every lot is counted, choose the **reason** (*Count correction*) and click **Post**. Lots that differ are written up or off.",
            "**Cancel count** drops it without changing anything.",
        ],
        tips: [
            "A lot that moved after you counted it is refused at posting, marked *changed since counted*: count it again.",
            "Stock you find that isn't in the count is received separately.",
            "**Inventory → Journal** lists every movement, newest first, filtered by material, lot, location or who.",
        ],
        tipsIcon: "MdChecklistRtl",
    },
]);

m("oeeReports", "Utilization and OEE", "Where the time went, and how lines did against their targets", [
    {
        type: "steps", title: "Read the OEE report",
        steps: [
            "Open **Operate → Machines → Utilization and OEE**. Choose the days (up to 366), optionally one **Equipment** and **Shift**, and click **Show**.",
            "**By equipment**: scheduled, planned, run and down hours, stops, and availability × performance × quality = **OEE**.",
            "**By shift** (or **By day** over 31 days) and **By product** show the same split other ways.",
            "**Stop Pareto** ranks down time by reason; **Where the time went** breaks one machine's time down.",
            "**Trend** charts one figure against its target. **Export CSV** saves each section.",
        ],
        tips: [
            "Output comes from production reports, never machine counts alone.",
            "**Uncoded** and **No signal** are listed like reasons: coding stops makes the Pareto useful.",
            "With targets set, each figure shows its target beneath it, in red when below.",
        ],
        tipsIcon: "MdQueryStats",
    },
]);

// ===================== Quality =====================

m("qualityLots", "Holding and rejecting lots", "Holds, releases and rejecting on quality", [
    {
        type: "steps", title: "Hold, release or reject a lot",
        steps: [
            "Open the lot's page. **Hold…** puts it **on hold** or in **quarantine** with a hold reason; **Change hold…** switches between them.",
            "**Release** makes a held lot available again.",
            "To reject a held lot: under **Quality**, click **Reject…**, choose the **Scrap reason** (*Rejected on quality*) and a comment.",
            "Click **Reject the lot**. What's left is written off as scrap; if made in a run, it moves from good to scrap there.",
        ],
        tips: [
            "A failing result on a characteristic that **holds the lot** holds an available lot by itself (reason OUT-OF-SPEC).",
            "Rejection reaches OEE quality, the job's totals and production by shift.",
            "**Leave out…** a wrong result with why; it stays, marked *Left out*, never edited.",
        ],
        tipsIcon: "MdGppMaybe",
    },
]);

m("correctInventory", "Correcting inventory", "Write-ups, write-offs, corrections and reversals", [
    {
        type: "steps", title: "Correct inventory and reverse movements",
        steps: [
            "**Write up** adds stock found or miscounted; **Write off** removes it (damage, loss). Both need an adjustment reason.",
            "**Correct…** fixes the lot number, supplier lot or expiry, with a comment saying why.",
            "**Split** part into a new lot; **Merge…** takes other lots of the same material, place and status into this one.",
            "**Reverse** a movement from the lot's **History**: read what it will do, give a **Reason** and **Comment**, click **Reverse**.",
            "To undo a movement with everything after it, tick **With everything after it that depends on it**, **Check**, then **Undo N movements**.",
        ],
        tips: [
            "If something since depends on it, Pulaski says what (*Since then: consumed into WO-1001-10*).",
            "**Back-date** a late movement with **Counts from**, up to 31 days, with a comment.",
            "Nothing is ever edited: the old movement stays, marked reversed, with who and why.",
        ],
        tipsIcon: "MdBuildCircle",
    },
]);

m("spc", "SPC: charts and alerts", "Control charts, control limits and responding to signals", [
    {
        type: "steps", title: "Respond to SPC alerts",
        steps: [
            "Open **Operate → Quality → SPC alerts** (or the Overview's **Open SPC alerts**).",
            "Open alerts come first: the chart (characteristic, material, line), the rules broken and when.",
            "Click **Respond**: each result that broke a rule is listed; **See the chart** opens it.",
            "Find the cause at the line. Choose the **Cause**, say **What was done**, and click **Close alert**.",
        ],
        tips: [
            "One alert per chart: further breaks are added to the open alert.",
            "A red diamond on the chart is beyond the limits; an amber square completes another run rule.",
            "In specification and in control are different: limits come from the results, not the specification.",
        ],
        tipsIcon: "MdShowChart",
    },
    {
        type: "steps", title: "Charts and control limits",
        steps: [
            "**Control charts**: choose the **Characteristic**, **Material**, a **Line** (or every line) and the period; click **Show**.",
            "Switch views above the chart: X̄, R and S for subgroups; Individuals and Moving range for single values.",
            "To set limits: on **Characteristics**, click **Control limits**, choose material, line and a period when the process ran normally, and **Work it out**.",
            "Review the limits the results give (at least 20 subgroups), then approve them; they are versioned.",
        ],
        tips: [
            "A line's own limits win over the material's.",
            "Capability (Cp, Cpk, Pp, Ppk) shows on the chart with a released specification.",
            "Process values can be sampled automatically from machine tags.",
        ],
        tipsIcon: "MdStackedLineChart",
    },
]);

m("ncr", "Nonconformance reports", "From what was found to closure", [
    {
        type: "flow", title: "An NCR from raised to closed",
        stages: [
            { label: "Raise", text: "**Raise an NCR** on a lot, an out-of-spec result, an SPC alert, a run or a failed checklist, or on its own. Say what was found and how severe." },
            { label: "Investigate", text: "Ask why up to five times, write the **Root cause**, then **Root cause found: act on it**." },
            { label: "Act", text: "Add corrective and preventive actions, each with an owner and a due day. The owner clicks **Finish** and **Mark done**." },
            { label: "Close", text: "Once every action is done or cancelled, choose **What becomes of the lots**, add a closing note and **Close NCR**." },
            { label: "Dispose", text: "The closed NCR lists its lots in stock with what to do to each: release, rework, scrap or return." },
        ],
        note: "NCRs are numbered NCR-000001. Raising one needs **ncr.raise** (Operator, Supervisor, Quality); working one needs **ncr.manage** (Quality). Owned and overdue actions appear in the Inbox.",
    },
]);

m("specs", "Specifications", "Characteristics, specifications and sampling plans", [
    {
        type: "steps", title: "Write a specification",
        steps: [
            "First add **characteristics** (Configure → Specifications → Characteristics): a code, name, **Measured** or **Inspected**, unit and decimals.",
            "Click **New specification**, choose the **Material** and click **Create and edit**.",
            "**Add characteristic** for each line: **Lower** and/or **Upper limit** and a **Target**, or **Most defects**. Tick **Holds the lot** where a failure should hold it.",
            "For sampling, tick **at the start of each run** and/or **every** so many hours or units, and the minutes until **overdue**.",
            "**Save draft**, then **Release version N** (or **Submit for approval…** where the site asks for it).",
        ],
        tips: [
            "A characteristic's code and kind never change, since results depend on them.",
            "To change a released specification, click **New draft**: it starts as a copy.",
            "The released version is stamped on each result, so later versions never change past checks.",
        ],
        tipsIcon: "MdRule",
    },
]);

m("certificates", "Lot records and certificates", "Attributes, certificates of analysis and documents", [
    {
        type: "steps", title: "Lot attributes and certificates",
        steps: [
            "On a lot's page, under **Attributes and certificates**, type or pick each value and click **Save**.",
            "**Attach a certificate**: its **Kind** (CoA, CoC, Test report, SDS), a **Title**, and a **File** (PDF, PNG or JPEG ≤ 10 MB) and/or document no. or link.",
            "**Withdraw…** one that was wrong or replaced, with the reason; it stays, marked withdrawn.",
            "**Certificate of analysis:** click **Prepare**, check each line's Pass or Fail, type the customer order, and **Issue and attach**.",
        ],
        tips: [
            "A split lot takes its parent's certificates and values; a merged lot takes every lot's certificates.",
            "When shipping, tick **Issue its certificate of analysis** to do both in one step.",
            "Attributes in bulk: import a CSV, all or nothing, with a dry run.",
        ],
        tipsIcon: "MdVerified",
    },
]);

m("instructionsWrite", "Work instructions", "Writing, releasing and checklists", [
    {
        type: "steps", title: "Write and release a work instruction",
        steps: [
            "Open **Configure → Work instructions**, type a **Code** (WI-EXT-001) and **Title**, and click **New instruction**.",
            "Write the steps under **Text**: # starts a heading, 1. a numbered step, - a point; two stars each side make words bold. **How it reads** shows it.",
            "Attach drawings or photos (PDF, PNG, JPEG, up to 10 MB, 20 per version).",
            "Say what it is shown for: a process's operation, a material or a line.",
            "Add a **setup** or **first-article** checklist if needed (optionally with a second sign-off), save, and release.",
        ],
        tips: [
            "One draft and one released version at a time; releasing makes the last one obsolete.",
            "The job page and the station always show the version released now; a draft never shows.",
            "Start waits for setup checklists; reports wait for both.",
        ],
        tipsIcon: "MdMenuBook",
    },
]);

m("releaseApproval", "Approving releases", "A second person before a process, BOM or specification is released", [
    {
        type: "steps", title: "Approve or send back a release",
        steps: [
            "Where the site asks for approval, the editor clicks **Submit version N for approval…**; it then waits and can't be changed.",
            "It appears in the **Inbox** of those who may approve releases at the site.",
            "Open it, read it, type a **Comment**, and click **Approve and release** (which releases it) or **Send back**.",
            "**Approvals** on its page lists every request: who submitted, who decided and why.",
        ],
        tips: [
            "You can't approve a draft you edited or submitted.",
            "The submitter may **Withdraw** it while it waits, to change it.",
            "Site settings → **Approval before release** decides which kinds need approval.",
        ],
        tipsIcon: "MdHowToReg",
    },
]);

// ===================== Engineering =====================

m("processes", "Processes", "Routes of operations, versioned and released", [
    {
        type: "steps", title: "Create and build a process",
        steps: [
            "On **Configure → Processes**, click **New process**, enter a **Code** (EXT-PACK) and **Name**, and click **Create and edit**.",
            "Drag a line or department onto the canvas to add an operation, or click **Add operation**. Set its **Number** (10, 20, 30) and **Name**.",
            "Drag more equipment onto an operation; set each **share** so they total exactly **100%** (**Split evenly** helps).",
            "Link operations by dragging from an operation's right-hand handle to the next; set **after start** or **after end**, and a **Lag**.",
            "With nothing selected, say what it makes: **All materials**, a class, or single materials; set it as a material's **default**.",
        ],
        tips: [
            "The editor saves after every change: *Saving…*, then *All changes saved*.",
            "Each equipment entry becomes its own job for its share of the quantity.",
            "*After start* runs both together (extrude and pack); *after end* is a hand-off.",
        ],
        tipsIcon: "MdAltRoute",
    },
    {
        type: "steps", title: "Release and change a process",
        steps: [
            "Fix what the yellow box lists (no equipment, shares not 100%, unlinked operations, a loop).",
            "Click **Release version N…**, optionally type what changed, and click **Release**.",
            "Where approval is required, click **Submit version N for approval…** instead; someone else approves it.",
            "To change it later, click **Change: start version N+1**: a draft copy. Release it; the old version becomes obsolete.",
        ],
        tips: [
            "A released version never changes: orders keep the version they were made from.",
            "An inactive process can't be picked for new orders.",
        ],
        tipsIcon: "MdNewReleases",
    },
]);

m("boms", "Bills of materials", "What a material is made from, versioned like processes", [
    {
        type: "steps", title: "Create and release a BOM",
        steps: [
            "On **Configure → BOMs**, click **New BOM**, pick the **Material** and **For process** (or **Any process**), and **Create and edit**.",
            "Set **Quantities are per**, such as 1000 m.",
            "**Add component** for each line: line number (10, 20…), **Component**, **Quantity**, **Unit**, **Scrap %** and **Consumed by**.",
            "For a process BOM, name the **operation** that consumes each component.",
            "Click **Save draft**, then **Release** (or submit for approval).",
        ],
        tips: [
            "One level only: a profile's BOM lists the blend; the blend has its own BOM.",
            "**Consumed by** backflush or time window takes components automatically from the line's feed points.",
            "Approved **alternates** on a line can be allocated in its place, at a ratio.",
        ],
        tipsIcon: "MdListAlt",
    },
]);

m("capabilities", "Where materials can run", "Capabilities, rates and batch sizes per line", [
    {
        type: "steps", title: "Add a line to a material",
        steps: [
            "Open the material on **Configure → Materials**; under **Where it can run**, click **Add line**.",
            "Choose the **Line** and enter the **Ideal rate, per hour** (the best it can do) and its **Rate unit**.",
            "Optionally: **Standard changeover** minutes, **Crew size**, **Preferred line**, a batch size or **… per machine count** for this line.",
            "For lot-tracked output, **Production lots on this line** (lot size, age, new lot each shift) fills lots over many reports.",
            "**Starts** today unless you pick a day; **Ends on** is optional. Click **Save**.",
        ],
        tips: [
            "Adding one creates the run standard from the ideal rate.",
            "Periods never overlap and are ended, not deleted.",
            "Many at once: **Import capabilities** from a CSV, all or nothing, with a dry run.",
        ],
        tipsIcon: "MdBolt",
    },
]);

// ===================== Management =====================

m("shifts", "Shift schedules", "Repeating schedules, which lines follow them, and one-off changes", [
    {
        type: "steps", title: "Add a shift schedule",
        steps: [
            "Open **Configure → Shifts**, click **New schedule**, and type a **Code** (2X8) and **Name**.",
            "Set **Repeats every (days)** and **Cycle starts on** (7 and a Monday for a weekly one).",
            "For each shift: its code, name, **Starts**, **Hours**, **Crew** and the days it runs. **Add shift** adds the next.",
            "Add **Planned breaks** (lunch) inside each shift.",
            "Check **The next 7 days** preview, and click **Create schedule**.",
            "On **Configure → Equipment**, set a site, area or line's **Shifts** to the schedule; what's below inherits it.",
        ],
        tips: [
            "**One-off changes**: an extra Saturday, a late start or a shift off one day, without changing the schedule.",
            "A line can run **Around the clock** under a site with shifts.",
            "Holidays and shutdowns are **calendar exceptions** on Site settings.",
        ],
        tipsIcon: "MdEventNote",
    },
]);

m("people", "People, badges and terminals", "Who works here, their qualifications, and the devices on the floor", [
    {
        type: "steps", title: "Set up people and crews",
        steps: [
            "Open **Configure → People**. On **Crews**, type a **Code** (RED) and **Name**, and click **Add crew**.",
            "On **People**, fill in **Employee no.**, **Name**, **Crew** and, to clock themselves on, their Pulaski **User name**. Click **Add person**.",
            "Set each shift's **Crew** on **Configure → Shifts**.",
            "**Badge**: click **Issue**, scan the badge and type a PIN (4 to 8 digits) if terminals ask. **Replace** or **Revoke** a lost one.",
            "**Qualifications**: add one (code, name, months valid), require it on equipment, and **Grant** it to people; **Renew** or **Revoke** later.",
        ],
        tips: [
            "People from HR or the ERP can be imported (CSV, SCIM or a connection); the source then owns their number, name and active flag.",
            "**Also works at** lets someone work at another site.",
            "**Expiring in 30 days** shows who needs renewing.",
        ],
        tipsIcon: "MdPeopleAlt",
    },
    {
        type: "steps", title: "Set up a terminal or line board",
        steps: [
            "Open **Configure → Terminals**. Type a **Code** and **Name**, choose **Offers the lines of** and the **First line offered**, and **Badge** or **Badge and PIN**.",
            "Click **Enrolment code**: a ten-character code shows once, for 15 minutes.",
            "On the device, open the address ending **/terminal/enrol**, type the code and press **Enrol**.",
            "Leave it on the badge screen, or open **/terminal/station** (operator station) or **/terminal/board** (a TV line board).",
            "To stop one (lost, moved), **Change**, untick **Active** and save.",
        ],
        tips: [
            "Moving it to another device: issue a new code and enrol the new one; the old one stops.",
            "Choose the terminal's **Language** so the floor reads its own.",
        ],
        tipsIcon: "MdTabletMac",
    },
]);

m("siteSettings", "Site settings", "How the site works: tolerances, targets, calendar, andon and more", [
    {
        type: "cards", title: "What you set on Site settings",
        cards: [
            { icon: "MdSwapHoriz", title: "Changeovers", text: "**Automatic** (the floor starts a changeover job) or **Placed by planners**. Click **Save changeovers**." },
            { icon: "MdPercent", title: "Reporting tolerances", text: "**Output** and **Consumption**, 100 to 1000%. **Use the defaults** puts both back to 110%." },
            { icon: "MdFlag", title: "OEE targets", text: "For the site or a piece of equipment, from a day: availability, performance, quality, OEE. **End today** stops one." },
            { icon: "MdBeachAccess", title: "Calendar exceptions", text: "Holidays and shutdowns for the site, an area or a line, from and until. **Remove** stops one; **Put back** restores it." },
            { icon: "MdCampaign", title: "Andon call types", text: "Code, name, the minutes after which an unanswered call **Escalates**, and its order." },
            { icon: "MdTune", title: "And more", text: "Lot allocations, planning rates from run history, ERP orders not whole batches, labels and printers, approval before release." },
        ],
        notes: "Configure → Site settings. Seeing it needs site_settings.view; changing it site_settings.manage (Manager, Administrator). Targets are never deleted, so a report always measures a day against the targets of that day.",
    },
    {
        type: "steps", title: "The setup checklist",
        steps: [
            "Open **Configure → Site settings → Setup checklist** (the Overview links to it until the site is ready).",
            "Work down the list: each step is **Done** (with how many), **To do** or **Not needed**, and opens the screen where it's done.",
            "A step is done as soon as what it asks for exists; come back and it has moved on.",
            "For an optional step that doesn't apply, click **Not needed here** (**Needed after all** takes it back).",
        ],
        tips: [
            "Required: a site with its time zone, a line, a material, a capability, a released process, a location, a work order, a job started.",
            "It is worked out from what the site has: nothing is ticked off by hand.",
        ],
        tipsIcon: "MdChecklist",
    },
]);

m("reasons", "Reason codes", "The reasons people choose, and how machine time counts", [
    {
        type: "steps", title: "Maintain reason codes",
        steps: [
            "Open **Configure → Reasons**: one tab per kind (adjustments, holds, scrap, run ends, utilization…).",
            "To add one, type a **code**, a **name**, choose **Every site** or one, tick **Needs a comment** if people must explain, and click **Add**.",
            "A **utilization** reason also has a **Class**: *Runtime*, *Downtime* (against availability) or *Planned (not in OEE)*.",
            "File reasons under **groups** (tick **Group** before **Add**; choose **Under**); move one by choosing another group and **Save**.",
            "To stop using a reason, untick **Active**. A code and kind never change.",
        ],
        tips: [
            "Moves never change history: it refers to reason ids.",
            "Reports can total stops by group level.",
            "**Make group** turns an unused reason into a group.",
        ],
        tipsIcon: "MdLabel",
    },
]);

// ===================== Audit =====================

m("audit", "The audit trail", "Who changed what, and when", [
    {
        type: "steps", title: "Search the audit trail",
        steps: [
            "**One thing's history**: on its page, click **History** (a material, capability, equipment, process, lot, work order, user).",
            "**Search**: open **Configure → Integrations → Audit trail**, choose an **Area**, type **Who** or **Text in the change**, and the days.",
            "Click **Search**; **Older changes** shows the next page.",
            "Click **Details**: a change shows the fields that changed, **Before** and **After**; **As recorded** shows it exactly.",
        ],
        tips: [
            "Every change is recorded as an event; nothing is edited afterwards.",
            "A subject's history covers all time; a search covers up to 92 days.",
            "Personal settings (favourites, filters) aren't audited.",
        ],
        tipsIcon: "MdManageSearch",
    },
]);

m("traceJournal", "Tracing and the journal", "Genealogy forward and back, and every movement", [
    {
        type: "steps", title: "Trace a lot",
        steps: [
            "On a lot's page, **Genealogy** shows the lots it was **made from** and what it was **used in**.",
            "Click **Trace back** for everything it was made from, all the way up, or **Trace forward** for everything made from it.",
            "Each pair carries a chip: **Exact**, **Estimated** (by time), **Declared** (picked) or **Possible**.",
            "**As it was**: choose a date and time and **Show** the lot then: quantity, where, status.",
            "**Inventory → Journal**: every movement in the site, filtered by days, movement, material, lot or who.",
        ],
        tips: [
            "Through a silo, traces are narrowed by its flow (mixed or first in, first out).",
            "Back-dated movements count from their effective time, so As it was agrees with the journal.",
        ],
        tipsIcon: "MdAccountTree",
    },
]);

m("bi", "Reports in other tools", "Power BI, Excel or a warehouse, from the read-only reporting views", [
    {
        type: "steps", title: "Build your own reports",
        steps: [
            "Ask whoever runs the database for a **reporting login**, a member of **pulaski_report**: it reads the reporting views and nothing else.",
            "In Power BI, Excel or your warehouse tool, connect to the PostgreSQL database **pulaski** with that login.",
            "Pick tables from the **report** schema: sites, equipment, materials, production, runs, stops, OEE by shift and day, lots, movements, genealogy, labor, quality.",
            "Follow **design/reporting.md**, the analysts' guide: what each view holds, how they join, how OEE adds up.",
        ],
        tips: [
            "Figures are worked out the way the screens work them out.",
            "Add hours and earned hours, never average OEE percentages.",
            "The views keep their meaning as Pulaski changes: columns are only added.",
        ],
        tipsIcon: "MdBarChart",
    },
]);

// ===================== Administration =====================

m("users", "Users and roles", "Who signs in, and what each role may do", [
    {
        type: "steps", title: "Add a user and change their roles",
        steps: [
            "Open **Configure → Users**, **Users** tab, and click **New user**.",
            "Fill in **User name**, **Name**, an optional **E-mail** and a **Temporary password**; choose a **Role** and its **site**.",
            "Click **Create user**, and tell them the user name and password yourself: Pulaski sends nothing.",
            "To change someone, click **Change**: details, **Can sign in**, roles (**Add a role…**, **Take away**) or **Reset password**.",
            "From your organisation's sign-in: they sign in once, show **Awaiting a role**; add one and ask them to sign in again.",
        ],
        tips: [
            "Every user keeps a role; a role someone holds can't be switched off.",
            "**Reset password** also unlocks a locked-out user.",
            "Tick **Service account** for a machine user, such as an edge node's.",
        ],
        tipsIcon: "MdPersonAdd",
    },
    {
        type: "steps", title: "Add or change a role",
        steps: [
            "On **Configure → Users**, open the **Roles** tab and click **New role** (or **Change**).",
            "Type a **Code** (line_lead; fixed later) and a **Name**.",
            "Under **May change**, tick each permission, grouped by area: Planning, Floor, Inventory, Configuration, Administration.",
            "Tick **May be held at every site** for a role some people need everywhere (a regional quality lead).",
            "Click **Create role** or **Save role**, then give it to people on the **Users** tab.",
        ],
        tips: [
            "A permission to change brings the views it needs.",
            "**Administrator** has every permission everywhere and can't be changed.",
            "The **Permissions** tab sets one permission across roles at once.",
        ],
        tipsIcon: "MdAdminPanelSettings",
    },
]);

m("equipment", "Equipment", "The plant as a tree, and copying lines and sites", [
    {
        type: "steps", title: "Add equipment",
        steps: [
            "Open **Configure → Equipment**. Click the equipment the new one goes **under**, then **Add child**.",
            "Fill in its **Code** (fixed later) and **Name**, its **Level** and **Role** (which sets sensible capability defaults).",
            "For a site, its **Time zone** (America/Chicago).",
            "Tick its capabilities: **Can run jobs**, **Schedulable**, **Holds inventory**, **Tracks state**.",
            "Properties save as you press Enter or leave a field; **Esc** undoes an edit before it is saved.",
        ],
        tips: [
            "**Copy this line** copies a line with its machines, capabilities and settings, at this site or another.",
            "**Start a new site from this one** copies a site's lists and settings into a new site.",
            "Deactivate equipment you no longer use; a location with stock can't be deactivated.",
        ],
        tipsIcon: "MdFactory",
    },
]);

m("machineData", "Machine data", "Machine profiles, edge nodes and tags", [
    {
        type: "flow", title: "From the machine to Pulaski",
        stages: [
            { label: "Profile", text: "**Machine profiles**: one per kind of machine. Maps its raw state values to PackML states and its stop codes to reasons." },
            { label: "Edge node", text: "**Edge nodes**: add a node and its connections: Sparkplug B (MQTT), OPC UA or EtherNet/IP." },
            { label: "User", text: "Add a user with the **Edge node** role at the site, as a service account." },
            { label: "Install", text: "Copy the node's **Setup** into the edge's appsettings.json and start Pulaski Edge." },
            { label: "Bind tags", text: "On its **Seen** tab, **Bind** each metric to equipment: state, stop code, counters, process values." },
        ],
        note: "The edge buffers what it collects and forwards it once the link is back, so a network drop loses nothing. **Configure → System health** shows edge nodes that have gone quiet.",
    },
]);

m("adminSystem", "Running Pulaski", "System health, integrations and languages", [
    {
        type: "cards", title: "Keep Pulaski healthy",
        cards: [
            { icon: "MdMonitorHeart", title: "System health", text: "**Configure → System health**: database, background services, edge nodes, delivery feeds, connections, OEE rollups and e-mail, each **Failing**, **Needs a look**, **Healthy** or **Off**." },
            { icon: "MdCloudSync", title: "Integrations", text: "**Configure → Integrations**: feeds to the ERP and document systems (address, what it sends), and **retry** or **skip** a failed delivery." },
            { icon: "MdTranslate", title: "Languages", text: "**Configure → Languages**: add a language, then type its **Translations**; Spanish ships with Pulaski. A term without one shows in English." },
            { icon: "MdStraighten", title: "Units", text: "**Configure → Units**: standard units, and each material's own conversions (1 m = 1.6 kg)." },
        ],
        notes: "Equipment (with machine profiles and edge nodes), units and retrying or skipping ERP deliveries belong to the Administrator. Secrets (feed secrets, connection passwords) are kept in configuration, never in the database.",
    },
]);

// ===================== Roles =====================

const roles = [
    {
        code: "planner", name: "Planner", icon: "MdViewTimeline", tagline: "Plan and sequence the work, and keep the plan honest",
        summary: "You turn orders into a plan the floor can run: you create work orders, put their jobs on lines in the right order, publish and freeze the plan, and keep rates, changeovers and materials right so the times Pulaski works out are true. Scheduling is part of this role: Pulaski has no separate scheduler.",
        day: [
            { icon: "MdWarningAmber", title: "Start with what needs you", text: "The Overview and Work orders tiles: orders behind, conflicts, not on a line, ERP changes." },
            { icon: "MdViewTimeline", title: "Shape the schedule", text: "Place new orders, resequence, pin, plan to departments." },
            { icon: "MdPublish", title: "Publish to the floor", text: "Dispatch and firm orders; watch links and overlaps." },
            { icon: "MdInventory", title: "Make sure material is there", text: "Shortages, allocations and picking lists for coming jobs." },
        ],
        sees: ["Overview, Work orders, Schedule, Floor, Machines, Inventory", "Quality, Materials, Processes and BOMs, Specifications", "Work instructions, Changeovers, Site settings (to look at)"],
        changes: ["The schedule: place, sequence, pin, assign and cancel jobs, and replace one with a partial batch", "Work orders: create, dispatch, un-dispatch, firm, cancel, close", "Planning rates and changeover rules", "Materials", "Allocating lots to jobs"],
        agenda: [
            { icon: "MdSchool", title: "Basics", text: "Signing in, the menu, the inbox, the Overview." },
            { icon: "MdAddBox", title: "Orders", text: "How planning works, creating orders, the Work orders screen, ERP orders." },
            { icon: "MdViewTimeline", title: "Schedule", text: "The four views, moving and pinning jobs, departments, dispatch and firm." },
            { icon: "MdTune", title: "What the times rest on", text: "Materials, rates, changeover rules, allocations and shortages." },
        ],
        modules: ["basics", "overview", "planningIdeas", "createOrder", "workOrders", "erpOrders", "schedule", "departments", "materialsPlanning", "changeovers", "allocations", "alerts"],
        quiz: [
            { q: "You want a job to run sooner. Do you drag it to an earlier time?", short: "Drag a job to an earlier time?", a: "No: you move it to an earlier place in its line's sequence (Gantt or side panel); Pulaski works out the time." },
            { q: "What does **Dispatch** save, and what does **Firm** add?", short: "Dispatch and Firm", a: "Dispatch publishes the order and saves each job's times as the baseline; Firm freezes it: no un-dispatch, jobs keep their start." },
            { q: "An order is refused with *…is not set up to run…*. What's missing?", short: "\"…is not set up to run…\"", a: "A capability for the material on that line: add it on the material's page, or pick another process." },
            { q: "A firmed job's link can't be met. Will Pulaski move it to another line?", short: "Will Pulaski move a job to meet a link?", a: "Never. It warns 'Link not met'; you move the earlier operation's work forward or to another line." },
            { q: "Where do you plan work that the floor should place on any line of a department?", short: "Plan to a department", a: "Plan the job to the department with a window and a finish-by; the floor assigns it with Run on … ." },
            { q: "How do you see whether coming jobs will have their components?", short: "Will coming jobs have material?", a: "Inventory → Shortages: needed, available, incoming and short, per job in the order they're needed." },
        ],
        quickref: [
            ["Create a work order", "Schedule → **New work order**"],
            ["See which orders are behind", "Operate → Work orders → **Behind** tile"],
            ["Move or resequence a job", "Schedule → Gantt (drag), or the job's side panel → **Move**"],
            ["Hold a job until material arrives", "Side panel → **Hold until** → **Set**"],
            ["Publish and freeze an order", "Side panel or Kanban → **Dispatch**, then **Firm**"],
            ["Accept or reject an ERP change", "Work orders → ⓘ on the row → **Accept** / **Reject**"],
            ["Review proposed planning rates", "Configure → **Planning rates**"],
            ["Changeover times", "Configure → **Changeovers**"],
            ["Check material for coming jobs", "Inventory → **Shortages**"],
        ],
        helpFrom: "A lead planner or key user first; your administrator for sign-in, roles and lines that aren't schedulable.",
    },
    {
        code: "operator", name: "Operator", icon: "MdEngineering", tagline: "Run the line and record what really happens",
        summary: "You run jobs on your line and record what happens: start, pause and complete jobs, report good and scrap, say why machines stopped, record quality results, use and move material, clock on and off, and call for help when the line needs it. What you record is what the plant's figures are made of.",
        day: [
            { icon: "MdBadge", title: "Clock on", text: "On the Floor, or badge at the terminal or station." },
            { icon: "MdPlayArrow", title: "Run the job", text: "Start the next job, follow its instructions and checklists." },
            { icon: "MdAddTask", title: "Report and code", text: "Report output (from the machine when it counts), and give stops their reason." },
            { icon: "MdSwapCalls", title: "Hand over", text: "Notes and open items for the next shift, then clock off." },
        ],
        sees: ["Overview, Work orders (to look at), Floor, Machines, Inventory", "Quality, Specifications and Work instructions (to look at)"],
        changes: ["Start, pause and complete jobs", "Report production and consumption", "Code machine stops", "Receive and move lots", "Clock yourself on and off", "Record quality results, raise NCRs", "Call for help, request maintenance, write handover notes"],
        agenda: [
            { icon: "MdSchool", title: "Basics", text: "Signing in, the menu and how Pulaski behaves." },
            { icon: "MdPlayCircleOutline", title: "Running jobs", text: "The Floor, starting, reporting, pausing and completing." },
            { icon: "MdTabletAndroid", title: "At the station", text: "Badging in, running, reporting and coding stops at the line." },
            { icon: "MdInventory2", title: "Around the job", text: "Your time, stops, material, quality, calls for help." },
        ],
        modules: ["basics", "floorIdeas", "runJobs", "station", "labourOwn", "machinesView", "inventoryBasics", "qualityFloor", "floorTools"],
        quiz: [
            { q: "Can two jobs run on the same line at once?", short: "Two jobs on one line?", a: "No: one run per line. Pause or complete the running job first." },
            { q: "Your report is refused as more than the run can hold. Why?", short: "Report refused: more than the run holds", a: "The rate check: run length × ideal rate × the site's tolerance (110%). The message says how much more fits." },
            { q: "The line's counter counted 348 m. How do you report it without typing?", short: "Report what the machine counted", a: "Report → From the machine → Use these figures → check → Report. Don't report it by hand as well." },
            { q: "A machine stopped and nobody said why. Where do you code it?", short: "Code a stop", a: "At the station (Give the reason) or Operate → Machines → Stops → Code." },
            { q: "The red bar says *No connection to Pulaski*. Is your report saved?", short: "No connection bar", a: "No: nothing is saved. What you typed stays; send it again once the bar goes." },
            { q: "**Start** is greyed out on a ready job. What might be missing?", short: "Start greyed out", a: "The line is busy, the order isn't on the floor, a process link isn't met, or the setup checklist hasn't passed." },
        ],
        quickref: [
            ["Start, pause or complete a job", "Operate → **Floor**, the line's card"],
            ["Report good and scrap", "Floor → **Report** (or **From the machine**)"],
            ["Give a stop its reason", "Station → **Give the reason**, or Machines → **Stops**"],
            ["Clock on or off", "Floor → **Clock on** / **Clock off**, or badge"],
            ["Use or move a lot", "The lot's page → **Consume** / **Move**, or Inventory → **Scan**"],
            ["Record a quality result", "Operate → **Quality** → Record a result"],
            ["Call for help", "Station → **Call for help**"],
            ["Ask for something to be fixed", "Station or Machines → **Request maintenance**"],
            ["Leave word for the next shift", "Operate → **Handover**"],
        ],
        helpFrom: "Your supervisor or line lead first; your administrator for sign-in and badges.",
    },
    {
        code: "supervisor", name: "Supervisor", icon: "MdSupervisorAccount", tagline: "Run the floor, put things right, approve the shift",
        summary: "You run the floor for your shift: you do everything an operator does, and you put records right when they're wrong, decide on partial batches, answer calls for help, work maintenance requests, check crews in and approve their labor, and count and control stock. You're the one who makes the shift's figures true before they're approved.",
        day: [
            { icon: "MdWarningAmber", title: "See what needs you", text: "The Overview, your Inbox, the Andon page." },
            { icon: "MdGroups", title: "Crew on lines", text: "Check the crew in; add people for other time and breaks." },
            { icon: "MdBuildCircle", title: "Put things right", text: "Late reports, reversals, stops entered, split or merged, partial batches." },
            { icon: "MdFactCheck", title: "Close the shift", text: "Production by shift, OEE, approve the shift's labor." },
        ],
        sees: ["Overview, Work orders (to look at), Floor, Machines, Inventory", "Quality, Specifications and Work instructions (to look at)", "Labor, Site settings (to look at)"],
        changes: ["Start, pause, complete and reopen jobs; report and reverse production", "Record and approve labor", "Code, enter, split and merge machine stops", "Receive, move, ship, hold and release lots; counts; allocations", "Approve or reject partial batches; sign off checklists", "Record quality results; answer andon calls; work maintenance requests"],
        agenda: [
            { icon: "MdSchool", title: "Basics", text: "Signing in, the inbox, the Overview." },
            { icon: "MdPlayCircleOutline", title: "The floor", text: "Running and reporting, and putting production right." },
            { icon: "MdGroups", title: "People", text: "Crews, breaks, shift review and approval." },
            { icon: "MdPrecisionManufacturing", title: "Machines and stock", text: "Stops, andon, maintenance, counts and OEE." },
        ],
        modules: ["basics", "overview", "floorIdeas", "runJobs", "supervisorJobs", "station", "supervisorLabour", "machinesView", "supervisorStops", "andonAnswer", "maintenanceWork", "inventoryBasics", "stockControl", "qualityFloor", "oeeReports", "floorTools", "alerts"],
        quiz: [
            { q: "A report went into the wrong job. What do you do?", short: "Report on the wrong job", a: "Reverse it on the job's page (reason and comment), then report it on the right job." },
            { q: "Can you approve a partial batch you reported yourself?", short: "Approve your own partial batch?", a: "No: never the person who reported it. Another Supervisor or Quality user decides." },
            { q: "A machine reported a jam and the wait for cartons as one stop. How do you fix it?", short: "One stop that was two", a: "Machines → Stops → split the stop at the time, each part with its reason (and a reason in words)." },
            { q: "When can you approve a shift's labor, and what happens to anyone still on?", short: "Approving a shift", a: "Once the shift has ended; anyone still on is clocked off at its end." },
            { q: "A lot moved after you counted it. What happens when you post?", short: "Lot moved after counting", a: "It is refused for that lot, marked 'changed since counted': count it again." },
            { q: "What turns an andon call red?", short: "A red andon call", a: "Nobody acknowledged it within its type's escalation minutes." },
        ],
        quickref: [
            ["See what waits for you", "**Inbox**, and the Overview's Needs attention"],
            ["Report after the run, reverse or correct", "The job's page → **Production reports**"],
            ["Decide a partial batch", "The job's page → **Partial batches**"],
            ["Check a crew in", "Operate → **Labor** → **Check crew in**"],
            ["Approve the shift", "Labor → **Shift review** → **Approve shift**"],
            ["Enter, split or merge a stop", "Machines → **Stops**"],
            ["Answer a call", "Operate → **Andon** → **I'm on it**"],
            ["Work a maintenance request", "Machines → **Maintenance** → **Start** / **Close**"],
            ["Count a location", "Inventory → **Counts** → **Start a count**"],
        ],
        helpFrom: "Your production manager or key user first; your administrator for sign-in, roles and setup.",
    },
    {
        code: "quality", name: "Quality", icon: "MdVerified", tagline: "Decide what is good, and keep the evidence",
        summary: "You decide what is good: you hold, release and reject lots, record and judge results against specifications, respond to SPC alerts, work nonconformance reports, keep lot attributes and certificates, write specifications and work instructions, and approve releases. You can also correct inventory when the record is wrong.",
        day: [
            { icon: "MdShowChart", title: "Signals first", text: "Open SPC alerts, lots on hold, NCR actions in your Inbox." },
            { icon: "MdScience", title: "Results and lots", text: "Record results, decide held lots, partial batches, checklists." },
            { icon: "MdReportGmailerrorred", title: "Nonconformances", text: "Investigate, act and close NCRs, and carry out dispositions." },
            { icon: "MdRule", title: "Specifications", text: "Specs, sampling plans, instructions, approving releases." },
        ],
        sees: ["Overview, Floor, Machines, Inventory", "Quality, Materials, Specifications, Work instructions"],
        changes: ["Hold and release lots; correct and back-date inventory", "Decide partial batches; sign off checklists", "Record results, leave them out, reject lots", "Specifications and lot attribute definitions", "Lot attributes and certificates", "Work instructions; approving releases", "NCRs; answering andon calls"],
        agenda: [
            { icon: "MdSchool", title: "Basics", text: "Signing in, the inbox, the Overview." },
            { icon: "MdScience", title: "Results", text: "Recording, SPC charts and alerts, holding and rejecting lots." },
            { icon: "MdReportGmailerrorred", title: "When something is wrong", text: "NCRs, correcting inventory, reversals." },
            { icon: "MdRule", title: "Standards", text: "Specifications, certificates, instructions, approvals." },
        ],
        modules: ["basics", "overview", "qualityFloor", "spc", "qualityLots", "correctInventory", "ncr", "decisions", "specs", "certificates", "instructionsWrite", "releaseApproval", "andonAnswer", "handover", "alerts"],
        quiz: [
            { q: "A result is out of spec on a characteristic that **holds the lot**. What happens?", short: "Out of spec, holds the lot", a: "An available lot is put on hold with reason OUT-OF-SPEC, and Pulaski says so." },
            { q: "A result was typed wrong. Can you edit it?", short: "Edit a wrong result?", a: "No: Leave it out with why. It stays, marked Left out, and no longer counts." },
            { q: "What does **Reject…** do to a lot made in a run?", short: "Reject a lot", a: "Writes off what's left as scrap and moves its quantity from good to scrap on that run." },
            { q: "Can a process be in specification and still out of control?", short: "In spec but out of control?", a: "Yes: control limits come from the results, not the specification." },
            { q: "What must be true before an NCR can close?", short: "Closing an NCR", a: "It is acting, has at least one action, none open, and a disposition with a closing note." },
            { q: "Can you approve a specification you edited?", short: "Approve your own release?", a: "No: someone who didn't edit or submit it approves it." },
        ],
        quickref: [
            ["Record a result", "Operate → **Quality** → Record a result"],
            ["Respond to an SPC alert", "Quality → **SPC alerts** → **Respond**"],
            ["See a control chart", "Quality → **Control charts**"],
            ["Hold, release or reject a lot", "The lot's page → **Hold…** / **Release** / **Reject…**"],
            ["Raise or work an NCR", "Quality → **Nonconformances**, or **Raise an NCR**"],
            ["Correct or reverse inventory", "The lot's page → **Write up/off**, **Correct…**, **Reverse**"],
            ["Issue a certificate of analysis", "The lot's page → **Prepare** → **Issue and attach**"],
            ["Write a specification", "Configure → **Specifications**"],
            ["Approve a release", "**Inbox** → Releases to approve"],
        ],
        helpFrom: "Your quality manager or key user first; your administrator for sign-in, roles and setup.",
    },
    {
        code: "manager", name: "Manager", icon: "MdBusinessCenter", tagline: "See everything, and set how the site works",
        summary: "You see every screen, and you set up how the site works: its shift schedules and calendar, reason codes, people, crews, qualifications, badges and terminals, and its settings: tolerances, OEE targets, andon types and more. You also approve labor, reopen jobs and close work orders, and you watch the site's performance.",
        day: [
            { icon: "MdInsights", title: "Watch the site", text: "The Overview, OEE and its trend against targets." },
            { icon: "MdFactCheck", title: "Decide", text: "Approve labor, close finished work orders, reopen jobs." },
            { icon: "MdEventNote", title: "Shape the calendar", text: "Shift schedules, one-off changes, holidays and shutdowns." },
            { icon: "MdTune", title: "Keep setup right", text: "People, terminals, reasons and site settings." },
        ],
        sees: ["Every screen (Users only for administrators)"],
        changes: ["Reopen jobs; cancel and close work orders", "Approve labor", "Reason codes", "Shift schedules", "People, crews and qualifications; badges; shop-floor terminals", "Lot attribute definitions", "The site's settings"],
        agenda: [
            { icon: "MdSchool", title: "Basics", text: "Signing in, the inbox, the Overview." },
            { icon: "MdQueryStats", title: "Performance", text: "OEE, production by shift, shift approval." },
            { icon: "MdEventNote", title: "Time and people", text: "Shift schedules, people, badges, terminals." },
            { icon: "MdTune", title: "The site", text: "Site settings, the setup checklist, reason codes." },
        ],
        modules: ["basics", "overview", "oeeReports", "workOrders", "labourApprove", "shifts", "people", "siteSettings", "reasons", "alerts"],
        quiz: [
            { q: "Monday is a holiday for the whole site. Where do you take the time out?", short: "A site holiday", a: "Site settings → Calendar exceptions, applying to the site, from and until." },
            { q: "One extra Saturday shift, without changing the schedule. How?", short: "An extra Saturday", a: "Configure → Shifts → One-off changes: the day, a new shift code, Runs, and save." },
            { q: "Can an OEE target be deleted?", short: "Delete a target?", a: "No: targets are ended (End today) or replaced from a day, never deleted." },
            { q: "A tablet was stolen. How do you stop it working?", short: "A stolen terminal", a: "Configure → Terminals → Change, untick Active, save." },
            { q: "What does the setup checklist count as done?", short: "The setup checklist", a: "A step is done as soon as what it asks for exists at the site; nothing is ticked by hand." },
            { q: "Why can't a reason code be renamed to a different code?", short: "Change a reason's code?", a: "History refers to it: the code and kind never change; switch it off and add another." },
        ],
        quickref: [
            ["See how the site is doing", "Operate → **Overview**; Machines → **Utilization and OEE**"],
            ["Approve labor", "Labor → **Shift review**"],
            ["Close a finished work order", "Work orders → **Actions** → Close"],
            ["Change shifts or add a one-off", "Configure → **Shifts**"],
            ["Holidays and shutdowns", "Site settings → **Calendar exceptions**"],
            ["OEE targets and tolerances", "Configure → **Site settings**"],
            ["People, crews, badges", "Configure → **People**"],
            ["Terminals and line boards", "Configure → **Terminals**"],
            ["Reason codes", "Configure → **Reasons**"],
        ],
        helpFrom: "Your administrator for sign-in, roles, equipment and integrations.",
    },
    {
        code: "auditor", name: "Auditor", icon: "MdManageSearch", tagline: "Check the record, and correct it the right way",
        summary: "You see every screen and check that the record is true: who changed what and when, where every lot came from and went, how the counts compare. Where it's wrong you correct it the right way: reversals and corrections that leave the original in place, stock counts, and back-dated movements. You don't plan or run jobs.",
        day: [
            { icon: "MdManageSearch", title: "Look back", text: "The audit trail and a thing's History." },
            { icon: "MdAccountTree", title: "Follow material", text: "Trace lots back and forward; the movement journal." },
            { icon: "MdChecklistRtl", title: "Count", text: "Count locations and post the differences." },
            { icon: "MdUndo", title: "Correct properly", text: "Reverse movements and production reports, never edit." },
        ],
        sees: ["Every screen (Users only for administrators)"],
        changes: ["Correct inventory: write up, write off, correct, split, merge", "Reverse consumption, splits, merges and transfers", "Reverse and correct production reports", "Stock counts", "Back-date inventory movements"],
        agenda: [
            { icon: "MdSchool", title: "Basics", text: "Signing in, the menu, how Pulaski behaves." },
            { icon: "MdManageSearch", title: "The record", text: "The audit trail, tracing lots, the journal." },
            { icon: "MdUndo", title: "Correcting it", text: "Inventory corrections, reversals, counts." },
            { icon: "MdBarChart", title: "Analysis", text: "Reports in Power BI, Excel or a warehouse." },
        ],
        modules: ["basics", "audit", "traceJournal", "inventoryIdeas", "correctInventory", "counts", "correctReports", "bi"],
        quiz: [
            { q: "Can a recorded movement be edited?", short: "Edit a movement?", a: "No: it is reversed by a new movement; the original stays, marked reversed, with who and why." },
            { q: "How do you see one material's every change?", short: "One thing's history", a: "Its page → History: the audit trail with every recorded change, newest first, all time." },
            { q: "A reversal is refused: *Since then: consumed into WO-1001-10*. Options?", short: "Reversal refused", a: "Reverse the consumption first, or tick With everything after it that depends on it, Check, then Undo." },
            { q: "How far back can a movement be dated?", short: "Back-dating limit", a: "Up to 31 days, with a comment, never before the lot's stock or a posted count." },
            { q: "What does a trace's **Estimated** chip mean?", short: "Estimated in a trace", a: "The pair was made by declaration time, not picked or certain." },
            { q: "Where do analysts get data for their own dashboards?", short: "Data for dashboards", a: "The read-only report schema with a pulaski_report login; design/reporting.md explains it." },
        ],
        quickref: [
            ["Who changed something", "Its page → **History**, or Integrations → **Audit trail**"],
            ["Every movement", "Inventory → **Journal**"],
            ["Where a lot came from or went", "The lot's page → **Trace back** / **Trace forward**"],
            ["A lot at a past time", "The lot's page → **As it was**"],
            ["Reverse a movement", "The lot's history → **Reverse**"],
            ["Reverse a production report", "The job's page → **Reverse…** / **Correct…**"],
            ["Count a location", "Inventory → **Counts**"],
            ["Your own reports", "The **report** schema; design/reporting.md"],
        ],
        helpFrom: "Your administrator for sign-in, roles and a reporting login.",
    },
    {
        code: "process_engineer", name: "Process engineer", icon: "MdSchema", tagline: "Define how products are made",
        summary: "You define how products are made: the processes (routes of operations on lines and departments), the bills of materials, which lines can run each material and how fast, changeover times, quality characteristics and specifications, and work instructions with their checklists. You also approve others' releases where the site asks for it.",
        day: [
            { icon: "MdAltRoute", title: "Routes", text: "Create and release processes; start new versions." },
            { icon: "MdListAlt", title: "BOMs", text: "Components per operation, scrap allowances, alternates." },
            { icon: "MdBolt", title: "Capabilities", text: "Which lines run each material, ideal rates, changeovers." },
            { icon: "MdMenuBook", title: "Standards", text: "Specifications, instructions, checklists and approvals." },
        ],
        sees: ["Equipment, Materials, Processes and BOMs, Units", "Specifications, Work instructions", "Schedule, Changeovers"],
        changes: ["Processes and BOMs", "Which lines can run each material (capabilities)", "Changeover rules", "Characteristics and specifications", "Work instructions", "Approve releases"],
        agenda: [
            { icon: "MdSchool", title: "Basics", text: "Signing in, the menu, how saving works." },
            { icon: "MdAltRoute", title: "How it's made", text: "Processes, BOMs, capabilities." },
            { icon: "MdSwapHoriz", title: "Changeovers", text: "How long each change of material takes." },
            { icon: "MdRule", title: "Standards", text: "Specifications, instructions, approvals." },
        ],
        modules: ["basics", "planningIdeas", "processes", "boms", "capabilities", "changeovers", "specs", "instructionsWrite", "releaseApproval"],
        quiz: [
            { q: "An operation's shares add up to 90%. Can you release?", short: "Shares at 90%", a: "No: each operation's shares must total exactly 100%; the yellow box lists it." },
            { q: "Can you change a released process?", short: "Change a released process?", a: "No: Change: start version N+1 makes a draft copy; release it and the old version becomes obsolete." },
            { q: "What's the difference between **after start** and **after end**?", short: "After start / after end", a: "After start: both run together as material flows; after end: a hand-off, the next waits for the earlier to finish." },
            { q: "A profile is made from a blend made from resin. What's in the profile's BOM?", short: "Multi-level BOM", a: "The blend only: BOMs are one level; the blend has its own BOM." },
            { q: "What does adding a line to a material create?", short: "Adding a capability", a: "A capability with its ideal rate, and the run standard the schedule plans with." },
            { q: "When does a work instruction show at the job?", short: "Instruction at the job", a: "Once released, for the job's operation, line or material; a draft never shows." },
        ],
        quickref: [
            ["Create or change a process", "Configure → **Processes**"],
            ["Create a BOM", "Configure → **BOMs** → **New BOM**"],
            ["Let a line run a material", "The material's page → **Where it can run** → **Add line**"],
            ["Changeover times", "Configure → **Changeovers**"],
            ["Characteristics and specs", "Configure → **Specifications**"],
            ["Work instructions", "Configure → **Work instructions**"],
            ["Approve a release", "**Inbox** → Releases to approve"],
        ],
        helpFrom: "Your engineering lead or key user first; your administrator for equipment, units and roles.",
    },
    {
        code: "maintenance", name: "Maintenance", icon: "MdHandyman", tagline: "Keep the machines running",
        summary: "You keep the machines running: you work maintenance requests from start to close, set up preventive plans by run hours or counts, answer andon calls, give stops their reason, and leave word for the next shift. You see the floor, machines and equipment, and read the work instructions.",
        day: [
            { icon: "MdInbox", title: "See what's waiting", text: "Requests nobody has started, in your Inbox and on the Maintenance tab." },
            { icon: "MdCampaign", title: "Answer calls", text: "Andon calls for maintenance, at the station or on the Andon page." },
            { icon: "MdHandyman", title: "Do the work", text: "Start and close requests with what was done." },
            { icon: "MdEventRepeat", title: "Prevent", text: "Preventive plans raise requests by run hours or counts." },
        ],
        sees: ["Overview, Floor, Machines", "Equipment and Work instructions (to look at)"],
        changes: ["Start and close maintenance requests, and raise them", "Preventive maintenance plans", "Code machine stops", "Answer andon calls", "Write handover notes"],
        agenda: [
            { icon: "MdSchool", title: "Basics", text: "Signing in, the inbox, the Overview." },
            { icon: "MdPrecisionManufacturing", title: "Machines", text: "The status board and stops." },
            { icon: "MdHandyman", title: "Requests", text: "Working requests and preventive plans." },
            { icon: "MdCampaign", title: "On the floor", text: "Andon calls, handover." },
        ],
        modules: ["basics", "overview", "machinesView", "maintenanceWork", "andonAnswer", "handover", "alerts"],
        quiz: [
            { q: "What's the difference between an andon call and a maintenance request?", short: "Andon or request?", a: "A call brings someone to the line now; a request is work to do and follow up until closed." },
            { q: "You close a request nobody started. What happens?", short: "Closing an unstarted request", a: "It is started at the same moment, by you, then closed." },
            { q: "A preventive plan's request is still open. Does its meter keep counting?", short: "Meter while a request is open", a: "No: it waits, and starts again from when the request is closed." },
            { q: "Where do run hours for a plan come from?", short: "Run hours", a: "The time the machine spent executing (PackML Execute), from its machine state." },
            { q: "What does *… quiet* on a machine tile mean?", short: "\"… quiet\"", a: "Its edge node hasn't been heard from for 2 minutes." },
        ],
        quickref: [
            ["Requests waiting", "**Inbox**, or Machines → **Maintenance**"],
            ["Start or close a request", "Machines → Maintenance → **Start** / **Close**"],
            ["Add a preventive plan", "Maintenance → **Preventive plans** → **Add a plan**"],
            ["Answer a call", "Operate → **Andon**, or the station → **I'm here**"],
            ["Give a stop its reason", "Machines → **Stops** → **Code**"],
            ["Leave word for the next shift", "Operate → **Handover**"],
        ],
        helpFrom: "Your maintenance lead first; your administrator for sign-in, equipment and edge nodes.",
    },
    {
        code: "administrator", name: "Administrator", icon: "MdAdminPanelSettings", tagline: "Set Pulaski up, and keep it running",
        summary: "You can do everything, at every site. Above all you look after who gets in and what they may do, the plant's equipment and how machine data reaches Pulaski, and how Pulaski itself is running. For each role's own tasks, use that role's course: this one covers what only administrators do.",
        day: [
            { icon: "MdPersonAdd", title: "People in", text: "Users, roles and sign-in from your organisation." },
            { icon: "MdFactory", title: "The plant", text: "Equipment, copied lines and sites, the setup checklist." },
            { icon: "MdSensors", title: "Machine data", text: "Profiles, edge nodes and tags." },
            { icon: "MdMonitorHeart", title: "Health", text: "System health, integrations, languages, the audit trail." },
        ],
        sees: ["Every screen, at every site, including **Users**"],
        changes: ["Everything, everywhere, including users, roles and permissions", "Equipment, machine profiles, edge nodes and tags", "Units", "Integrations: feeds, retrying or skipping deliveries", "Languages and translations"],
        agenda: [
            { icon: "MdSchool", title: "Basics", text: "Signing in, the menu, how Pulaski behaves." },
            { icon: "MdAdminPanelSettings", title: "Access", text: "Users, roles, permissions." },
            { icon: "MdFactory", title: "The plant", text: "Equipment, machine data, site settings." },
            { icon: "MdMonitorHeart", title: "Running it", text: "System health, integrations, audit." },
        ],
        modules: ["basics", "users", "equipment", "siteSettings", "machineData", "adminSystem", "audit", "bi"],
        quiz: [
            { q: "You create a user. How do they learn their password?", short: "A new user's password", a: "You tell them: Pulaski sends nothing. They change it at first sign-in." },
            { q: "Can you switch off a role someone holds?", short: "Switch off a held role?", a: "No: give them another role and take this one away first." },
            { q: "A user is locked out after five wrong tries. Fastest fix?", short: "Locked out", a: "Reset their password (Change → Reset password), which also unlocks them." },
            { q: "What user does an edge node sign in as?", short: "The edge's user", a: "A service account with the Edge node role at the site." },
            { q: "Where do you see a delivery feed failing?", short: "A failing feed", a: "Configure → System health (Delivery feeds), then Integrations to retry or skip." },
        ],
        quickref: [
            ["Add a user", "Configure → **Users** → **New user**"],
            ["Change what a role may do", "Users → **Roles** (or **Permissions**)"],
            ["Add a site, area or line", "Configure → **Equipment** → **Add child**"],
            ["Copy a line or start a site", "Equipment → **Copy this line** / **Start a new site…**"],
            ["Connect machines", "Configure → **Machine profiles**, **Edge nodes**"],
            ["See what's unhealthy", "Configure → **System health**"],
            ["ERP feeds and deliveries", "Configure → **Integrations**"],
            ["Translations", "Configure → **Languages**"],
            ["Who changed what", "Integrations → **Audit trail**"],
        ],
        helpFrom: "The person who runs Pulaski's servers and database; deploy/ and design/ in the repository.",
    },
];

module.exports = { roles, modules };
