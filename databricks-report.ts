import { Component, OnInit, OnDestroy, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import {
  ApplicationService,
  DashboardSummary,
  WorkspaceInfo,
  EmbedTokenResponse
} from '../../services/application';
import { Subscription } from 'rxjs';
import { DatabricksDashboard as DatabricksDashboardNpm } from '@databricks/aibi-client';
import { ApiResponse } from '../../services/base.service';

@Component({
  selector: 'app-databricks-report',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './databricks-report.html',
  styleUrls: ['./databricks-report.scss']
})
export class DatabricksReportComponent implements OnInit, OnDestroy {

  private sanitizer = inject(DomSanitizer);
  private applicationService = inject(ApplicationService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);

  workspaces: WorkspaceInfo[] = [];
  selectedWorkspace: WorkspaceInfo | null = null;

  instanceUrl: string = '';
  workspaceId: string = '';

  dashboards: DashboardSummary[] = [];
  selectedDashboardId: string = '';
  selectedDashboardLabel: string = '';

  currentViewMode: 'library' | 'iframe' = 'library';

  dashboardIframeUrl: SafeResourceUrl | null = null;
  isLoading = false;
  errorMessage = '';
  showIframe = false;
  showContainer = false;

  private aibiClientPromise: Promise<{ DatabricksDashboard: any }> | null = null;
  private queryParamSub?: Subscription;
  private configSub?: Subscription;
  private tokenSub?: Subscription;
  private loadTimeout: any = null;

  ngOnInit(): void {
    window.addEventListener('error', this.onWindowError);
    window.addEventListener('unhandledrejection', this.onUnhandledRejection);
    this.queryParamSub = this.route.queryParamMap.subscribe(params => {
      const mode = params.get('view');
      const newViewMode = mode === 'iframe' ? 'iframe' : 'library';

      // Check if view mode changed (not on initial load)
      const viewModeChanged = this.currentViewMode !== newViewMode && this.selectedDashboardId;
      this.currentViewMode = newViewMode;

      // Check if workspace changed via URL (e.g., browser back/forward)
      const workspaceIdFromUrl = params.get('workspaceId');
      if (workspaceIdFromUrl && this.selectedWorkspace?.workspaceId !== workspaceIdFromUrl && this.workspaces.length > 0) {
        const matchingWorkspace = this.workspaces.find(w => w.workspaceId === workspaceIdFromUrl);
        if (matchingWorkspace) {
          this.applyWorkspace(matchingWorkspace, true);
          return;
        }
      }

      // Check if dashboard ID changed via URL (e.g., browser back/forward)
      const dashboardIdFromUrl = params.get('dashboardId');
      if (dashboardIdFromUrl && dashboardIdFromUrl !== this.selectedDashboardId && this.dashboards.length > 0) {
        const matchingDashboard = this.dashboards.find(d => d.dashboardId === dashboardIdFromUrl);
        if (matchingDashboard) {
          this.selectedDashboardId = matchingDashboard.dashboardId;
          this.selectedDashboardLabel = matchingDashboard.label;
          this.loadSelectedDashboard();
          return;
        }
      }

      // Reload if only view mode changed
      if (viewModeChanged) {
        this.loadSelectedDashboard();
      }
    });

    this.loadDashboardConfig();
  }

  ngOnDestroy(): void {
    window.removeEventListener('error', this.onWindowError);
    window.removeEventListener('unhandledrejection', this.onUnhandledRejection);
    if (this.queryParamSub) this.queryParamSub.unsubscribe();
    if (this.configSub) this.configSub.unsubscribe();
    if (this.tokenSub) this.tokenSub.unsubscribe();
    if (this.loadTimeout) clearTimeout(this.loadTimeout);
  }

  private onWindowError = (ev: ErrorEvent) => {
    this.showError(ev?.error ?? ev?.message ?? ev);
  };

  private onUnhandledRejection = (ev: PromiseRejectionEvent) => {
    this.showError(ev?.reason ?? ev);
  };

  private ensurePrereqs(context: string): void {
    if (!this.instanceUrl || !this.workspaceId) {
      throw new Error(`${context}: dashboard configuration missing`);
    }

    if (!this.selectedDashboardId) {
      throw new Error(`${context}: dashboardId missing`);
    }
  }

  private loadDashboardConfig(): void {
    this.hideError();

    if (this.configSub) this.configSub.unsubscribe();
    this.configSub = this.applicationService.getDashboards().subscribe({
      next: (response) => {
        if (!response.success || !response.data) {
          this.showError(response.message || 'Failed to load dashboards');
          return;
        }

        this.workspaces = response.data.workspaces || [];

        if (this.workspaces.length === 0) {
          this.showError('No workspaces configured in backend appsettings.json');
          return;
        }

        // Find workspace from URL or use first
        const savedWorkspaceId = this.route.snapshot.queryParamMap.get('workspaceId');
        const matchingWorkspace = savedWorkspaceId
          ? this.workspaces.find(w => w.workspaceId === savedWorkspaceId)
          : null;

        this.applyWorkspace(matchingWorkspace || this.workspaces[0], true);
      },
      error: (err: any) => this.showError(err)
    });
  }

  selectWorkspace(workspace: WorkspaceInfo): void {
    this.hideError();
    this.clearAll();
    this.applyWorkspace(workspace);
  }

  private applyWorkspace(workspace: WorkspaceInfo, restoreFromUrl = false): void {
    this.selectedWorkspace = workspace;
    this.instanceUrl = workspace.instanceUrl;
    this.workspaceId = workspace.workspaceId;
    this.dashboards = workspace.dashboards || [];

    if (this.dashboards.length === 0) {
      this.selectedDashboardId = '';
      this.selectedDashboardLabel = '';
      return;
    }

    let dashboardToSelect: DashboardSummary | null = null;
    if (restoreFromUrl) {
      const savedDashboardId = this.route.snapshot.queryParamMap.get('dashboardId');
      dashboardToSelect = savedDashboardId
        ? this.dashboards.find(d => d.dashboardId === savedDashboardId) || null
        : null;
    }

    this.selectDashboard(dashboardToSelect || this.dashboards[0]);
  }

  private updateIframeUrl(): void {
    this.ensurePrereqs('updateIframeUrl');
    const url =
      `${this.instanceUrl}/embed/dashboardsv3/${this.selectedDashboardId}?o=${this.workspaceId}`;
    this.dashboardIframeUrl =
      this.sanitizer.bypassSecurityTrustResourceUrl(url);
  }

  onDashboardChange(): void {
    this.hideError();
    this.loadSelectedDashboard();
  }

  loadSelectedDashboard(): void {
    if (!this.selectedDashboardId) {
      return;
    }

    if (this.currentViewMode === 'iframe') {
      this.loadIframeView();
      return;
    }

    this.loadTokenView();
  }

  selectDashboard(dashboard: DashboardSummary): void {
    this.selectedDashboardId = dashboard.dashboardId;
    this.selectedDashboardLabel = dashboard.label;

    // Persist selected workspace + dashboard in query params for page refresh
    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        workspaceId: this.selectedWorkspace?.workspaceId,
        dashboardId: dashboard.dashboardId
      },
      queryParamsHandling: 'merge'
    });

    this.onDashboardChange();
  }

  private showError(error: unknown): void {
    this.errorMessage = this.extractErrorMessage(error);
    this.isLoading = false;
  }

  private extractErrorMessage(error: unknown): string {
    if (typeof error === 'string') {
      return error;
    }

    if (error instanceof Error) {
      return error.message;
    }

    if (error && typeof error === 'object') {
      const errObj = error as {
        message?: string;
        error?: { message?: string } | string;
        status?: number;
        statusText?: string;
      };

      if (typeof errObj.error === 'string' && errObj.error.trim()) {
        return errObj.error;
      }

      if (errObj.error && typeof errObj.error === 'object' && 'message' in errObj.error) {
        const nestedMessage = errObj.error.message;
        if (typeof nestedMessage === 'string' && nestedMessage.trim()) {
          return nestedMessage;
        }
      }

      if (errObj.message) {
        return errObj.message;
      }

      if (typeof errObj.status === 'number') {
        if (errObj.status === 0) {
          return 'Unable to reach backend API. Ensure backend is running and accessible.';
        }

        const statusText = errObj.statusText ? ` ${errObj.statusText}` : '';
        return `API request failed (${errObj.status}${statusText}).`;
      }

      try {
        return JSON.stringify(error);
      } catch {
        return 'Unexpected error';
      }
    }

    return 'Unexpected error';
  }

  private hideError(): void {
    this.errorMessage = '';
  }

  clearAll(): void {
    this.showIframe = false;
    this.showContainer = false;
    this.isLoading = false;
    this.hideError();
    if (this.loadTimeout) clearTimeout(this.loadTimeout);
    const container = document.getElementById('dashboard-container');
    if (container) container.innerHTML = '';
  }

  loadIframeView(): void {
    this.hideError();
    this.showIframe = true;
    this.showContainer = false;
    this.updateIframeUrl();
  }

  loadTokenView(): void {
    this.ensurePrereqs('loadTokenView');
    this.hideError();

    this.isLoading = true;
    this.showIframe = false;
    this.showContainer = true;

    const container = document.getElementById('dashboard-container');
    if (container) {
      container.innerHTML =
        '<p style="padding:20px;text-align:center;">Loading...</p>';
    }

    if (this.loadTimeout) clearTimeout(this.loadTimeout);
    this.loadTimeout = setTimeout(() => {
      this.showError('Dashboard load timed out');
    }, 30000);

    if (this.tokenSub) this.tokenSub.unsubscribe();

    this.tokenSub = this.applicationService
      .getEmbedToken(this.selectedDashboardId, this.workspaceId)
      .subscribe({
        next: (response: ApiResponse<EmbedTokenResponse>) => {
          const embedToken = response.data?.accessToken;
          if (!response.success || !embedToken) {
            this.showError(response.message || 'Embed token missing in response');
            return;
          }

          this.initializedDashboard(embedToken);
        },
        error: (err: any) => {
          this.showError(err);
        }
      });
  }

  private async initializedDashboard(token: string): Promise<void> {
    const container = document.getElementById('dashboard-container');
    if (!container) return;

    container.innerHTML = '';

    const { DatabricksDashboard } = await this.loadAibiClient();

    const dashboard = new DatabricksDashboard({
      instanceUrl: this.instanceUrl,
      workspaceId: this.workspaceId,
      dashboardId: this.selectedDashboardId,
      token,
      container
    });

    await dashboard.initialize();

    if (this.loadTimeout) clearTimeout(this.loadTimeout);
    this.isLoading = false;
  }

  private loadAibiClient(): Promise<{ DatabricksDashboard: any }> {
    if (this.aibiClientPromise) return this.aibiClientPromise;

    this.aibiClientPromise = new Promise(async (resolve, reject) => {
      try {
        if (DatabricksDashboardNpm) {
          resolve({ DatabricksDashboard: DatabricksDashboardNpm });
          return;
        }
      } catch { }

      try {
        const script = document.createElement("script");
        script.src =
          "https://cdn.jsdelivr.net/npm/@databricks/aibi-client/dist/index.js";
        script.type = "module";

        script.onload = () => {
          const DatabricksDashboard = (window as any).DatabricksDashboard;
          if (!DatabricksDashboard) {
            reject("CDN loaded but dashboard not available");
            return;
          }
          resolve({ DatabricksDashboard });
        };

        script.onerror = () => reject("CDN load failed");
        document.body.appendChild(script);

      } catch (err) {
        reject(err);
      }
    });

    return this.aibiClientPromise;
  }

  handleIframeLoad(): void { }
  handleIframeError(): void {
    this.showError('Failed to load iframe');
  }
}
