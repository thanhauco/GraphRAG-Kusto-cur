using GraphRag.Api.Models;

namespace GraphRag.Api.Services;

public static class SchemaRelationshipHeuristics
{
    private static readonly string[] TimeColumnHints =
    [
        "timestamp", "timegenerated", "precisetimestamp", "ingestion_time", "ingesteddatetime"
    ];

    private static readonly string[] JoinSuffixes =
    [
        "id", "_id", "key", "guid"
    ];

    public static readonly string[] CorrelationHints =
    [
        "incidentid", "correlationid", "alertid", "tenantid", "serviceid", "componentid",
        "deploymentid", "subscriptionid", "resourceid"
    ];

    public static IReadOnlyList<string> InferTimeColumns(IReadOnlyList<ColumnInfo> columns)
    {
        return columns
            .Select(c => c.Name)
            .Where(name =>
            {
                var n = name.ToLowerInvariant();
                return TimeColumnHints.Any(h => n.Contains(h, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();
    }

    /// <summary>Table A -[RELATES_TO {{column:on}}]- Table B when a column name appears as join key on both sides.</summary>
    public static IReadOnlyList<(string FromTable, string ToTable, string OnColumn)> InferEdges(
        IReadOnlyDictionary<string, IReadOnlyList<string>> tableColumns)
    {
        var colToTables = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, cols) in tableColumns)
        {
            foreach (var c in cols)
            {
                if (!LooksLikeJoinKey(c))
                    continue;
                if (!colToTables.TryGetValue(c, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    colToTables[c] = set;
                }

                set.Add(table);
            }
        }

        var edges = new List<(string, string, string)>();
        foreach (var (col, tables) in colToTables)
        {
            if (tables.Count < 2)
                continue;
            var list = tables.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                for (var j = i + 1; j < list.Count; j++)
                {
                    if (string.Equals(list[i], list[j], StringComparison.OrdinalIgnoreCase))
                        continue;
                    edges.Add((list[i], list[j], col));
                }
            }
        }

        return edges;
    }

    private static bool LooksLikeJoinKey(string columnName)
    {
        var n = columnName.ToLowerInvariant();
        if (CorrelationHints.Contains(n))
            return true;
        if (n is "id" or "tenantid")
            return true;
        foreach (var s in JoinSuffixes)
        {
            if (n.EndsWith(s, StringComparison.OrdinalIgnoreCase) && n.Length > s.Length)
                return true;
        }

        return false;
    }
}
