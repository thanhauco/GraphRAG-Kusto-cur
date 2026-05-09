using System.Data;
using GraphRag.Api.Configuration;
using GraphRag.Api.Models;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;

namespace GraphRag.Api.Services;

public class KustoQueryService
{
    private readonly GraphRagOptions _options;

    public KustoQueryService(GraphRagOptions options)
    {
        _options = options;
    }

    private string ResolveQueryDatabase(string? database)
    {
        if (!string.IsNullOrWhiteSpace(database))
            return database.Trim();
        if (!string.IsNullOrWhiteSpace(_options.KustoDatabase))
            return _options.KustoDatabase.Trim();
        return "Samples";
    }

    public ICslQueryProvider CreateProvider(string clusterUri, string? tenantId, string? clientId, string? clientSecret)
    {
        var kcsb = new KustoConnectionStringBuilder(clusterUri);

        if (_options.KustoUseManagedIdentity)
        {
            if (!string.IsNullOrWhiteSpace(_options.KustoManagedIdentityClientId))
                kcsb = kcsb.WithAadUserManagedIdentity(_options.KustoManagedIdentityClientId);
            else
                kcsb = kcsb.WithAadSystemManagedIdentity();

            return KustoClientFactory.CreateCslQueryProvider(kcsb);
        }

        var tid = tenantId ?? _options.KustoTenantId;
        var cid = clientId ?? _options.KustoClientId;
        var csec = clientSecret ?? _options.KustoClientSecret;

        if (string.IsNullOrWhiteSpace(csec) || string.IsNullOrWhiteSpace(cid) || string.IsNullOrWhiteSpace(tid))
        {
            throw new InvalidOperationException(
                "Kusto authentication is not configured: provide tenant_id, client_id, and client_secret (request body or appsettings GraphRag section), " +
                "or set GraphRag:KustoUseManagedIdentity to true for Azure managed identity.");
        }

        kcsb = new KustoConnectionStringBuilder(clusterUri).WithAadApplicationKeyAuthentication(cid, csec, tid);
        return KustoClientFactory.CreateCslQueryProvider(kcsb);
    }

    private ClientRequestProperties BuildProperties(int maxRows)
    {
        var cap = Math.Min(maxRows, _options.MaxKqlResultRows);
        var props = new ClientRequestProperties();
        props.SetOption(ClientRequestProperties.OptionServerTimeout, $"{_options.KustoQueryTimeoutSeconds}s");
        props.SetOption(ClientRequestProperties.OptionTruncationMaxRecords, (long)cap);
        return props;
    }

