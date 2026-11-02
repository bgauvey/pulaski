# Pulaski — Project Charter

Oct 11, 2026 · Bill Gauvey

## Purpose

Pulaski is an open-source MES for process and semi-continuous manufacturing lines. It gives mid-market plants what they pay commercial vendors for today: machine-driven production and downtime capture, lot genealogy through continuous flow, and real-time quality.

- **Codename.** Pulaski is a working name. A formal trademark search happens before any public release, and the name lives in one place so a rename stays cheap.
- **Independence.** Pulaski is a personal project with no corporate backing, sponsorship or affiliation, now or later. It is built clean-room: no employer code, schemas, data or documents are used.

## Market gap

The leading open-source options either serve discrete job shops or try to replace the ERP. None is built around continuous lines and machine data.

| Project | Focus | Stack | License | Gap for process plants |
| --- | --- | --- | --- | --- |
| [OpenMES](https://github.com/Mes-Open/OpenMes) | MES for small discrete shops: woodworking, metal, plastics, assembly | Laravel (PHP), PostgreSQL | AGPL-3.0 core, AFL-3.0 modules, commercial option | Self-declared gaps: no SPC or Cpk, no runtime-based maintenance, no B2MML; ERP data mainly via CSV import |
| [Carbon](https://github.com/crbnos/carbon) | ERP, MES and QMS suite for complex assembly and configure-to-order | TypeScript, Supabase, Vercel-oriented services | AGPL-3.0, commercial option | Replaces the ERP rather than sitting under it; machine data and OEE are not featured |
| Commercial suites (Opcenter, Plex, AVEVA, Critical Manufacturing) | Full-breadth MES | Proprietary | Per-site or per-user fees | Cost and implementation effort exclude most mid-market plants |

Open-source details reviewed October 2026 from each project's repository; see [OpenMES ISA-95 coverage](https://github.com/Mes-Open/OpenMes/blob/main/docs/isa95.md).

## Wedge and target users

Pulaski targets mid-market process and semi-continuous plants (roughly 50 to 500 people, one to several sites) that already run an ERP and need execution visibility beneath it.

- **Industries:** extrusion, injection and blow molding, compounding, and high-rate packaging lines.
- **Users:** operators, supervisors, quality technicians, maintenance, and plant managers.
- **Buyers and deployers:** plant IT and the systems integrators who implement MES. Integrators are the main distribution channel.
- **Differentiators:** machine-data-first capture, genealogy through continuous flow, and real-time SPC, the three things no current open option does well.
- **Design partners:** two or three plants or integrators, chosen to avoid any conflict with the founder's existing employment commitments.

## V1 scope

Version 1 covers ten capabilities, all centred on running lines rather than discrete routings.

1. **Equipment model:** ISA-95 hierarchy (enterprise, site, area, line, work unit) with machine tags mapped to each unit.
2. **Machine data ingestion:** MQTT Sparkplug B and OPC UA as first-party connectors, with store-and-forward at the edge.
3. **Automatic state and downtime:** machine states derived from tags, micro-stop capture, and operator reason coding.
4. **Production tracking:** runs against ERP work orders, with good and scrap counts taken from machine counters.
5. **Continuous-flow genealogy:** material lots consumed into running lines by time window, output lots cut by time or quantity, splits and merges, and forward and backward trace.
6. **OEE:** availability, performance and quality by line, shift and product.
7. **Real-time SPC:** control charts (X-bar and R, I-MR, p) on process variables and inspections, with rule violations raised as alerts.
8. **ERP integration:** REST API plus outbound events and webhooks, with B2MML-aligned payloads for work orders and production performance.
9. **Operator UI:** tablet-first, shift-aware screens for operators and supervisors.
10. **Audit trail:** every change recorded in an append-only event log.

## Non-goals

Pulaski v1 deliberately leaves these out, so the core stays sharp and fits beside the systems plants already own.

- **Not an ERP:** no accounting, purchasing, sales orders or MRP. Pulaski sits under the ERP.
- **Not a job-shop MES:** discrete multi-step routings are not the primary use case.
- **Not SCADA, HMI or a historian:** Pulaski integrates with them and keeps only the process data its own functions need.
- **No finite-capacity scheduling** in v1.
- **No regulated-industry features** (electronic signatures, Part 11 controls) in v1. The event model is designed so they can be added without rework.
- **No hosted multi-tenant SaaS** in v1. Self-hosted only.

## Architecture

Pulaski is event-first: machine signals become production events at the edge, and production, genealogy, OEE and SPC are all projections of one append-only log.

```
Plant floor (PLCs, OPC UA servers, MQTT / Sparkplug B)
        |
        v
Pulaski Edge   - protocol connectors, machine state detection, store-and-forward
        |  production events
        v
Pulaski Core   - append-only production event log
                 -> production & runs, lot genealogy, OEE and downtime, real-time SPC
                 -> API, events and webhooks (B2MML-aligned payloads)
        |
        v
Operator and supervisor UI  |  ERP (SAP, D365, Epicor)  |  BI and analytics
```

The edge node keeps a line running and recording through network loss; the core rebuilds every view from the event log.

**Decisions that are expensive to reverse**

- **Platform:** C# on the current .NET LTS release, ASP.NET Core for APIs, a worker service for the edge node.
- **Database:** PostgreSQL only, for relational and time-series data alike. High-volume tables use native time partitioning, BRIN indexes and worker-maintained rollups; retention drops old partitions. No TimescaleDB and no SQL Server.
- **Event model:** append-only production events with ISA-95 identifiers; state, OEE and genealogy are derived and can be rebuilt.
- **Edge resilience:** the edge node runs on plant hardware, buffers locally and forwards when the link returns.
- **Extensibility:** a connector SDK; Sparkplug B and OPC UA are the first two connectors, built on the same SDK others use.
- **Identity:** OpenID Connect, so plants can use Entra ID, Active Directory or Keycloak.
- **Deployment:** containers for the demo and for Linux sites; a Windows service option for Windows-only plants.

## First vertical slice

The first milestone is one simulated extrusion line running end to end, installable with a single command and showing value in under 15 minutes.

1. A line simulator publishes state, counts and process values over MQTT Sparkplug B.
2. The edge node ingests them, derives running, idle and down states, and buffers through a network drop.
3. A work order arrives through the API, and the run is tracked against it.
4. Resin lots are consumed into the line by time window; output lots are cut by quantity.
5. An operator codes a downtime reason on a tablet screen.
6. OEE and one live SPC chart (melt temperature or profile dimension) update in real time.
7. A forward and backward trace returns the right lots.
8. Production performance is posted back out as an ERP-ready event.

**Success criteria**

- [ ] `docker compose up` to working demo in 15 minutes or less on a laptop
- [ ] No data lost across a simulated 10-minute network outage
- [ ] Genealogy trace correct across a lot change mid-run
- [ ] At least one design partner runs the slice against real line data

## License, governance and business model

Decided: Apache 2.0 with DCO sign-off. It maximizes adoption by integrators and enterprises; the registered product name, not the license, protects the project's identity. The options considered are kept below for the record.

| Option | Adoption | Protection against resale | Fits |
| --- | --- | --- | --- |
| Apache 2.0 | Highest; integrators and enterprises accept it readily | None; anyone can host or fork closed | Revenue from support, hosting and certified connectors |
| AGPL-3.0 core, permissive connectors, commercial license | Some enterprise legal teams hesitate | Strong; hosted forks must share source | Dual licensing, as OpenMES and Carbon do |
| Open core | High for the core | Premium features stay closed | Paid multi-site, validation packs or advanced scheduling |

- **Contributions:** Developer Certificate of Origin (DCO); no CLA.
- **Governance:** founder-led benevolent-dictator model until outside maintainers emerge; revisit at 5 or more regular contributors.
- **Connectors:** published under Apache 2.0, so integrators and vendors can ship their own.

## Risks

The biggest risk is scope: a single maintainer trying to match commercial breadth.

| Risk | Mitigation |
| --- | --- |
| Scope creep toward full-suite MES | Non-goals above are binding for v1; new scope needs a design-partner request |
| Single maintainer, limited time | Vertical slices only; connector SDK so others extend it; recruit a second maintainer early |
| Genealogy model wrong for real continuous lines | Validate with design-partner data before the API is published as stable |
| Name conflict on public launch | Codename until a trademark search clears the final name |
| Employment conflict | Clean-room build, personal time and hardware, design partners chosen to avoid competitive overlap |
| OpenMES or Carbon move into process manufacturing | Ship the slice early; edge resilience and continuous genealogy are hard to retrofit into their architectures |

## Open decisions and next steps

One decision remains open; the license, storage and UI framework are settled.

**Open decisions**

- [x] License model: decided, Apache 2.0 with DCO
- [x] Operator UI framework: decided, Blazor (D-011)
- [x] Time-series storage: decided, plain PostgreSQL with native partitioning
- [ ] Final product name, after a trademark search

**Next steps**

- [ ] Draft the core domain model (in progress — see `domain-model-decisions.md`)
- [ ] Create the repository, solution layout and CI pipeline under the Pulaski codename
- [ ] Build the line simulator for the first slice
- [ ] Approach two or three candidate design partners
