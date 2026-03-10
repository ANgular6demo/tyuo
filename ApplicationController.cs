using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UnifiedPOCAPI.Models;

namespace UnifiedPOCAPI.Controllers;

[ApiController]
[Route("api/application")]
public class ApplicationController(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<DatabricksOptions> optionsMonitor) : ControllerBase
{
    [HttpGet("databricks/dashboards")]
    public ActionResult<ApiResponse> GetDashboards()
    {
        var workspaces = optionsMonitor.CurrentValue.Workspaces;
        if (workspaces == null || workspaces.Count == 0)
            return BadRequest(ApiResponse.CreateFailure("No Databricks workspaces configured"));

        var dashboards = new List<DashboardSummary>();
        var index = 0;

        foreach (var ws in workspaces.Where(ws => IsWorkspaceValid(ws)))
        {
            foreach (var entry in ws.Dashboards.Where(e => !string.IsNullOrWhiteSpace(e.Id)))
            {
                dashboards.Add(new DashboardSummary
                {
                    DashboardId = entry.Id,
                    Label = string.IsNullOrWhiteSpace(entry.Name) ? $"Report {++index}" : entry.Name,
                    InstanceUrl = ws.InstanceUrl,
                    WorkspaceId = ws.WorkspaceId
                });
            }
        }

        return dashboards.Count == 0
            ? BadRequest(ApiResponse.CreateFailure("No valid dashboard configurations found"))
            : Ok(ApiResponse.CreateSuccess(new DashboardConfigResponse { Dashboards = dashboards }));
    }

    [HttpPost("databricks/embedToken")]
    public async Task<ActionResult<ApiResponse>> GetEmbedTokenAsync([FromBody] EmbedTokenRequest request)
    {
        var workspace = FindWorkspaceForDashboard(request.DashboardId);
        if (workspace == null)
            return BadRequest(ApiResponse.CreateFailure("Selected dashboardId is not configured in any workspace"));

        try
        {
            var oidcToken = await RequestOidcAllApisTokenAsync(workspace);
            var tokenInfo = await RequestTokenInfoAsync(
                workspace, oidcToken, request.DashboardId,
                workspace.ExternalViewerId, workspace.ExternalValue);

            if (!tokenInfo.RootElement.TryGetProperty("authorization_details", out var authDetails))
                return BadRequest(ApiResponse.CreateFailure("authorization_details missing in tokenInfo response"));

            var scope = tokenInfo.RootElement.TryGetProperty("scope", out var s) ? s.GetString() : null;
            if (string.IsNullOrWhiteSpace(scope))
                return BadRequest(ApiResponse.CreateFailure("scope missing in tokenInfo response"));

            var customClaim = tokenInfo.RootElement.TryGetProperty("custom_claim", out var c) ? c.GetString() : null;
            var embedToken = await RequestScopedTokenAsync(workspace, authDetails.GetRawText(), scope, customClaim);

            return Ok(ApiResponse.CreateSuccess(new { accessToken = embedToken }));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse.CreateFailure(ex.Message));
        }
    }

    private async Task<string> RequestOidcAllApisTokenAsync(WorkspaceConfig ws)
    {
        var form = new Dictionary<string, string>
        {
            { "grant_type", "client_credentials" },
            { "scope", "all-apis" }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ws.InstanceUrl}/oidc/v1/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", GetBasicAuth(ws));
        req.Content = new FormUrlEncodedContent(form);

        using var doc = await SendDatabricksRequestAsync(req);
        return doc.RootElement.TryGetProperty("access_token", out var t)
            ? t.GetString() ?? throw new InvalidOperationException("access_token was empty")
            : throw new InvalidOperationException("access_token missing from OIDC response");
    }

    private async Task<JsonDocument> RequestTokenInfoAsync(
        WorkspaceConfig ws, string oidcToken, string dashboardId,
        string? externalViewerId, string? externalValue)
    {
        var url = $"{ws.InstanceUrl}/api/2.0/lakeview/dashboards/{dashboardId}/published/tokeninfo" +
            $"?external_viewer_id={Uri.EscapeDataString(externalViewerId ?? string.Empty)}" +
            $"&external_value={Uri.EscapeDataString(externalValue ?? string.Empty)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oidcToken);
        return await SendDatabricksRequestAsync(req);
    }

    private async Task<string> RequestScopedTokenAsync(
        WorkspaceConfig ws, string authorizationDetails, string scope, string? customClaim)
    {
        var form = new Dictionary<string, string>
        {
            { "grant_type", "client_credentials" },
            { "authorization_details", authorizationDetails },
            { "scope", scope }
        };
        if (!string.IsNullOrWhiteSpace(customClaim)) form["custom_claim"] = customClaim;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ws.InstanceUrl}/oidc/v1/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", GetBasicAuth(ws));
        req.Content = new FormUrlEncodedContent(form);

        using var doc = await SendDatabricksRequestAsync(req);
        return doc.RootElement.TryGetProperty("access_token", out var t)
            ? t.GetString() ?? throw new InvalidOperationException("scoped access_token was empty")
            : throw new InvalidOperationException("access_token missing from scoped token response");
    }

    private async Task<JsonDocument> SendDatabricksRequestAsync(HttpRequestMessage request)
    {
        var client = httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Databricks request failed with status {(int)response.StatusCode}: {content}");

        try { return JsonDocument.Parse(content); }
        catch (JsonException) { throw new InvalidOperationException("Databricks response was not valid JSON"); }
    }

    private static string GetBasicAuth(WorkspaceConfig ws) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes($"{ws.ClientId}:{ws.ClientSecret}"));

    private WorkspaceConfig? FindWorkspaceForDashboard(string dashboardId) =>
        optionsMonitor.CurrentValue.Workspaces?.FirstOrDefault(ws =>
            ws.Dashboards.Any(d => d.Id.Equals(dashboardId, StringComparison.OrdinalIgnoreCase)));

    private static bool IsWorkspaceValid(WorkspaceConfig ws) =>
        !string.IsNullOrWhiteSpace(ws.InstanceUrl) &&
        !string.IsNullOrWhiteSpace(ws.WorkspaceId) &&
        !string.IsNullOrWhiteSpace(ws.ClientId) &&
        !string.IsNullOrWhiteSpace(ws.ClientSecret) &&
        ws.Dashboards?.Count > 0;
}
