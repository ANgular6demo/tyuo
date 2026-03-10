import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { BaseService, ApiResponse } from './base.service';

export interface DashboardSummary {
  dashboardId: string;
  label: string;
  instanceUrl: string;
  workspaceId: string;
}

export interface DashboardConfigResponse {
  dashboards: DashboardSummary[];
}

export interface EmbedTokenResponse {
  accessToken: string;
}

@Injectable({
  providedIn: 'root'
})
export class ApplicationService extends BaseService {
  getDashboards(): Observable<ApiResponse<DashboardConfigResponse>> {
    return this.get<ApiResponse<DashboardConfigResponse>>('application/databricks/dashboards');
  }

  getEmbedToken(
    dashboardId: string
  ): Observable<ApiResponse<EmbedTokenResponse>> {
    return this.post<ApiResponse<EmbedTokenResponse>>('application/databricks/embedToken', {
      dashboardId
    });
  }
}
