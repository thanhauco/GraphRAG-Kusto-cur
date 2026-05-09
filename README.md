# GraphRAG Kusto + Neo4j

Full stack: **ASP.NET Core 8** API, **React (Vite)** UI, **Neo4j** schema graph, **Azure Data Explorer (Kusto)** queries, and **Azure OpenAI** for GraphRAG (natural language → graph-grounded KQL).

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Node.js 20+ (for the UI)
- Docker (optional, for Neo4j + compose)

## Quick start (local)

1. **Neo4j** (Docker):

   ```bash
   docker compose up neo4j -d
   ```

2. **API** — edit [backend/GraphRag.Api/appsettings.Development.json](backend/GraphRag.Api/appsettings.Development.json) (or environment variables) for Kusto AAD app credentials and Azure OpenAI:

   | Config key | Example env var |
   |------------|-----------------|
   | `GraphRag:KustoTenantId` | `GraphRag__KustoTenantId` |
   | `GraphRag:KustoClientId` | `GraphRag__KustoClientId` |
   | `GraphRag:KustoClientSecret` | `GraphRag__KustoClientSecret` |
   | `GraphRag:AzureOpenAiEndpoint` | `GraphRag__AzureOpenAiEndpoint` |
   | `GraphRag:AzureOpenAiApiKey` | `GraphRag__AzureOpenAiApiKey` |
   | `GraphRag:AzureOpenAiDeploymentName` | `GraphRag__AzureOpenAiDeploymentName` |

   ```bash
   cd backend/GraphRag.Api
   dotnet run
   ```

   Swagger: `http://localhost:8000/swagger`

3. **UI**:

   ```bash
   cd frontend
   npm install
   npm run dev
   ```

   Open `http://localhost:5173` (proxies `/api` to port 8000).

## Docker (API + Neo4j + UI)

```bash
docker compose up --build
```

- UI: `http://localhost:8080`
- API: `http://localhost:8000`
- Neo4j Browser: `http://localhost:7474` (`neo4j` / `graphrag-demo-pass`)

Set Kusto and Azure OpenAI via environment variables on the `backend` service (same `GraphRag__*` names as above).

## Security notes

- Kusto **client secrets** should not be checked in; use Key Vault or compose secrets for real environments.
- Production-friendly auth: set `GraphRag:KustoUseManagedIdentity` to `true` for Azure managed identity (optional `GraphRag:KustoManagedIdentityClientId` for user-assigned).
- `GraphRag:KustoClusterUriAllowlist` restricts cluster URIs accepted from the UI (SSRF mitigation).
- Demo **safe mode** (`DemoSafeMode`: true) requires `take`/`limit` and a time bound in KQL.

## Graph debugging & sampling

- After sync, `GET /api/graph/inspect?database=...` returns counts, `schema_hash`, and sample Cypher snippets for Neo4j Browser.
- Optional `GraphRag:SchemaSampleColumns` runs bounded `take N` samples per table during sync and stores `sample_distinct_estimate` on column nodes (see `SchemaSampleRowCap`).

## Neo4j Browser

```cypher
MATCH (d:Database)-[:HAS_TABLE]->(t:Table)-[:HAS_COLUMN]->(c:Column)
RETURN d.name, t.name, c.name LIMIT 80
```

## Demo data

See [demo/README.md](demo/README.md) and CSVs for an IcM-style story (incidents, alerts, deployments, services, components).
