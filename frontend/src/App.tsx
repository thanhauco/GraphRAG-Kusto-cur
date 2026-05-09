import { useCallback, useEffect, useMemo, useState } from "react";

const LS_KEY = "graphrag_kusto_conn_v1";

type Conn = {
  clusterUri: string;
  database: string;
  tenantId: string;
  clientId: string;
  clientSecret: string;
};

type TableInfo = {
  name: string;
  folder: string;
  doc_string: string;
  columns: { name: string; data_type: string }[];
  time_columns: string[];
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
      const j = JSON.parse(text) as { title?: string; detail?: string; message?: string };
      msg = j.detail ?? j.title ?? j.message ?? msg;
    } catch {
      /* plain text body from API */
    }
    throw new Error(msg);
  }
  return text ? (JSON.parse(text) as T) : ({} as T);
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
  const [tables, setTables] = useState<TableInfo[]>([]);
  const [tablesErr, setTablesErr] = useState<string | null>(null);
  const [loadingTables, setLoadingTables] = useState(false);

  const [kql, setKql] = useState(
    "StormEvents | where StartTime > ago(7d) | take 50"
  );
  const [result, setResult] = useState<{
    columns: string[];
    rows: unknown[][];
    truncated: boolean;
    error_detail?: string;
  } | null>(null);

  const [syncMsg, setSyncMsg] = useState<string | null>(null);
  const [nlQuestion, setNlQuestion] = useState(
    "Which states had storm events in the last week?"
  );
  const [ragOut, setRagOut] = useState<string | null>(null);
  const [ragLoading, setRagLoading] = useState(false);

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

  useEffect(() => {
    const id = setTimeout(() => persistConn(conn), 300);
    return () => clearTimeout(id);
  }, [conn, persistConn]);

  const testConnection = async () => {
    setValidateMsg(null);
    setValidateOk(null);
    try {
      const r = await apiJson<{
        ok: boolean;
        message: string;
        latency_ms: number;
      }>("/api/kusto/validate", {
        method: "POST",
        body: JSON.stringify(authBody),
      });
      setValidateOk(r.ok);
      setValidateMsg(
        `${r.message} (${r.latency_ms.toFixed(0)} ms)`
      );
    } catch (e) {
      setValidateOk(false);
      setValidateMsg(e instanceof Error ? e.message : String(e));
    }
  };

  const loadTables = async () => {
    setTablesErr(null);
    setLoadingTables(true);
    try {
      const qs = new URLSearchParams({
        cluster_uri: conn.clusterUri,
        database: conn.database,
      });
      if (conn.tenantId) qs.set("tenant_id", conn.tenantId);
      if (conn.clientId) qs.set("client_id", conn.clientId);
      if (conn.clientSecret) qs.set("client_secret", conn.clientSecret);
      const list = await apiJson<TableInfo[]>(
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
      const r = await apiJson<{ schema_hash: string; message: string }>(
        "/api/graph/sync-schema",
        { method: "POST", body: JSON.stringify(authBody) }
      );
      setSyncMsg(`Synced. schema_hash=${r.schema_hash} ${r.message ?? ""}`);
    } catch (e) {
      setSyncMsg(e instanceof Error ? e.message : String(e));
    }
  };

  const runRag = async () => {
    setRagOut(null);
    setRagLoading(true);
    try {
      const r = await apiJson<Record<string, unknown>>("/api/rag/query", {
        method: "POST",
        body: JSON.stringify({
          question: nlQuestion,
          cluster_uri: conn.clusterUri,
          database: conn.database,
          execute_kql: false,
          tenant_id: conn.tenantId || null,
          client_id: conn.clientId || null,
          client_secret: conn.clientSecret || null,
        }),
      });
      setRagOut(JSON.stringify(r, null, 2));
    } catch (e) {
      setRagOut(e instanceof Error ? e.message : String(e));
    } finally {
      setRagLoading(false);
    }
  };

  return (
    <div className="app">
      <h1>GraphRAG + Kusto (Neo4j)</h1>
      <p className="small">
        ASP.NET Core API. Credentials stay on the server when configured in{" "}
        <code>appsettings</code>; the form can override for local demos.
      </p>

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
          <label>Database</label>
          <input
            value={conn.database}
            onChange={(e) =>
              setConn((c) => ({ ...c, database: e.target.value }))
            }
          />
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
          <button type="button" onClick={testConnection}>
            Test connection
          </button>
          <button type="button" className="secondary" onClick={loadTables} disabled={loadingTables}>
            {loadingTables ? "Loading…" : "Load tables"}
          </button>
          {validateMsg && (
            <div className={`banner ${validateOk ? "ok" : "err"}`}>
              {validateMsg}
            </div>
          )}
          {tablesErr && <div className="banner err">{tablesErr}</div>}
          <ul className="tables">
            {tables.map((t) => (
              <li key={t.name} title={t.doc_string}>
                <strong>{t.name}</strong>
                <span className="small">
                  {" "}
                  ({t.columns?.length ?? 0} columns)
                </span>
              </li>
            ))}
          </ul>
        </div>

        <div className="panel">
          <h2>KQL</h2>
          <p className="small">
            Safe mode requires <code>take|limit</code> and a time filter (
            <code>ago()</code> etc.).
          </p>
          <textarea value={kql} onChange={(e) => setKql(e.target.value)} />
          <button type="button" onClick={runKql}>
            Run KQL
          </button>
          {result && (
            <div className="table-wrap">
              {result.error_detail ? (
                <p className="banner err">{result.error_detail}</p>
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
          <button type="button" onClick={syncGraph}>
            Sync schema to graph
          </button>
          {syncMsg && <div className="banner ok">{syncMsg}</div>}
        </div>
        <div className="panel">
          <h2>GraphRAG (NL → KQL)</h2>
          <p className="small">Requires Azure OpenAI in API configuration.</p>
          <textarea
            value={nlQuestion}
            onChange={(e) => setNlQuestion(e.target.value)}
          />
          <button type="button" disabled={ragLoading} onClick={runRag}>
            {ragLoading ? "…" : "Generate KQL"}
          </button>
          {ragOut && (
            <pre className="table-wrap" style={{ padding: "0.5rem" }}>
              {ragOut}
            </pre>
          )}
        </div>
      </div>
    </div>
  );
}
