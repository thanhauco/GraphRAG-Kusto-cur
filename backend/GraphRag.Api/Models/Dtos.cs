using System.Text.Json.Serialization;

namespace GraphRag.Api.Models;

public record KustoValidateRequest(
    [property: JsonPropertyName("cluster_uri")] string ClusterUri,
    [property: JsonPropertyName("database")] string Database,
    [property: JsonPropertyName("tenant_id")] string? TenantId = null,
    [property: JsonPropertyName("client_id")] string? ClientId = null,
    [property: JsonPropertyName("client_secret")] string? ClientSecret = null);

public record KustoValidateResponse(
    bool Ok,
    string Message,
    [property: JsonPropertyName("latency_ms")] double LatencyMs,
    [property: JsonPropertyName("databases_sample")] IReadOnlyList<string> DatabasesSample);

public record KustoExecuteRequest(
    [property: JsonPropertyName("cluster_uri")] string ClusterUri,
    [property: JsonPropertyName("database")] string Database,
    [property: JsonPropertyName("kql")] string Kql,
    [property: JsonPropertyName("tenant_id")] string? TenantId = null,
    [property: JsonPropertyName("client_id")] string? ClientId = null,
    [property: JsonPropertyName("client_secret")] string? ClientSecret = null);

public record KustoExecuteResponse(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated,
    [property: JsonPropertyName("error_detail")] string? ErrorDetail);

public record ColumnInfo(string Name, [property: JsonPropertyName("data_type")] string DataType = "", string Description = "");

public record TableInfo(
    string Name,
    string Folder = "",
    [property: JsonPropertyName("doc_string")] string DocString = "",
    IReadOnlyList<ColumnInfo>? Columns = null,
    [property: JsonPropertyName("time_columns")] IReadOnlyList<string>? TimeColumns = null);

public record SchemaGraphSummary(
    string Database,
    [property: JsonPropertyName("table_count")] int TableCount,
    [property: JsonPropertyName("column_count")] int ColumnCount,
    [property: JsonPropertyName("relationship_count")] int RelationshipCount);

public record GraphSyncResult(
    bool Ok,
    SchemaGraphSummary Summary,
    [property: JsonPropertyName("schema_hash")] string SchemaHash,
    string Message = "");

public record GraphSyncRequest(
    [property: JsonPropertyName("cluster_uri")] string ClusterUri,
    [property: JsonPropertyName("database")] string Database,
    [property: JsonPropertyName("tenant_id")] string? TenantId = null,
    [property: JsonPropertyName("client_id")] string? ClientId = null,
    [property: JsonPropertyName("client_secret")] string? ClientSecret = null);

public record RagQueryRequest(
    string Question,
    [property: JsonPropertyName("cluster_uri")] string ClusterUri,
    [property: JsonPropertyName("database")] string Database,
    [property: JsonPropertyName("execute_kql")] bool ExecuteKql = false,
    [property: JsonPropertyName("tenant_id")] string? TenantId = null,
    [property: JsonPropertyName("client_id")] string? ClientId = null,
    [property: JsonPropertyName("client_secret")] string? ClientSecret = null);

public record RagQueryResponse(
    string Reasoning,
    string Kql,
    IReadOnlyList<string> Assumptions,
    [property: JsonPropertyName("graph_node_refs")] IReadOnlyList<string> GraphNodeRefs,
    [property: JsonPropertyName("execute_result")] KustoExecuteResponse? ExecuteResult,
    [property: JsonPropertyName("retrieval_tables")] IReadOnlyList<string> RetrievalTables);
