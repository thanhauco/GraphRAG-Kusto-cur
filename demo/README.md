# Demo dataset (IcM / OSOC style)

Synthetic CSVs model **Incidents**, **Alerts**, **Deployments**, **Services**, and **Components** with shared keys suitable for graph inference (`IncidentId`, `CorrelationId`, `ServiceId`, `ComponentId`, etc.).

## Files

| File | Role |
|------|------|
| `components.csv` | Component catalog |
| `services.csv` | Service → component mapping |
| `incidents.csv` | Incident rows with timestamps |
| `alerts.csv` | Alerts linked by correlation / service |
| `deployments.csv` | Deployments touching services |

## Ingest to ADX (outline)

1. Create a database and tables matching the CSV schemas (or use one [serialization](https://learn.microsoft.com/en-us/azure/data-explorer/create-table-wizard) from sample row).
2. Ingest via queued ingest, `.ingest into table ...`, or Kusto Web UI ingestion.

After ingest, use the app **Sync schema to graph**, then try the built-in NL samples from `GET /api/demo/samples`.

## Story for demos

- **Multi-hop**: incidents and alerts tied through `CorrelationId` and `ServiceId`; deployments link to services — GraphRAG subgraph surfaces join paths before KQL is generated.
- **Blast radius**: follow `ComponentId` across services → incidents.
