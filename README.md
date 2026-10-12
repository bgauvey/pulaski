# Pulaski

**An open-source Manufacturing Execution System (MES) for process and semi-continuous manufacturing lines.**

> **Status: pre-alpha.** Pulaski is in early design and development. Nothing here is production-ready yet, and APIs will change.
>
> *Pulaski is a working codename. The final product name will be announced before the first public release.*

---

## What is Pulaski?

Pulaski gives mid-market plants the shop-floor capabilities they usually pay commercial MES vendors for:

- **Machine-driven production and downtime capture.** States, counts, and micro-stops come from the machines, not from clipboards.
- **Lot genealogy through continuous flow.** Trace material forward and backward through running lines, lot changes, splits, and merges.
- **Real-time quality.** Statistical process control on live process data, with alerts when a process drifts.

It is built for extrusion, molding, compounding, packaging, and other continuous or high-rate lines. It sits **beneath your existing ERP**, not in place of it.

## Planned v1 capabilities

| Area | Capability |
| --- | --- |
| Equipment | ISA-95 hierarchy (enterprise, site, area, line, work unit) with machine tag mapping |
| Machine data | MQTT Sparkplug B and OPC UA connectors with edge store-and-forward |
| Downtime | Automatic machine state detection, micro-stops, operator reason coding |
| Production | Runs tracked against ERP work orders, good and scrap counts from machine counters |
| Genealogy | Time-window lot consumption, output lot boundaries, splits and merges, forward and backward trace |
| Performance | OEE by line, shift, and product |
| Quality | Real-time SPC charts (X-bar and R, I-MR, p) with rule-violation alerts |
| Integration | REST API, outbound events and webhooks, B2MML-aligned payloads |
| Operator UI | Tablet-first, shift-aware screens |
| Audit | Append-only production event log |

### Not in scope for v1

Pulaski is not an ERP (no accounting, purchasing, or MRP), not a SCADA/HMI or historian, and does not yet include finite scheduling or regulated-industry features such as electronic signatures.

## Architecture at a glance

```
Plant floor (PLCs, OPC UA servers, MQTT / Sparkplug B)
        │
        ▼
Pulaski Edge   — protocol connectors, machine state detection, store-and-forward
        │  production events
        ▼
Pulaski Core   — append-only event log
                 → production & runs, lot genealogy, OEE, SPC
                 → API, events, webhooks
        │
        ▼
Operator UI · ERP · BI and analytics
```

- **Event-first.** Every machine signal becomes a production event. Production, genealogy, OEE, and SPC are projections that can be rebuilt from the log.
- **Edge-resilient.** The edge node keeps recording when the network drops and forwards when it returns.
- **.NET.** Built in C# on the current .NET LTS release, with PostgreSQL and SQL Server support.
- **Extensible.** A connector SDK lets anyone add protocols and integrations.

## Getting started

There is nothing to install yet. The first milestone is a single-command demo: a simulated extrusion line running end to end, from machine signals to OEE, SPC, and genealogy.

Watch or star the repository to follow progress.

## Contributing

Contributions, ideas, and feedback from manufacturing and controls engineers are very welcome, especially on the domain model and genealogy for continuous processes.

- Open an issue to discuss significant changes before starting work.
- All commits must be signed off under the [Developer Certificate of Origin](https://developercertificate.org/). Use `git commit -s`.

## License

Pulaski is licensed under the [Apache License, Version 2.0](LICENSE.md). See [NOTICE](NOTICE) for attribution.

Pulaski is an independent project and is not affiliated with, sponsored by, or endorsed by any employer or company.

