using GraphRag.Api.Models;
using GraphRag.Api.Services;

namespace GraphRag.Api.Tests;

public class SchemaRelationshipHeuristicsTests
{
    [Fact]
    public void InferEdges_links_tables_with_shared_join_columns()
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Incidents"] = new[] { "IncidentId", "ServiceId", "Timestamp" },
            ["Services"] = new[] { "ServiceId", "Name" },
            ["Alerts"] = new[] { "IncidentId", "CorrelationId" }
        };
        var edges = SchemaRelationshipHeuristics.InferEdges(map);
        Assert.Contains(edges, e => string.Equals(e.OnColumn, "IncidentId", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(edges, e => string.Equals(e.OnColumn, "ServiceId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InferTimeColumns_detects_common_hints()
    {
        var cols = new List<ColumnInfo>
        {
            new("foo"),
            new("PreciseTimeStamp", "datetime"),
            new("Region", "string")
        };
        var t = SchemaRelationshipHeuristics.InferTimeColumns(cols);
        Assert.Contains("PreciseTimeStamp", t);
    }

    [Fact]
    public void InferEdges_empty_for_unrelated_tables()
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = new[] { "x", "y" },
            ["B"] = new[] { "p", "q" }
        };
        Assert.Empty(SchemaRelationshipHeuristics.InferEdges(map));
    }
}
