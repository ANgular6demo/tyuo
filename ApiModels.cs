using System.ComponentModel.DataAnnotations;

namespace UnifiedPOCAPI.Models;

public class ApiResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public object? Data { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public static ApiResponse CreateSuccess(object? data, string? message = null)
    {
        return new ApiResponse
        {
            Success = true,
            Message = message,
            Data = data
        };
    }

    public static ApiResponse CreateFailure(string message, object? data = null)
    {
        return new ApiResponse
        {
            Success = false,
            Message = message,
            Data = data
        };
    }
}

public class DatabricksOptions
{
    [Required]
    public string InstanceUrl { get; set; } = string.Empty;

    [Required]
    public string WorkspaceId { get; set; } = string.Empty;

    [Required]
    public string ClientId { get; set; } = string.Empty;

    [Required]
    public string ClientSecret { get; set; } = string.Empty;

    public string Name { get; set; } = "Default";

    [Required]
    public string DashboardIds { get; set; } = string.Empty;

    public List<DashboardEntry> Dashboards { get; set; } = [];

    public string? ExternalViewerId { get; set; }

    public string? ExternalValue { get; set; }

    public List<WorkspaceEntry> Workspaces { get; set; } = [];
}

public class DashboardEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public class WorkspaceEntry
{
    public string Name { get; set; } = string.Empty;

    [Required]
    public string InstanceUrl { get; set; } = string.Empty;

    [Required]
    public string WorkspaceId { get; set; } = string.Empty;

    [Required]
    public string ClientId { get; set; } = string.Empty;

    [Required]
    public string ClientSecret { get; set; } = string.Empty;

    public string? DashboardIds { get; set; }

    public List<DashboardEntry> Dashboards { get; set; } = [];

    public string? ExternalViewerId { get; set; }

    public string? ExternalValue { get; set; }
}

public class DashboardSummary
{
    public string DashboardId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public class WorkspaceInfo
{
    public string WorkspaceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string InstanceUrl { get; set; } = string.Empty;
    public List<DashboardSummary> Dashboards { get; set; } = [];
}

public class AllDashboardsConfigResponse
{
    public List<WorkspaceInfo> Workspaces { get; set; } = [];
}

public class EmbedTokenRequest
{
    [Required]
    public string DashboardId { get; set; } = string.Empty;

    public string? WorkspaceId { get; set; }
}

public class MicrosoftGraphOptions
{
    [Required]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    public string ClientId { get; set; } = string.Empty;

    [Required]
    public string ClientSecret { get; set; } = string.Empty;

    public string Scope { get; set; } = "https://graph.microsoft.com/.default";

    public string TokenEndpoint { get; set; } = "https://login.microsoftonline.com";
}

public class ADUserPhotoRequest
{
    [Required]
    public string Upn { get; set; } = string.Empty;
}

public class ADUserPhotoResponse
{
    public bool Success { get; set; }
    public string? Data { get; set; }
    public int StatusCode { get; set; }
    public string? Error { get; set; }
}
