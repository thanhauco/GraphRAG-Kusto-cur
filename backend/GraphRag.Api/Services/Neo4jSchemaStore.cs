using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphRag.Api.Configuration;
using GraphRag.Api.Models;
using Neo4j.Driver;

namespace GraphRag.Api.Services;

public class Neo4jSchemaStore
{
    private readonly GraphRagOptions _options;
    private readonly ILogger<Neo4jSchemaStore> _logger;

    public Neo4jSchemaStore(GraphRagOptions options, ILogger<Neo4jSchemaStore> logger)
    {
        _options = options;
        _logger = logger;
    }

    private IDriver CreateDriver()
    {
        if (string.IsNullOrWhiteSpace(_options.Neo4jPassword))
            throw new InvalidOperationException("Neo4j password is not configured (GraphRag:Neo4jPassword).");
        return GraphDatabase.Driver(_options.Neo4jUri, AuthTokens.Basic(_options.Neo4jUser, _options.Neo4jPassword));
    }

    public async Task EnsureConstraintsAsync(CancellationToken ct)
    {
        await using var driver = CreateDriver();
        await using var session = driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("CREATE CONSTRAINT db_name IF NOT EXISTS FOR (d:Database) REQUIRE d.name IS UNIQUE");
            await tx.RunAsync("CREATE CONSTRAINT table_fqn IF NOT EXISTS FOR (t:Table) REQUIRE t.fqn IS UNIQUE");
            await tx.RunAsync("CREATE CONSTRAINT col_id IF NOT EXISTS FOR (c:Column) REQUIRE c.id IS UNIQUE");
        });
    }

    public async Task<GraphSyncResult> SyncSchemaAsync(
        string database,
        IReadOnlyList<TableInfo> tables,
        IReadOnlyDictionary<string, IReadOnlyList<string>> tableColumns,
        IReadOnlyList<(string FromTable, string ToTable, string OnColumn)> edges,
        CancellationToken ct)
    {
        await using var driver = CreateDriver();
        await using var session = driver.AsyncSession();

        var schemaPayload = JsonSerializer.Serialize(new
        {
            database,
            tables = tables.Select(t => t.Name).OrderBy(x => x).ToList(),
            columns = tableColumns.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(c => c).ToList()),
            edges = edges.OrderBy(e => e.FromTable).ThenBy(e => e.ToTable).Select(e => new { e.FromTable, e.ToTable, e.OnColumn }).ToList()
        });
        var schemaHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(schemaPayload)))[..16].ToLowerInvariant();

        var columnCount = tableColumns.Values.Sum(c => c.Count);

        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                """
                MERGE (d:Database { name: $db })
                SET d.last_sync = datetime(), d.schema_hash = $hash
                """,
                new { db = database, hash = schemaHash });

            foreach (var t in tables)
            {
                var fqn = $"{database}.{t.Name}";
                var timeCols = t.TimeColumns ?? SchemaRelationshipHeuristics.InferTimeColumns(t.Columns ?? Array.Empty<ColumnInfo>());
                await tx.RunAsync(
                    """
                    MATCH (d:Database { name: $db })
                    MERGE (t:Table { fqn: $fqn })
                    SET t.name = $name, t.folder = $folder, t.doc_string = $doc,
                        t.time_columns = $timeCols
                    MERGE (d)-[:HAS_TABLE]->(t)
                    """,
                    new
                    {
                        db = database,
                        fqn,
                        name = t.Name,
                        folder = t.Folder,
                        doc = t.DocString,
                        timeCols = timeCols.ToArray()
                    });

                foreach (var c in t.Columns ?? Array.Empty<ColumnInfo>())
                {
                    var colId = $"{fqn}.{c.Name}";
                    await tx.RunAsync(
                        """
                        MATCH (t:Table { fqn: $fqn })
                        MERGE (c:Column { id: $colId })
                        SET c.name = $colName, c.data_type = $dtype, c.description = $desc,
                            c.sample_distinct_estimate = $sampleEst
                        MERGE (t)-[:HAS_COLUMN]->(c)
                        """,
                        new
                        {
                            fqn,
                            colId,
                            colName = c.Name,
                            dtype = c.DataType,
                            desc = c.Description,
                            sampleEst = c.SampleDistinctEstimate
                        });
                }
            }

            foreach (var (fromTable, toTable, onCol) in edges)
            {
                var fromFqn = $"{database}.{fromTable}";
                var toFqn = $"{database}.{toTable}";
                await tx.RunAsync(
                    """
                    MATCH (a:Table { fqn: $fromFqn })
                    MATCH (b:Table { fqn: $toFqn })
                    MERGE (a)-[r:RELATES_TO]->(b)
                    SET r.column = $onCol, r.inferred = true
                    """,
                    new { fromFqn, toFqn, onCol });
            }
        });

        var relCount = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                """
                MATCH (d:Database { name: $db })-[:HAS_TABLE]->(:Table)-[r:RELATES_TO]-(:Table)
                RETURN count(distinct r) AS c
                """,
                new { db = database });
            if (!await cursor.FetchAsync())
                return 0;
            return cursor.Current["c"].As<int>();
        });

        _logger.LogInformation("Neo4j schema sync: {Db} tables {Tc} cols {Cc} rels {R}",
            database, tables.Count, columnCount, relCount);

        return new GraphSyncResult(true,
            new SchemaGraphSummary(database, tables.Count, columnCount, relCount),
            schemaHash,
            "Schema synced to Neo4j");
    }

    /// <summary>Retrieve table names + columns + neighbors for GraphRAG context.</summary>
    public async Task<string> BuildContextSubgraphAsync(string database, IReadOnlyList<string> seedTables, CancellationToken ct)
    {
        if (seedTables.Count == 0)
            return "{}";

        await using var driver = CreateDriver();
        await using var session = driver.AsyncSession();
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                """
                MATCH (d:Database { name: $db })-[:HAS_TABLE]->(t:Table)
                WHERE t.name IN $seeds
                OPTIONAL MATCH (t)-[:RELATES_TO]-(n:Table)
                WITH collect(distinct t) + collect(distinct n) AS nodes
                UNWIND nodes AS x
                WITH DISTINCT x
                OPTIONAL MATCH (x)-[:HAS_COLUMN]->(c:Column)
                WITH x, collect(CASE WHEN c IS NULL THEN null ELSE
                  c.name + ':' + coalesce(c.data_type, '') +
                  CASE WHEN c.sample_distinct_estimate IS NULL THEN ''
                  ELSE '~' + toString(c.sample_distinct_estimate) END
                END) AS raw
                WITH x, [p IN raw WHERE p IS NOT NULL] AS colpairs
                RETURN x.name AS table, x.time_columns AS time_columns, x.folder AS folder, colpairs
                """,
                new { db = database, seeds = seedTables.ToArray() });

            var rows = new List<object>();
            while (await cursor.FetchAsync())
            {
                var record = cursor.Current;
                var colpairs = record["colpairs"].As<List<string>>();
                List<string> timeCols = [];
                try
                {
                    if (record["time_columns"] != null)
                        timeCols = record["time_columns"].As<List<string>>();
                }
                catch
                {
                    /* Neo4j may omit or type differently */
                }

                rows.Add(new
                {
                    table = record["table"].As<string>(),
                    time_columns = timeCols,
                    folder = record["folder"].As<string>(),
                    columns = colpairs
                });
            }

            return JsonSerializer.Serialize(new { database, tables = rows });
        });
    }

    public async Task<IReadOnlyList<string>> MatchTablesByKeywordsAsync(string database, string question, CancellationToken ct)
    {
        var tokens = Tokenize(question);
        if (tokens.Count == 0)
            return Array.Empty<string>();

        await using var driver = CreateDriver();
        await using var session = driver.AsyncSession();
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                """
                MATCH (d:Database { name: $db })-[:HAS_TABLE]->(t:Table)
                OPTIONAL MATCH (t)-[:HAS_COLUMN]->(c:Column)
                WITH t, collect(DISTINCT toLower(c.name)) AS cnamesRaw, lower(t.name) AS tname,
                     lower(coalesce(t.doc_string, '')) AS tdoc, lower(coalesce(t.folder, '')) AS tfolder
                WITH t, [x IN cnamesRaw WHERE x IS NOT NULL] AS cnames, tname, tdoc, tfolder
                RETURN t.name AS name,
                       reduce(s = 0, tok IN $tokens | s +
                         (CASE WHEN tname CONTAINS tok THEN 3 ELSE 0 END) +
                         (CASE WHEN tdoc CONTAINS tok THEN 2 ELSE 0 END) +
                         (CASE WHEN tfolder CONTAINS tok THEN 1 ELSE 0 END) +
                         size([x IN cnames WHERE x CONTAINS tok | x])
                       ) AS score
                ORDER BY score DESC
                LIMIT 8
                """,
                new { db = database, tokens = tokens.ToArray() });

            var scored = new List<(string name, int score)>();
            while (await cursor.FetchAsync())
            {
                var name = cursor.Current["name"].As<string>();
                var score = cursor.Current["score"].As<int>();
                if (score > 0)
                    scored.Add((name, score));
            }

            return scored.Select(s => s.name).ToList();
        });
    }

    /// <summary>Neo4j-only snapshot for debugging (no Kusto cluster parameters).</summary>
    public async Task<GraphInspectResponse?> InspectGraphAsync(string database, CancellationToken ct)
    {
        await using var driver = CreateDriver();
        await using var session = driver.AsyncSession();

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                """
                MATCH (d:Database { name: $db })
                OPTIONAL MATCH (d)-[:HAS_TABLE]->(t:Table)
                OPTIONAL MATCH (t)-[:HAS_COLUMN]->(c:Column)
                OPTIONAL MATCH (t)-[r:RELATES_TO]-(:Table)
                RETURN d.schema_hash AS hash,
                       count(DISTINCT t) AS tc,
                       count(DISTINCT c) AS cc,
                       count(DISTINCT r) AS rc
                """,
                new { db = database });

            if (!await cursor.FetchAsync())
                return null;

            var record = cursor.Current;
            string? hash = null;
            try
            {
                hash = record["hash"].As<string?>();
            }
            catch
            {
                /* Property missing or wrong type */
            }

            var esc = database.Replace("\\", "\\\\").Replace("'", "\\'");
            var examples = new[]
            {
                $"MATCH (d:Database {{ name: '{esc}' }})-[:HAS_TABLE]->(t:Table) RETURN d, t LIMIT 40",
                $"MATCH (d:Database {{ name: '{esc}' }})-[:HAS_TABLE]->(t:Table)-[:HAS_COLUMN]->(c:Column) RETURN t.name, c.name, c.data_type LIMIT 80",
                $"MATCH (d:Database {{ name: '{esc}' }})-[:HAS_TABLE]->(a:Table)-[r:RELATES_TO]-(b:Table) RETURN a.name, type(r), b.name, r.column LIMIT 80"
            };

            return new GraphInspectResponse(
                database,
                hash,
                (int)record["tc"].As<long>(),
                (int)record["cc"].As<long>(),
                (int)record["rc"].As<long>(),
                examples);
        });
    }

    private static List<string> Tokenize(string q)
    {
        return q.ToLowerInvariant()
            .Split([' ', ',', '.', '?', '!', ';', ':', '(', ')', '[', ']', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2)
            .Distinct()
            .ToList();
    }
}
