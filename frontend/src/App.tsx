import CodeMirror from "@uiw/react-codemirror";
import { sql } from "@codemirror/lang-sql";
import { oneDark } from "@codemirror/theme-one-dark";
import { useCallback, useEffect, useMemo, useState } from "react";

const LS_KEY = "graphrag_kusto_conn_v1";

type Conn = {
  clusterUri: string;
  database: string;
  tenantId: string;
  clientId: string;
  clientSecret: string;
};

type ColumnInfo = {
  name: string;
  data_type: string;
  sample_distinct_estimate?: number | null;
};

type TableSummary = {
  name: string;
  folder: string;
  doc_string: string;
};

type RagQueryResponse = {
  reasoning: string;
  kql: string;
  assumptions: string[];
  graph_node_refs: string[];
  execute_result?: {
    columns: string[];
    rows: unknown[][];
    truncated: boolean;
    error_detail?: string | null;
  } | null;
  retrieval_tables: string[];
  retrieval_source: string;
};

type DemoSamplesPayload = {
  nl_questions: string[];
  paired: Array<{
    title: string;
    nl: string;
    graphrag_note: string;
    kql_hint: string;
  }>;
};

async function apiJson<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(path, {
    ...init,
    headers: { "Content-Type": "application/json", ...init?.headers },
  });
  const text = await res.text();
  if (!res.ok) {
    let msg = text.trim() || res.statusText || "Request failed";
    try {
      const j = JSON.parse(text) as {
        title?: string;
        detail?: string;
        message?: string;
        extensions?: { code?: string };
      };
      msg = j.detail ?? j.title ?? j.message ?? msg;
      if (j.extensions?.code) msg = `${msg} [${j.extensions.code}]`;
    } catch {
      /* plain text */
    }
    throw new Error(msg);
  }
  return text ? (JSON.parse(text) as T) : ({} as T);
}

async function copyText(text: string) {
  await navigator.clipboard.writeText(text);
}

