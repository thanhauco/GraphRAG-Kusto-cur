using System.Text.Json;
using GraphRag.Api.Configuration;
using GraphRag.Api.Models;

namespace GraphRag.Api.Services;

public class SchemaSyncOrchestrator
{
    private readonly KustoQueryService _kusto;
    private readonly Neo4jSchemaStore _neo;
    private readonly GraphRagOptions _options;

    public SchemaSyncOrchestrator(KustoQueryService kusto, Neo4jSchemaStore neo, GraphRagOptions options)
    {
        _kusto = kusto;
        _neo = neo;
        _options = options;
    }

    public async Task<GraphSyncResult> SyncAsync(
        string clusterUri,
        string database,
        string? tenantId,
        string? clientId,
        string? clientSecret,
        CancellationToken ct)
    {
        await _neo.EnsureConstraintsAsync(ct);

        var tables = await _kusto.ListTablesAsync(clusterUri, database, tenantId, clientId, clientSecret, ct);
        var tableColumnMap = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var enriched = new List<TableInfo>();

        foreach (var t in tables)
        {
            var cols = await _kusto.GetTableColumnsAsync(clusterUri, database, t.Name, tenantId, clientId, clientSecret, ct);
            var timeCols = SchemaRelationshipHeuristics.InferTimeColumns(cols);
            tableColumnMap[t.Name] = cols.Select(c => c.Name).ToList();
            enriched.Add(new TableInfo(t.Name, t.Folder, t.DocString, cols, timeCols));
        }

        var edges = SchemaRelationshipHeuristics.InferEdges(tableColumnMap);
        return await _neo.SyncSchemaAsync(database, enriched, tableColumnMap, edges, ct);
    }
}