    public Task<(bool Ok, string Message, double LatencyMs, IReadOnlyList<string> DatabasesSample)> ValidateAsync(
        string clusterUri,
        string database,
        string? tenantId,
        string? clientId,
        string? clientSecret,
        CancellationToken ct)
    {
        var ctxDb = ResolveQueryDatabase(database);

        return Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var provider = CreateProvider(clusterUri, tenantId, clientId, clientSecret);
                var props = BuildProperties(100);
                const string kql = ".show databases | project DatabaseName | take 20";

                var (cols, rows, err) = ExecuteInternal(provider, ctxDb, kql, props);
                sw.Stop();
                if (err != null)
                    return (false, err, sw.Elapsed.TotalMilliseconds, (IReadOnlyList<string>)Array.Empty<string>());

                var idx = cols.FindIndex(c => string.Equals(c, "DatabaseName", StringComparison.OrdinalIgnoreCase));
                if (idx < 0)
                    idx = 0;

                var names = rows
                    .Select(r => r.ElementAtOrDefault(idx)?.ToString() ?? "")
                    .Where(static s => !string.IsNullOrEmpty(s))
                    .ToList();

                return (true, "Connected", sw.Elapsed.TotalMilliseconds, names);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return (false, ex.Message, sw.Elapsed.TotalMilliseconds, Array.Empty<string>());
            }
        }, ct);
    }

    private static (List<string> Columns, List<object?[]> Rows, string? Error) ExecuteInternal(
        ICslQueryProvider provider,
        string database,
        string kql,
        ClientRequestProperties props)
    {
        try
        {
            using IDataReader reader = provider.ExecuteQuery(database, kql, props);
            var columns = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
                columns.Add(reader.GetName(i));

            var rows = new List<object?[]>();
            while (reader.Read())
            {
                var buffer = new object[reader.FieldCount];
                reader.GetValues(buffer);
                rows.Add(buffer);
            }

            return (columns, rows, null);
        }
        catch (Exception ex)
        {
            return (new List<string>(), new List<object?[]>(), ex.Message);
        }
    }

    public Task<IReadOnlyList<string>> ListDatabasesAsync(
        string clusterUri,
        string? database,
        string? tenantId,
        string? clientId,
        string? clientSecret,
        CancellationToken ct)
    {
        var ctxDb = ResolveQueryDatabase(database);

        return Task.Run(() =>
        {
            using var provider = CreateProvider(clusterUri, tenantId, clientId, clientSecret);
            const string kql = ".show databases | project DatabaseName | sort by DatabaseName asc";
            var props = BuildProperties(5000);
            var (cols, rows, err) = ExecuteInternal(provider, ctxDb, kql, props);
            if (err != null)
                throw new InvalidOperationException(err);

            var idx = cols.FindIndex(c => string.Equals(c, "DatabaseName", StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                idx = 0;

            return (IReadOnlyList<string>)rows
                .Select(r => r.ElementAtOrDefault(idx)?.ToString() ?? "")
                .Where(static s => !string.IsNullOrEmpty(s))
                .ToList();
        }, ct);
    }

    public Task<IReadOnlyList<TableInfo>> ListTablesAsync(
        string clusterUri,
        string database,
        string? tenantId,
        string? clientId,
        string? clientSecret,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            using var provider = CreateProvider(clusterUri, tenantId, clientId, clientSecret);
            var safeDb = KustoLiteral.EscapeString(database);
            var kql = $".show tables | where DatabaseName == {safeDb} | project TableName, Folder, DocString";
            var props = BuildProperties(10000);
            var (cols, rows, err) = ExecuteInternal(provider, database, kql, props);
            if (err != null)
                throw new InvalidOperationException(err);

            var nameIdx = cols.FindIndex(c => string.Equals(c, "TableName", StringComparison.OrdinalIgnoreCase));
            var folderIdx = cols.FindIndex(c => string.Equals(c, "Folder", StringComparison.OrdinalIgnoreCase));
            var docIdx = cols.FindIndex(c => string.Equals(c, "DocString", StringComparison.OrdinalIgnoreCase));

            var list = new List<TableInfo>();
            foreach (var r in rows)
            {
                var name = nameIdx >= 0 ? r.ElementAtOrDefault(nameIdx)?.ToString() ?? "" : "";
                if (string.IsNullOrEmpty(name))
                    continue;
                var folder = folderIdx >= 0 ? r.ElementAtOrDefault(folderIdx)?.ToString() ?? "" : "";
                var doc = docIdx >= 0 ? r.ElementAtOrDefault(docIdx)?.ToString() ?? "" : "";
                list.Add(new TableInfo(name, folder, doc, Array.Empty<ColumnInfo>(), Array.Empty<string>()));
            }

            return (IReadOnlyList<TableInfo>)list;
        }, ct);
    }

    public Task<KustoExecuteResponse> ExecuteKqlAsync(
        string clusterUri,
        string database,
        string kql,
        string? tenantId,
        string? clientId,
        string? clientSecret,
        bool safeMode,
        CancellationToken ct)
    {
        if (kql.Length > _options.MaxKqlLength)
            throw new InvalidOperationException("KQL exceeds maximum length.");

        if (safeMode && !KqlSafety.LooksBounded(kql))
            throw new InvalidOperationException(
                "Demo safe mode: KQL must include take/limit and a time filter (e.g. where Timestamp > ago(7d)).");

        return Task.Run(() =>
        {
            using var provider = CreateProvider(clusterUri, tenantId, clientId, clientSecret);
            var props = BuildProperties(_options.MaxKqlResultRows);
            var (cols, rows, err) = ExecuteInternal(provider, database, kql, props);
            if (err != null)
                return new KustoExecuteResponse(cols, Array.Empty<IReadOnlyList<object?>>(), false, err);

            var max = _options.MaxKqlResultRows;
            var truncated = rows.Count >= max;
            var projection = rows.Select(static r => (IReadOnlyList<object?>)r.ToList()).ToList();
            return new KustoExecuteResponse(cols, projection, truncated, null);
        }, ct);
    }

    /// <summary>
    /// Trusted server-side sampling only (bounded row cap, no demo safe-mode time filter).
    /// </summary>
    public Task<KustoExecuteResponse> ExecuteTrustedSampleAsync(
        string clusterUri,
        string database,
        string kql,
        string? tenantId,
        string? clientId,
        string? clientSecret,
        int maxRows,
        CancellationToken ct)
    {
        if (kql.Length > _options.MaxKqlLength)
            throw new InvalidOperationException("KQL exceeds maximum length.");

        maxRows = Math.Clamp(maxRows, 1, 500);

        return Task.Run(() =>
        {
            using var provider = CreateProvider(clusterUri, tenantId, clientId, clientSecret);
            var props = BuildProperties(maxRows);
            var (cols, rows, err) = ExecuteInternal(provider, database, kql, props);
            if (err != null)
                return new KustoExecuteResponse(cols, Array.Empty<IReadOnlyList<object?>>(), false, err);

            var projection = rows.Select(static r => (IReadOnlyList<object?>)r.ToList()).ToList();
            return new KustoExecuteResponse(cols, projection, rows.Count >= maxRows, null);
        }, ct);
    }

    public async Task<IReadOnlyDictionary<string, int>> EstimateDistinctFromSampleAsync(
        string clusterUri,
        string database,
        string tableName,
        IReadOnlyList<string> columnNames,
        string? tenantId,
        string? clientId,
        string? clientSecret,
        int sampleCap,
        CancellationToken ct)
    {
        if (columnNames.Count == 0)
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var bracket = BracketTable(tableName);
        var kql = $"{bracket} | take {sampleCap}";
        var resp = await ExecuteTrustedSampleAsync(clusterUri, database, kql, tenantId, clientId, clientSecret, sampleCap, ct)
            .ConfigureAwait(false);

        if (!string.IsNullOrEmpty(resp.ErrorDetail) || resp.Columns.Count == 0)
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var colIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < resp.Columns.Count; i++)
            colIndex[resp.Columns[i]] = i;

        var sets = columnNames.ToDictionary(c => c, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);

        foreach (var row in resp.Rows)
        {
            foreach (var cn in columnNames)
            {
                if (!colIndex.TryGetValue(cn, out var idx))
                    continue;
                var raw = row.ElementAtOrDefault(idx);
                var val = raw?.ToString() ?? "";
                sets[cn].Add(val);
            }
        }

        return sets.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.OrdinalIgnoreCase);
    }

    public Task<IReadOnlyList<ColumnInfo>> GetTableColumnsAsync(
        string clusterUri,
        string database,
        string tableName,
        string? tenantId,
        string? clientId,
        string? clientSecret,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            using var provider = CreateProvider(clusterUri, tenantId, clientId, clientSecret);
            var escaped = tableName.Replace("'", "''");
            var kql = $".show table ['{escaped}'] cslschema";
            var props = BuildProperties(5000);
            var (cols, rows, err) = ExecuteInternal(provider, database, kql, props);
            if (err != null)
                throw new InvalidOperationException($"Schema for {tableName}: {err}");

            var nameCol = cols.FindIndex(c => string.Equals(c, "ColumnName", StringComparison.OrdinalIgnoreCase));
            var typeCol = cols.FindIndex(c => string.Equals(c, "ColumnType", StringComparison.OrdinalIgnoreCase));

            var result = new List<ColumnInfo>();
            foreach (var r in rows)
            {
                var cn = nameCol >= 0 ? r.ElementAtOrDefault(nameCol)?.ToString() ?? "" : "";
                if (string.IsNullOrEmpty(cn))
                    continue;
                var ctStr = typeCol >= 0 ? r.ElementAtOrDefault(typeCol)?.ToString() ?? "" : "";
                result.Add(new ColumnInfo(cn, ctStr, ""));
            }

            return (IReadOnlyList<ColumnInfo>)result;
        }, ct);
    }

    private static string BracketTable(string tableName)
    {
        var escaped = tableName.Replace("'", "''");
        return $"['{escaped}']";
    }

    /// <summary>Quote a string literal for KQL comparisons.</summary>
    private static class KustoLiteral
    {
        public static string EscapeString(string value)
        {
            var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return $"\"{escaped}\"";
        }
    }
}