export default function App() {
  const [conn, setConn] = useState<Conn>(() => {
    try {
      const raw = localStorage.getItem(LS_KEY);
      if (!raw) throw new Error("empty");
      const j = JSON.parse(raw) as Partial<Conn>;
      return {
        clusterUri: j.clusterUri ?? "",
        database: j.database ?? "",
        tenantId: j.tenantId ?? "",
        clientId: j.clientId ?? "",
        clientSecret: "",
      };
    } catch {
      return {
        clusterUri: "https://help.kusto.windows.net/",
        database: "Samples",
        tenantId: "",
        clientId: "",
        clientSecret: "",
      };
    }
  });

  const persistConn = useCallback((c: Conn) => {
    const { clientSecret: _, ...rest } = c;
    localStorage.setItem(LS_KEY, JSON.stringify(rest));
  }, []);

  const [validateMsg, setValidateMsg] = useState<string | null>(null);
  const [validateOk, setValidateOk] = useState<boolean | null>(null);
  const [databases, setDatabases] = useState<string[]>([]);
  const [databasesErr, setDatabasesErr] = useState<string | null>(null);
  const [loadingDatabases, setLoadingDatabases] = useState(false);

  const [tables, setTables] = useState<TableSummary[]>([]);
  const [tableColumns, setTableColumns] = useState<
    Record<string, { cols?: ColumnInfo[]; loading?: boolean; err?: string | null }>
  >({});
  const [tablesErr, setTablesErr] = useState<string | null>(null);
  const [loadingTables, setLoadingTables] = useState(false);

  const [kql, setKql] = useState(
    "StormEvents | where StartTime > ago(7d) | take 50"
  );
  const [resultView, setResultView] = useState<"table" | "json">("table");
  const [result, setResult] = useState<{
    columns: string[];
    rows: unknown[][];
    truncated: boolean;
    error_detail?: string;
  } | null>(null);

  const [syncMsg, setSyncMsg] = useState<string | null>(null);
  const [inspectOut, setInspectOut] = useState<string | null>(null);

  const [nlQuestion, setNlQuestion] = useState(
    "Which states had storm events in the last week?"
  );
  const [executeRagKql, setExecuteRagKql] = useState(false);
  const [ragOut, setRagOut] = useState<RagQueryResponse | null>(null);
  const [ragRaw, setRagRaw] = useState<string | null>(null);
  const [ragLoading, setRagLoading] = useState(false);

  const [demoSamples, setDemoSamples] = useState<DemoSamplesPayload | null>(null);

  const authBody = useMemo(
    () => ({
      cluster_uri: conn.clusterUri,
      database: conn.database,
      tenant_id: conn.tenantId || null,
      client_id: conn.clientId || null,
      client_secret: conn.clientSecret || null,
    }),
    [conn]
  );

  const qsConn = useMemo(() => {
    const qs = new URLSearchParams({
      cluster_uri: conn.clusterUri,
    });
    if (conn.tenantId) qs.set("tenant_id", conn.tenantId);
    if (conn.clientId) qs.set("client_id", conn.clientId);
    if (conn.clientSecret) qs.set("client_secret", conn.clientSecret);
    return qs;
  }, [conn]);

  useEffect(() => {
    const id = setTimeout(() => persistConn(conn), 300);
    return () => clearTimeout(id);
  }, [conn, persistConn]);

  useEffect(() => {
    void (async () => {
      try {
        const s = await apiJson<DemoSamplesPayload>("/api/demo/samples");
        setDemoSamples(s);
      } catch {
        setDemoSamples(null);
      }
    })();
  }, []);

  const testConnection = async () => {
    setValidateMsg(null);
    setValidateOk(null);
    try {
      const r = await apiJson<{
        ok: boolean;
        message: string;
        latency_ms: number;
        databases_sample?: string[];
      }>("/api/kusto/validate", {
        method: "POST",
        body: JSON.stringify(authBody),
      });
      setValidateOk(r.ok);
      setValidateMsg(`${r.message} (${r.latency_ms.toFixed(0)} ms)`);
      if (r.databases_sample?.length)
        setDatabases((d) => (d.length ? d : r.databases_sample!));
    } catch (e) {
      setValidateOk(false);
      setValidateMsg(e instanceof Error ? e.message : String(e));
    }
  };

  const loadDatabases = async () => {
    setDatabasesErr(null);
    setLoadingDatabases(true);
    try {
      const qs = new URLSearchParams(qsConn);
      if (conn.database.trim())
        qs.set("database", conn.database.trim());
      const list = await apiJson<string[]>(`/api/kusto/databases?${qs}`);
      setDatabases(list);
    } catch (e) {
      setDatabasesErr(e instanceof Error ? e.message : String(e));
      setDatabases([]);
    } finally {
      setLoadingDatabases(false);
    }
  };

  const loadTables = async () => {
    setTablesErr(null);
    setLoadingTables(true);
    setTableColumns({});
    try {
      const qs = new URLSearchParams(qsConn);
      qs.set("include_columns", "false");
      const list = await apiJson<TableSummary[]>(
        `/api/kusto/databases/${encodeURIComponent(conn.database)}/tables?${qs}`
      );
      setTables(list);
    } catch (e) {
      setTablesErr(e instanceof Error ? e.message : String(e));
      setTables([]);
    } finally {
      setLoadingTables(false);
    }
  };

  const toggleTableColumns = async (tableName: string) => {
    let collapsed = false;
    setTableColumns((m) => {
      const cur = m[tableName];
      if (cur?.cols) {
        collapsed = true;
        const next = { ...m };
        delete next[tableName];
        return next;
      }
      return { ...m, [tableName]: { loading: true, err: null } };
    });
    if (collapsed) return;

    try {
      const qs = new URLSearchParams(qsConn);
      const cols = await apiJson<ColumnInfo[]>(
        `/api/kusto/databases/${encodeURIComponent(conn.database)}/tables/${encodeURIComponent(tableName)}/columns?${qs}`
      );
      setTableColumns((m) => {
        if (!(tableName in m)) return m;
        return { ...m, [tableName]: { cols, loading: false, err: null } };
      });
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      setTableColumns((m) => {
        if (!(tableName in m)) return m;
        return { ...m, [tableName]: { loading: false, err: msg } };
      });
    }
  };

  const runKql = async () => {
    setResult(null);
    try {
      const r = await apiJson<typeof result>("/api/kusto/query", {
        method: "POST",
        body: JSON.stringify({ ...authBody, kql }),
      });
      setResult(r);
    } catch (e) {
      setResult({
        columns: [],
        rows: [],
        truncated: false,
        error_detail: e instanceof Error ? e.message : String(e),
      });
    }
  };

  const syncGraph = async () => {
    setSyncMsg(null);
    try {
      const r = await apiJson<{
        schemaHash?: string;
        schema_hash?: string;
        message?: string;
        summary?: { tableCount?: number; table_count?: number };
      }>("/api/graph/sync-schema", { method: "POST", body: JSON.stringify(authBody) });
      const hash = r.schemaHash ?? r.schema_hash ?? "?";
      const tc = r.summary?.tableCount ?? r.summary?.table_count ?? "?";
      setSyncMsg(`Synced. schema_hash=${hash} tables=${tc} ${r.message ?? ""}`);
    } catch (e) {
      setSyncMsg(e instanceof Error ? e.message : String(e));
    }
  };

  const inspectGraph = async () => {
    setInspectOut(null);
    try {
      const r = await apiJson<Record<string, unknown>>(
        `/api/graph/inspect?database=${encodeURIComponent(conn.database)}`
      );
      setInspectOut(JSON.stringify(r, null, 2));
    } catch (e) {
      setInspectOut(e instanceof Error ? e.message : String(e));
    }
  };

  const runRag = async () => {
    setRagOut(null);
    setRagRaw(null);
    setRagLoading(true);
    try {
      const r = await apiJson<RagQueryResponse>("/api/rag/query", {
        method: "POST",
        body: JSON.stringify({
          question: nlQuestion,
          cluster_uri: conn.clusterUri,
          database: conn.database,
          execute_kql: executeRagKql,
          tenant_id: conn.tenantId || null,
          client_id: conn.clientId || null,
          client_secret: conn.clientSecret || null,
        }),
      });
      setRagOut(r);
      setRagRaw(JSON.stringify(r, null, 2));
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      setRagRaw(msg);
    } finally {
      setRagLoading(false);
    }
  };

  const applyGeneratedKql = () => {
    if (ragOut?.kql) setKql(ragOut.kql);
  };

  return (
    <div className="app">
      <h1>GraphRAG + Kusto (Neo4j)</h1>
      <p className="small">
        ASP.NET Core API. Credentials stay on the server when configured in{" "}
        <code>appsettings</code>; the form can override for local demos.
      </p>

      {demoSamples && (
        <div className="panel" style={{ marginBottom: "1rem" }}>
          <h2>Demo samples</h2>
          <p className="small">
            NL chips from <code>/api/demo/samples</code> (IcM-style story after CSV ingest).
          </p>
          <div className="demo-chips">
            {demoSamples.nl_questions.map((q) => (
              <button key={q} type="button" className="secondary" onClick={() => setNlQuestion(q)}>
                {q.length > 72 ? `${q.slice(0, 72)}…` : q}
              </button>
            ))}
          </div>
          {demoSamples.paired.map((p) => (
            <div key={p.title} className="paired-card">
              <strong>{p.title}</strong>
              <p className="small">{p.nl}</p>
              <p className="small">
                <em>GraphRAG:</em> {p.graphrag_note}
              </p>
              <p className="small">
                <em>Flat / KQL hint:</em> {p.kql_hint}
              </p>
              <button type="button" className="secondary" onClick={() => setNlQuestion(p.nl)}>
                Use this question
              </button>
            </div>
          ))}
        </div>
      )}

      <div className="grid grid-2">
        <div className="panel">
          <h2>Kusto connection</h2>
          <label>Cluster URI</label>
          <input
            value={conn.clusterUri}
            onChange={(e) =>
              setConn((c) => ({ ...c, clusterUri: e.target.value }))
            }
          />
          <label>
            Database{" "}
            <span className="small">
              (datalist from <strong>Load databases</strong>)
            </span>
          </label>
          <input
            list="db-options"
            value={conn.database}
            onChange={(e) =>
              setConn((c) => ({ ...c, database: e.target.value }))
            }
          />
          <datalist id="db-options">
            {databases.map((d) => (
              <option key={d} value={d} />
            ))}
          </datalist>
          <label>Tenant ID</label>
          <input
            value={conn.tenantId}
            onChange={(e) =>
              setConn((c) => ({ ...c, tenantId: e.target.value }))
            }
          />
          <label>Client ID (AAD app)</label>
          <input
            value={conn.clientId}
            onChange={(e) =>
              setConn((c) => ({ ...c, clientId: e.target.value }))
            }
          />
          <label>Client secret (not stored in browser)</label>
          <input
            type="password"
            autoComplete="off"
            value={conn.clientSecret}
            onChange={(e) =>
              setConn((c) => ({ ...c, clientSecret: e.target.value }))
            }
          />
          <div className="row-actions">
            <button type="button" onClick={testConnection}>
              Test connection
            </button>
            <button
              type="button"
              className="secondary"
              onClick={loadDatabases}
              disabled={loadingDatabases}
            >
              {loadingDatabases ? "Loading…" : "Load databases"}
            </button>
            <button type="button" className="secondary" onClick={loadTables} disabled={loadingTables}>
              {loadingTables ? "Loading…" : "Load tables (lazy columns)"}
            </button>
          </div>
          {validateMsg && (
            <div className={`banner ${validateOk ? "ok" : "err"}`}>
              {validateMsg}
            </div>
          )}
          {databasesErr && <div className="banner err">{databasesErr}</div>}
          {tablesErr && <div className="banner err">{tablesErr}</div>}
          <ul className="tables">
            {tables.map((t) => {
              const meta = tableColumns[t.name];
              const expanded = Boolean(meta);
              return (
                <li key={t.name}>
                  <div onClick={() => void toggleTableColumns(t.name)} role="presentation">
                    <strong>{expanded ? "▼" : "▶"} {t.name}</strong>
                    <span className="small">
                      {" "}
                      ({t.folder})
                    </span>
                  </div>
                  {meta?.loading && <div className="small">Loading columns…</div>}
                  {meta?.err && <div className="banner err">{meta.err}</div>}
                  {meta?.cols && (
                    <ul className="small" style={{ margin: "0.25rem 0 0 1rem" }}>
                      {meta.cols.map((c) => (
                        <li key={c.name}>
                          {c.name}{" "}
                          <span className="small">
                            ({c.data_type}
                            {typeof c.sample_distinct_estimate === "number"
                              ? ` ~${c.sample_distinct_estimate} distinct in sample`
                              : ""}
                            )
                          </span>
                        </li>
                      ))}
                    </ul>
                  )}
                </li>
              );
            })}
          </ul>
        </div>

        <div className="panel">
          <h2>KQL</h2>
          <p className="small">
            Safe mode requires <code>take|limit</code> and a time filter (
            <code>ago()</code> etc.).
          </p>
          <CodeMirror
            value={kql}
            height="200px"
            theme={oneDark}
            extensions={[sql()]}
            onChange={(v) => setKql(v)}
          />
          <div className="row-actions">
            <button type="button" onClick={runKql}>
              Run KQL
            </button>
            <button
              type="button"
              className="secondary"
              onClick={() =>
                setResultView((v) => (v === "table" ? "json" : "table"))
              }
            >
              Results: {resultView === "table" ? "table" : "JSON"}
            </button>
          </div>
          {result && (
            <div className="table-wrap">
              {result.error_detail ? (
                <p className="banner err">{result.error_detail}</p>
              ) : resultView === "json" ? (
                <pre className="small" style={{ margin: 0, padding: "0.5rem" }}>
                  {JSON.stringify(
                    {
                      columns: result.columns,
                      rows: result.rows,
                      truncated: result.truncated,
                    },
                    null,
                    2
                  )}
                </pre>
              ) : (
                <table>
                  <thead>
                    <tr>
                      {result.columns.map((c) => (
                        <th key={c}>{c}</th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {result.rows.map((row, i) => (
                      <tr key={i}>
                        {row.map((cell, j) => (
                          <td key={j}>{String(cell)}</td>
                        ))}
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
              {result.truncated && (
                <p className="small">Results may be truncated.</p>
              )}
            </div>
          )}
        </div>
      </div>

      <div className="grid grid-2" style={{ marginTop: "1rem" }}>
        <div className="panel">
          <h2>Neo4j schema sync</h2>
          <div className="row-actions">
            <button type="button" onClick={syncGraph}>
              Sync schema to graph
            </button>
            <button type="button" className="secondary" onClick={inspectGraph}>
              Inspect graph (Neo4j)
            </button>
          </div>
          {syncMsg && <div className="banner ok">{syncMsg}</div>}
          {inspectOut && (
            <pre
              className="table-wrap small"
              style={{ padding: "0.5rem", maxHeight: 240 }}
            >
              {inspectOut}
            </pre>
          )}
        </div>
        <div className="panel">
          <h2>GraphRAG (NL → KQL)</h2>
          <p className="small">
            Requires Azure OpenAI in API configuration. Enable execute to run generated KQL
            subject to safe mode.
          </p>
          <textarea
            value={nlQuestion}
            onChange={(e) => setNlQuestion(e.target.value)}
            rows={4}
          />
          <div className="inline-row" style={{ marginTop: "0.35rem" }}>
            <label className="inline-row">
              <input
                type="checkbox"
                checked={executeRagKql}
                onChange={(e) => setExecuteRagKql(e.target.checked)}
              />
              Execute generated KQL (same request)
            </label>
          </div>
          <div className="row-actions">
            <button type="button" disabled={ragLoading} onClick={runRag}>
              {ragLoading ? "…" : "Generate"}
            </button>
            <button
              type="button"
              className="secondary"
              disabled={!ragOut?.kql}
              onClick={() => ragOut && void copyText(ragOut.kql)}
            >
              Copy KQL
            </button>
            <button
              type="button"
              className="secondary"
              disabled={!ragOut?.kql}
              onClick={applyGeneratedKql}
            >
              Apply KQL to editor
            </button>
            <button
              type="button"
              className="secondary"
              disabled={!ragRaw}
              onClick={() => ragRaw && void copyText(ragRaw)}
            >
              Copy JSON
            </button>
          </div>
          {ragOut && (
            <div style={{ marginTop: "0.75rem" }}>
              <p className="small">
                <strong>Retrieval:</strong> {ragOut.retrieval_source} — tables:{" "}
                {ragOut.retrieval_tables.join(", ")}
              </p>
              <p className="small">
                <strong>Refs:</strong> {ragOut.graph_node_refs.join("; ") || "—"}
              </p>
              <pre className="table-wrap small" style={{ padding: "0.5rem" }}>
                {ragRaw}
              </pre>
              {ragOut.execute_result && (
                <div style={{ marginTop: "0.5rem" }}>
                  <strong className="small">Execute result</strong>
                  {ragOut.execute_result.error_detail ? (
                    <div className="banner err">{ragOut.execute_result.error_detail}</div>
                  ) : (
                    <div className="table-wrap" style={{ maxHeight: 200 }}>
                      <table>
                        <thead>
                          <tr>
                            {ragOut.execute_result.columns.map((c) => (
                              <th key={c}>{c}</th>
                            ))}
                          </tr>
                        </thead>
                        <tbody>
                          {ragOut.execute_result.rows.map((row, i) => (
                            <tr key={i}>
                              {row.map((cell, j) => (
                                <td key={j}>{String(cell)}</td>
                              ))}
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                </div>
              )}
            </div>
          )}
          {!ragOut && ragRaw && !ragLoading && (
            <pre className="banner err" style={{ whiteSpace: "pre-wrap" }}>
              {ragRaw}
            </pre>
          )}
        </div>
      </div>
    </div>
  );
}
