namespace GraphRag.Api.Configuration;

public class GraphRagOptions
{
    public const string SectionName = "GraphRag";

    public string[] CorsOrigins { get; set; } = ["http://localhost:5173"];

    public string KustoClusterUri { get; set; } = "";
    public string KustoDatabase { get; set; } = "";
    public string KustoTenantId { get; set; } = "";
    public string KustoClientId { get; set; } = "";
    public string KustoClientSecret { get; set; } = "";

    /// <summary>When true, use Azure managed identity for Kusto (system-assigned, or user-assigned via KustoManagedIdentityClientId).</summary>
    public bool KustoUseManagedIdentity { get; set; }

    /// <summary>Optional client id for user-assigned managed identity (ignored when empty).</summary>
    public string KustoManagedIdentityClientId { get; set; } = "";

    /// <summary>URL prefixes allowed when the client supplies cluster_uri (SSRF mitigation).</summary>
    public string[] KustoClusterUriAllowlist { get; set; } = ["https://"];

    public string Neo4jUri { get; set; } = "bolt://localhost:7687";
    public string Neo4jUser { get; set; } = "neo4j";
    public string Neo4jPassword { get; set; } = "";

    public string AzureOpenAiEndpoint { get; set; } = "";
    public string AzureOpenAiApiKey { get; set; } = "";
    public string AzureOpenAiDeploymentName { get; set; } = "gpt-4o";
    public string AzureOpenAiApiVersion { get; set; } = "2024-10-21";

    public bool DemoSafeMode { get; set; } = true;
    public int MaxKqlLength { get; set; } = 65536;
    public int MaxKqlResultRows { get; set; } = 5000;
    public int KustoQueryTimeoutSeconds { get; set; } = 60;
    public bool SchemaSampleColumns { get; set; }

    /// <summary>Rows to scan per table when SchemaSampleColumns is enabled (distinct estimates are from this sample only).</summary>
    public int SchemaSampleRowCap { get; set; } = 100;
}
