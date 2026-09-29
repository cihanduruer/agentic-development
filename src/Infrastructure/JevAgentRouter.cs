using System.Net.Http.Json;
using AgenticHotelBooking.Application;

namespace AgenticHotelBooking.Infrastructure;

public sealed record JevRouterOptions(
    bool Enabled,
    bool Shadow,
    double MinimumConfidence,
    string Model,
    string? ApiKey);

public sealed class JevAgentRouter(HttpClient httpClient, JevRouterOptions options) : IAgentRouter
{
    public async Task<RoutingDecision> RouteAsync(
        RoutingRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);

        if (!options.Enabled)
        {
            return new RoutingDecision(
                "human_review",
                "human_review",
                1,
                true,
                "disabled",
                "Jev is disabled; the host routed safely to human review.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("Jev is enabled but TYPESAFE_API_KEY is not configured.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/systemone");
        message.Headers.Add("Authorization", options.ApiKey);
        message.Content = JsonContent.Create(new
        {
            state = new
            {
                task_category = request.TaskCategory,
                required_capability = request.RequiredCapability,
                risk = request.Risk,
                evidence_complete = request.EvidenceComplete,
                available_workers = request.AvailableWorkers.Keys
            },
            model = options.Model,
            questions = new
            {
                next_worker = new
                {
                    type = "choice",
                    instructions = "Choose the best currently available worker. Use human_review when risk, evidence, or capability is insufficient.",
                    criteria = request.AvailableWorkers
                }
            }
        });

        using var response = await httpClient.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JevResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Jev returned an empty response.");
        var answer = payload.Answers.NextWorker;
        var suggested = request.AvailableWorkers.ContainsKey(answer.Choice)
            ? answer.Choice
            : "human_review";
        var effective = options.Shadow || answer.Confidence < options.MinimumConfidence
            ? "human_review"
            : suggested;

        return new RoutingDecision(
            suggested,
            effective,
            answer.Confidence,
            options.Shadow,
            payload.Model,
            options.Shadow
                ? "Shadow mode records the suggestion but leaves execution with human review."
                : answer.Confidence < options.MinimumConfidence
                    ? "Confidence was below the configured threshold."
                    : "Jev selected a live worker above the confidence threshold.");
    }

    private static void Validate(RoutingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CorrelationId) ||
            string.IsNullOrWhiteSpace(request.WorkItemId) ||
            string.IsNullOrWhiteSpace(request.TaskCategory) ||
            string.IsNullOrWhiteSpace(request.RequiredCapability) ||
            string.IsNullOrWhiteSpace(request.KnowledgeRevision))
        {
            throw new ArgumentException("Routing metadata and knowledge revision are required.");
        }

        if (request.AvailableWorkers.Count == 0 ||
            !request.AvailableWorkers.ContainsKey("human_review"))
        {
            throw new ArgumentException("The live worker menu must include human_review.");
        }
    }

    private sealed record JevResponse(string Model, JevAnswers Answers);
    private sealed record JevAnswers(
        [property: System.Text.Json.Serialization.JsonPropertyName("next_worker")]
        JevChoice NextWorker);
    private sealed record JevChoice(string Choice, double Confidence);
}
