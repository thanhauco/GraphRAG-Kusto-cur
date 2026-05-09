using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.AI.OpenAI;
using GraphRag.Api.Configuration;
using GraphRag.Api.Models;
using OpenAI.Chat;

namespace GraphRag.Api.Services;

public class GraphRagService
{
    private readonly GraphRagOptions _options;
    private readonly Neo4jSchemaStore _neo;
    private readonly KustoQueryService _kusto;

    public GraphRagService(GraphRagOptions options, Neo4jSchemaStore neo, KustoQueryService kusto)
    {
        _options = options;
        _neo = neo;
        _kusto = kusto;
    }

    public async Task<RagQueryResponse> QueryAsync(RagQueryRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.AzureOpenAiEndpoint) || string.IsNullOrWhiteSpace(_options.AzureOpenAiApiKey))
            throw new InvalidOperationException("Azure OpenAI is not configured (GraphRag:AzureOpenAiEndpoint / AzureOpenAiApiKey).");

        var seeds = await _neo.MatchTablesByKeywordsAsync(req.Database, req.Question, ct);
        var contextJson = await _neo.BuildContextSubgraphAsync(req.Database, seeds, ct);

        var client = new AzureOpenAIClient(new Uri(_options.AzureOpenAiEndpoint), new AzureKeyCredential(_options.AzureOpenAiApiKey));
        var chat = client.GetChatClient(_options.AzureOpenAiDeploymentName);

        var system =
            """
            You are an expert in Azure Data Explorer Kusto KQL. You MUST only reference tables and columns that appear in CONTEXT_JSON.
            Always produce safe demo queries: include a time filter using ago() or between() on an appropriate datetime column from CONTEXT, and a take or limit cap (e.g. | take 200).
            Return a single JSON object only, with keys: reasoning (string), kql (string), assumptions (string array), graph_node_refs (string array of table.column or table citations).
            Do not wrap in markdown fences.
            """;

        var user = $"QUESTION:\n{req.Question}\n\nCONTEXT_JSON:\n{contextJson}";

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(system),
            new UserChatMessage(user)
        };

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
        };

        ClientResult<ChatCompletion> completion;
        try
        {
            completion = await chat.CompleteChatAsync(messages, options, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"LLM call failed: {ex.Message}", ex);
        }

        var text = completion.Value.Content[0].Text;
        RagLlmJson? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RagLlmJson>(text, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse LLM JSON: {ex.Message}. Raw: {text[..Math.Min(200, text.Length)]}", ex);
        }

        if (parsed == null || string.IsNullOrWhiteSpace(parsed.Kql))
            throw new InvalidOperationException("LLM returned empty KQL.");

        KustoExecuteResponse? exec = null;
        if (req.ExecuteKql)
        {
            exec = await _kusto.ExecuteKqlAsync(
                req.ClusterUri,
                req.Database,
                parsed.Kql,
                req.TenantId,
                req.ClientId,
                req.ClientSecret,
                _options.DemoSafeMode,
                ct).ConfigureAwait(false);
        }

        IReadOnlyList<string> assumptions = parsed.Assumptions ?? new List<string>();
        IReadOnlyList<string> refs = parsed.GraphNodeRefs ?? new List<string>();

        return new RagQueryResponse(
            parsed.Reasoning ?? "",
            parsed.Kql,
            assumptions,
            refs,
            exec,
            seeds);
    }

    private sealed class RagLlmJson
    {
        [JsonPropertyName("reasoning")]
        public string? Reasoning { get; set; }

        [JsonPropertyName("kql")]
        public string? Kql { get; set; }

        [JsonPropertyName("assumptions")]
        public List<string>? Assumptions { get; set; }

        [JsonPropertyName("graph_node_refs")]
        public List<string>? GraphNodeRefs { get; set; }
    }
}
