namespace MultiAgentTravelPlanner.Models;

public record ChatRequest(string Message, string? SessionId, string? RoutingMode);

public record ChatResponse(string SessionId, string Reply, string RoutingMode);
