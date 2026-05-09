# Three-minute demo script (speaker notes)

**Goal:** Show why graph-grounded schema context beats flat NL→KQL or doc-only RAG for multi-hop operational questions.

## Setup (before recording)

1. Ingest demo CSVs into ADX using `INGEST.kql` as a guide (or use your own cluster with matching table names).
2. Run Neo4j (`docker compose up neo4j -d`), configure API `GraphRag:*` and optional `SchemaSampleColumns`.
3. Start API + UI; open the browser UI.

## Minute 1 — Trust but verify Kusto

- Enter cluster URI, database, and AAD app credentials (or use managed identity in production).
- Click **Test connection**, then **Load databases** (pick DB from list) and **Load tables** with lazy column expansion.
- Run a bounded `take` + `ago()` query in the KQL editor to prove raw execution path.

## Minute 2 — Schema as a graph

- Click **Sync schema to graph**; note `schema_hash` and optionally **Inspect graph** (`GET /api/graph/inspect`) or Neo4j Browser with README Cypher.
- Explain inferred `RELATES_TO` edges from shared `*Id` columns (no FK metadata in Kusto).

## Minute 3 — GraphRAG

- Use a demo NL chip or type an OSOC-style question (deployments ↔ incidents ↔ services).
- Generate KQL; point at **retrieval_source** (`neo4j_keywords` vs fallback catalog) and **graph_node_refs**.
- Optional: **Execute generated KQL** when safe mode passes; contrast with how hard the join story would be without subgraph context.

**Closing line:** “The LLM only sees tables and paths we actually have in Neo4j today—so joins and time columns stay grounded when schema drifts.”
