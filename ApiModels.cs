using System.ComponentModel.DataAnnotations;

namespace UnifiedPOCAPI.Models;

public class ApiResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public object? Data { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public static ApiResponse CreateSuccess(object? data, string? message = null) =>
        new() { Success = true, Message = message, Data = data };

    public static ApiResponse CreateFailure(string message, object? data = null) =>
        new() { Success = false, Message = message, Data = data };
}

public class DatabricksOptions
{
    [Required] public List<WorkspaceConfig> Workspaces { get; set; } = [];
}

public class WorkspaceConfig
{
    [Required] public string InstanceUrl { get; set; } = string.Empty;
    [Required] public string WorkspaceId { get; set; } = string.Empty;
    [Required] public string ClientId { get; set; } = string.Empty;
    [Required] public string ClientSecret { get; set; } = string.Empty;
    [Required] public List<DashboardEntry> Dashboards { get; set; } = [];
    public string? ExternalViewerId { get; set; }
    public string? ExternalValue { get; set; }
}

public class DashboardEntry
{
    [Required] public string Id { get; set; } = string.Empty;
    [Required] public string Name { get; set; } = string.Empty;
}

public class DashboardSummary
{
    public string DashboardId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string InstanceUrl { get; set; } = string.Empty;
    public string WorkspaceId { get; set; } = string.Empty;
}

public class DashboardConfigResponse
{
    public List<DashboardSummary> Dashboards { get; set; } = [];
}

public class EmbedTokenRequest
{
    [Required] public string DashboardId { get; set; } = string.Empty;
}
