import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { BaseService, ApiResponse } from './base.service';

export interface DashboardSummary {
  dashboardId: string;
  label: string;
}

export interface WorkspaceInfo {
  workspaceId: string;
  name: string;
  instanceUrl: string;
  dashboards: DashboardSummary[];
}

export interface AllDashboardsConfigResponse {
  workspaces: WorkspaceInfo[];
}

export interface EmbedTokenResponse {
  accessToken: string;
}

@Injectable({
  providedIn: 'root'
})
export class ApplicationService extends BaseService {
  getDashboards(): Observable<ApiResponse<AllDashboardsConfigResponse>> {
    return this.get<ApiResponse<AllDashboardsConfigResponse>>('application/databricks/dashboards');
  }

  getEmbedToken(
    dashboardId: string,
    workspaceId: string
  ): Observable<ApiResponse<EmbedTokenResponse>> {
    return this.post<ApiResponse<EmbedTokenResponse>>('application/databricks/embedToken', {
      dashboardId,
      workspaceId
    });
  }

  getADUserPhoto(upn: string): Observable<any> {
    return this.post<any>('application/user/photo', { upn });
  }
}
