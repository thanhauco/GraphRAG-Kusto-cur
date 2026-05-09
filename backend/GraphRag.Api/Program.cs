using System.Threading.RateLimiting;
using GraphRag.Api.Configuration;
using GraphRag.Api.Models;
using GraphRag.Api.Security;
using GraphRag.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

var graphRag = new GraphRagOptions();
builder.Configuration.GetSection(GraphRagOptions.SectionName).Bind(graphRag);
builder.Services.AddSingleton(graphRag);
builder.Services.AddSingleton<KustoQueryService>();
builder.Services.AddSingleton<Neo4jSchemaStore>();
builder.Services.AddSingleton<SchemaSyncOrchestrator>();
builder.Services.AddSingleton<GraphRagService>();

builder.Services.AddCors(o =>
{
    o.AddPolicy("Ui", p =>
    {
        p.WithOrigins(graphRag.CorsOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("kusto", opt =>
    {
        opt.Window = TimeSpan.FromMinutes(1);
        opt.PermitLimit = 120;
        opt.QueueLimit = 0;
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseCors("Ui");
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapHealthChecks("/health");

static void ValidateCluster(string uri, GraphRagOptions opt) =>
    ClusterUriValidator.EnsureAllowed(uri, opt.KustoClusterUriAllowlist);

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", runtime = "dotnet" }))
    .WithName("ApiHealth");

app.MapPost("/api/kusto/validate",
        async Task<Results<Ok<KustoValidateResponse>, BadRequest<string>>> (
            KustoValidateRequest body,
            KustoQueryService kusto,
            GraphRagOptions opt,
            CancellationToken ct) =>
        {
            try
            {
                ValidateCluster(body.ClusterUri, opt);
                var (ok, msg, ms, db) = await kusto.ValidateAsync(
                    body.ClusterUri, body.Database, body.TenantId, body.ClientId, body.ClientSecret, ct);
                return TypedResults.Ok(new KustoValidateResponse(ok, msg, ms, db));
            }
            catch (Exception ex)
            {
                return TypedResults.BadRequest(ex.Message);
            }
        })
    .DisableAntiforgery();

app.MapGet("/api/kusto/databases",
        async Task<Results<Ok<IReadOnlyList<string>>, BadRequest<string>>> (
            string cluster_uri,
            string database,
            string? tenant_id,
            string? client_id,
            string? client_secret,
            KustoQueryService kusto,
            GraphRagOptions opt,
            CancellationToken ct) =>
        {
            try
            {
                ValidateCluster(cluster_uri, opt);
                var list = await kusto.ListDatabasesAsync(cluster_uri, database, tenant_id, client_id, client_secret, ct);
                return TypedResults.Ok(list);
            }
            catch (Exception ex)
            {
                return TypedResults.BadRequest(ex.Message);
            }
        })
    .DisableAntiforgery();

app.MapGet("/api/kusto/databases/{database}/tables",
        async Task<Results<Ok<IReadOnlyList<TableInfo>>, BadRequest<string>>> (
            string database,
            string cluster_uri,
            string? tenant_id,
            string? client_id,
            string? client_secret,
            KustoQueryService kusto,
            GraphRagOptions opt,
            CancellationToken ct) =>
        {
            try
            {
                ValidateCluster(cluster_uri, opt);
                var tables = await kusto.ListTablesAsync(cluster_uri, database, tenant_id, client_id, client_secret, ct);
                var withCols = new List<TableInfo>();
                foreach (var t in tables)
                {
                    var cols = await kusto.GetTableColumnsAsync(
                        cluster_uri, database, t.Name, tenant_id, client_id, client_secret, ct);
                    var timeCols = SchemaRelationshipHeuristics.InferTimeColumns(cols);
                    withCols.Add(new TableInfo(t.Name, t.Folder, t.DocString, cols, timeCols));
                }
                return TypedResults.Ok((IReadOnlyList<TableInfo>)withCols);
            }
            catch (Exception ex)
            {
                return TypedResults.BadRequest(ex.Message);
            }
        })
    .DisableAntiforgery();

app.MapPost("/api/kusto/query",
        async Task<Results<Ok<KustoExecuteResponse>, BadRequest<string>>> (
            KustoExecuteRequest body,
            KustoQueryService kusto,
            GraphRagOptions opt,
            CancellationToken ct) =>
        {
            try
            {
                ValidateCluster(body.ClusterUri, opt);
                var r = await kusto.ExecuteKqlAsync(
                    body.ClusterUri,
                    body.Database,
                    body.Kql,
                    body.TenantId,
                    body.ClientId,
                    body.ClientSecret,
                    opt.DemoSafeMode,
                    ct);
                return TypedResults.Ok(r);
            }
            catch (Exception ex)
            {
                return TypedResults.BadRequest(ex.Message);
            }
        })
    .RequireRateLimiting("kusto")
    .DisableAntiforgery();

app.MapPost("/api/graph/sync-schema",
        async Task<Results<Ok<GraphSyncResult>, BadRequest<string>>> (
            GraphSyncRequest body,
            SchemaSyncOrchestrator sync,
            GraphRagOptions opt,
            CancellationToken ct) =>
        {
            try
            {
                ValidateCluster(body.ClusterUri, opt);
                var result = await sync.SyncAsync(
                    body.ClusterUri, body.Database, body.TenantId, body.ClientId, body.ClientSecret, ct);
                return TypedResults.Ok(result);
            }
            catch (Exception ex)
            {
                return TypedResults.BadRequest(ex.Message);
            }
        })
    .DisableAntiforgery();

app.MapPost("/api/rag/query",
        async Task<Results<Ok<RagQueryResponse>, BadRequest<string>>> (
            RagQueryRequest body,
            GraphRagService rag,
            GraphRagOptions opt,
            CancellationToken ct) =>
        {
            try
            {
                ValidateCluster(body.ClusterUri, opt);
                var r = await rag.QueryAsync(body, ct);
                return TypedResults.Ok(r);
            }
            catch (Exception ex)
            {
                return TypedResults.BadRequest(ex.Message);
            }
        })
    .RequireRateLimiting("kusto")
    .DisableAntiforgery();

app.MapGet("/api/demo/samples", () =>
        Results.Ok(DemoSamples.Get()))
    .WithName("DemoSamples");

app.Run();

public static class DemoSamples
{
    public static object Get() => new
    {
        nl_questions = new[]
        {
            "Which deployments touched services that had open incidents in the last 7 days?",
            "What is the blast radius from component failures to incidents and alerts?",
            "List correlated alerts sharing the same correlation id in the last 48 hours."
        },
        paired = new[]
        {
            new
            {
                title = "Multi-hop join story",
                nl = "Show services that had both incidents and deployments in the last 3 days.",
                graphrag_note = "Neo4j yields RELATES_TO paths across Incidents, Services, Deployments before KQL is drafted.",
                kql_hint =
                    "join Incidents, Alerts, Services on shared keys with time filters — easy to get wrong without schema graph."
            }
        }
    };
}
