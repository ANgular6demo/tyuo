using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UnifiedPOCAPI.Models;

namespace UnifiedPOCAPI.Controllers;

[ApiController]
[Route("api/application")]
public class ApplicationController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<DatabricksOptions> _optionsMonitor;
    private readonly IOptionsMonitor<MicrosoftGraphOptions> _graphOptionsMonitor;

    // Simple in-memory token cache for Graph API (POC only)
    private static string? _cachedGraphToken;
    private static DateTime _graphTokenExpiry = DateTime.MinValue;
    private static readonly SemaphoreSlim _tokenLock = new(1, 1);

    public ApplicationController(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<DatabricksOptions> optionsMonitor,
        IOptionsMonitor<MicrosoftGraphOptions> graphOptionsMonitor)
    {
        _httpClientFactory = httpClientFactory;
        _optionsMonitor = optionsMonitor;
        _graphOptionsMonitor = graphOptionsMonitor;
    }

    [HttpGet("databricks/dashboards")]
    public ActionResult<ApiResponse> GetDashboards()
    {
        var options = _optionsMonitor.CurrentValue;
        var workspaces = new List<WorkspaceInfo>();

        // Primary: workspaces from array
        if (options.Workspaces is { Count: > 0 })
        {
            foreach (var ws in options.Workspaces)
            {
                var wsDashboards = ResolveDashboardSummaries(ws.Dashboards, ws.DashboardIds);
                if (!string.IsNullOrWhiteSpace(ws.InstanceUrl) &&
                    !string.IsNullOrWhiteSpace(ws.WorkspaceId) &&
                    wsDashboards.Count > 0)
                {
                    workspaces.Add(new WorkspaceInfo
                    {
                        WorkspaceId = ws.WorkspaceId,
                        Name = string.IsNullOrWhiteSpace(ws.Name) ? ws.WorkspaceId : ws.Name,
                        InstanceUrl = ws.InstanceUrl,
                        Dashboards = wsDashboards
                    });
                }
            }
        }

        // Fallback: flat config at Databricks root (backward compat)
        if (workspaces.Count == 0)
        {
            var defaultDashboards = ResolveDashboardSummaries(options.Dashboards, options.DashboardIds);
            if (!string.IsNullOrWhiteSpace(options.InstanceUrl) &&
                !string.IsNullOrWhiteSpace(options.WorkspaceId) &&
                defaultDashboards.Count > 0)
            {
                workspaces.Add(new WorkspaceInfo
                {
                    WorkspaceId = options.WorkspaceId,
                    Name = string.IsNullOrWhiteSpace(options.Name) ? "Default" : options.Name,
                    InstanceUrl = options.InstanceUrl,
                    Dashboards = defaultDashboards
                });
            }
        }

        if (workspaces.Count == 0)
        {
            return BadRequest(ApiResponse.CreateFailure(
                "No valid Databricks workspace with dashboards configured"));
        }

        return Ok(ApiResponse.CreateSuccess(new AllDashboardsConfigResponse { Workspaces = workspaces }));
    }

    [HttpPost("databricks/embedToken")]
    public async Task<ActionResult<ApiResponse>> GetEmbedTokenAsync([FromBody] EmbedTokenRequest request)
    {
        var workspace = ResolveWorkspace(request.WorkspaceId);
        if (workspace == null)
        {
            return BadRequest(ApiResponse.CreateFailure("Workspace not found or missing configuration"));
        }

        var configuredIds = ResolveDashboardIdsList(workspace.Dashboards, workspace.DashboardIdsRaw);
        if (!configuredIds.Any(id => id.Equals(request.DashboardId, StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest(ApiResponse.CreateFailure("Selected dashboardId is not configured for this workspace"));
        }

        try
        {
            var oidcToken = await RequestOidcAllApisTokenAsync(workspace);
            var tokenInfo = await RequestTokenInfoAsync(oidcToken, request.DashboardId, workspace);

            if (!tokenInfo.RootElement.TryGetProperty("authorization_details", out var authorizationDetails))
            {
                return BadRequest(ApiResponse.CreateFailure("authorization_details missing in tokenInfo response"));
            }

            var scope = tokenInfo.RootElement.TryGetProperty("scope", out var scopeElement)
                ? scopeElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(scope))
            {
                return BadRequest(ApiResponse.CreateFailure("scope missing in tokenInfo response"));
            }

            var customClaim = tokenInfo.RootElement.TryGetProperty("custom_claim", out var customClaimElement)
                ? customClaimElement.GetString()
                : null;

            var embedToken = await RequestScopedTokenAsync(
                authorizationDetails.GetRawText(),
                scope,
                customClaim,
                workspace);

            return Ok(ApiResponse.CreateSuccess(new
            {
                accessToken = embedToken
            }));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse.CreateFailure(ex.Message));
        }
    }

    private async Task<string> RequestOidcAllApisTokenAsync(ResolvedWorkspace workspace)
    {
        var basicAuth = Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"{workspace.ClientId}:{workspace.ClientSecret}"));

        var form = new Dictionary<string, string>
        {
            { "grant_type", "client_credentials" },
            { "scope", "all-apis" }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{workspace.InstanceUrl}/oidc/v1/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
        req.Content = new FormUrlEncodedContent(form);

        using var document = await SendDatabricksRequestAsync(req);
        if (!document.RootElement.TryGetProperty("access_token", out var tokenElement))
        {
            throw new InvalidOperationException("access_token missing from OIDC response");
        }

        return tokenElement.GetString() ?? throw new InvalidOperationException("access_token was empty");
    }

    // ─── Microsoft Graph: Get AD User Photo by UPN ─────────────────────────

    [HttpPost("user/photo")]
    public async Task<ActionResult<ApiResponse>> GetADUserPhoto([FromBody] ADUserPhotoRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Upn))
            return Ok(ApiResponse.CreateFailure("UPN is required"));

        try
        {
            var graphToken = await GetGraphTokenAsync();
            var client = _httpClientFactory.CreateClient();

            var url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(request.Upn)}/photo/$value";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", graphToken);

            using var response = await client.SendAsync(req);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                return Ok(new ADUserPhotoResponse
                {
                    Success = false,
                    StatusCode = (int)response.StatusCode,
                    Error = errorContent
                });
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            return Ok(new ADUserPhotoResponse
            {
                Success = true,
                Data = $"data:{contentType};base64,{Convert.ToBase64String(bytes)}"
            });
        }
        catch (Exception ex)
        {
            return Ok(ApiResponse.CreateFailure($"Error fetching user photo: {ex.Message}"));
        }
    }

    private async Task<string> GetGraphTokenAsync()
    {
        await _tokenLock.WaitAsync();
        try
        {
            if (!string.IsNullOrEmpty(_cachedGraphToken) && DateTime.UtcNow < _graphTokenExpiry.AddMinutes(-5))
                return _cachedGraphToken;

            var graphOptions = _graphOptionsMonitor.CurrentValue;
            var client = _httpClientFactory.CreateClient();

            var form = new Dictionary<string, string>
            {
                ["client_id"] = graphOptions.ClientId,
                ["client_secret"] = graphOptions.ClientSecret,
                ["scope"] = graphOptions.Scope,
                ["grant_type"] = "client_credentials"
            };

            var tokenUrl = $"{graphOptions.TokenEndpoint}/{graphOptions.TenantId}/oauth2/v2.0/token";
            using var response = await client.PostAsync(tokenUrl, new FormUrlEncodedContent(form));
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            _cachedGraphToken = doc.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("Graph access_token was empty");
            _graphTokenExpiry = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32());

            return _cachedGraphToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<JsonDocument> RequestTokenInfoAsync(
        string oidcToken,
        string dashboardId,
        ResolvedWorkspace workspace)
    {
        var url =
            $"{workspace.InstanceUrl}/api/2.0/lakeview/dashboards/{dashboardId}/published/tokeninfo" +
            $"?external_viewer_id={Uri.EscapeDataString(workspace.ExternalViewerId ?? string.Empty)}" +
            $"&external_value={Uri.EscapeDataString(workspace.ExternalValue ?? string.Empty)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oidcToken);

        return await SendDatabricksRequestAsync(req);
    }

    private async Task<string> RequestScopedTokenAsync(
        string authorizationDetails,
        string scope,
        string? customClaim,
        ResolvedWorkspace workspace)
    {
        var basicAuth = Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"{workspace.ClientId}:{workspace.ClientSecret}"));

        var form = new Dictionary<string, string>
        {
            { "grant_type", "client_credentials" },
            { "authorization_details", authorizationDetails },
            { "scope", scope }
        };

        if (!string.IsNullOrWhiteSpace(customClaim))
        {
            form["custom_claim"] = customClaim;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{workspace.InstanceUrl}/oidc/v1/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
        req.Content = new FormUrlEncodedContent(form);

        using var document = await SendDatabricksRequestAsync(req);
        if (!document.RootElement.TryGetProperty("access_token", out var tokenElement))
        {
            throw new InvalidOperationException("access_token missing from scoped token response");
        }

        return tokenElement.GetString() ?? throw new InvalidOperationException("scoped access_token was empty");
    }

    private async Task<JsonDocument> SendDatabricksRequestAsync(HttpRequestMessage request)
    {
        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Databricks request failed with status {(int)response.StatusCode}: {responseContent}");
        }

        try
        {
            return JsonDocument.Parse(responseContent);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Databricks response was not valid JSON");
        }
    }

    private sealed record ResolvedWorkspace(
        string InstanceUrl,
        string ClientId,
        string ClientSecret,
        string? ExternalViewerId,
        string? ExternalValue,
        List<DashboardEntry>? Dashboards,
        string? DashboardIdsRaw);

    private ResolvedWorkspace? ResolveWorkspace(string? workspaceId)
    {
        var options = _optionsMonitor.CurrentValue;

        // Primary: search Workspaces array
        if (!string.IsNullOrWhiteSpace(workspaceId) && options.Workspaces is { Count: > 0 })
        {
            var ws = options.Workspaces.FirstOrDefault(w =>
                w.WorkspaceId.Equals(workspaceId, StringComparison.OrdinalIgnoreCase));

            if (ws != null &&
                !string.IsNullOrWhiteSpace(ws.InstanceUrl) &&
                !string.IsNullOrWhiteSpace(ws.ClientId) &&
                !string.IsNullOrWhiteSpace(ws.ClientSecret))
            {
                return new ResolvedWorkspace(
                    ws.InstanceUrl, ws.ClientId, ws.ClientSecret,
                    ws.ExternalViewerId, ws.ExternalValue,
                    ws.Dashboards, ws.DashboardIds);
            }
        }

        // Fallback: flat config at Databricks root (backward compat)
        if (string.IsNullOrWhiteSpace(workspaceId) ||
            workspaceId.Equals(options.WorkspaceId, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.InstanceUrl) ||
                string.IsNullOrWhiteSpace(options.ClientId) ||
                string.IsNullOrWhiteSpace(options.ClientSecret))
                return null;

            return new ResolvedWorkspace(
                options.InstanceUrl, options.ClientId, options.ClientSecret,
                options.ExternalViewerId, options.ExternalValue,
                options.Dashboards, options.DashboardIds);
        }

        return null;
    }

    private static List<DashboardSummary> ResolveDashboardSummaries(
        List<DashboardEntry>? dashboards, string? dashboardIds)
    {
        if (dashboards is { Count: > 0 })
        {
            return dashboards
                .Where(d => !string.IsNullOrWhiteSpace(d.Id))
                .Select(d => new DashboardSummary
                {
                    DashboardId = d.Id.Trim(),
                    Label = string.IsNullOrWhiteSpace(d.Name) ? d.Id : d.Name
                })
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(dashboardIds)) return [];

        return ParseDashboardIds(dashboardIds)
            .Select((id, index) => new DashboardSummary
            {
                DashboardId = id,
                Label = $"Report {index + 1}"
            })
            .ToList();
    }

    private static List<string> ResolveDashboardIdsList(
        List<DashboardEntry>? dashboards, string? dashboardIds)
    {
        if (dashboards is { Count: > 0 })
        {
            return dashboards
                .Where(d => !string.IsNullOrWhiteSpace(d.Id))
                .Select(d => d.Id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(dashboardIds)) return [];
        return ParseDashboardIds(dashboardIds);
    }

    private static List<string> ParseDashboardIds(string dashboardIds)
    {
        return dashboardIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
