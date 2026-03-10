import { Component, OnInit, OnDestroy, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import {
  ApplicationService,
  DashboardSummary,
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
      this.currentViewMode = mode === 'iframe' ? 'iframe' : 'library';

      if (this.selectedDashboardId) {
        this.loadSelectedDashboard();
      }
    });

    this.loadDashboardConfig();
  }

  ngOnDestroy(): void {
    window.removeEventListener('error', this.onWindowError);
    window.removeEventListener('unhandledrejection', this.onUnhandledRejection);
    [this.queryParamSub, this.configSub, this.tokenSub].forEach(s => s?.unsubscribe());
    if (this.loadTimeout) clearTimeout(this.loadTimeout);
  }

  private onWindowError = (ev: ErrorEvent) => this.showError(ev?.error ?? ev?.message ?? ev);
  private onUnhandledRejection = (ev: PromiseRejectionEvent) => this.showError(ev?.reason ?? ev);

  private ensurePrereqs(context: string): void {
    if (!this.instanceUrl || !this.workspaceId) throw new Error(`${context}: dashboard configuration missing`);
    if (!this.selectedDashboardId) throw new Error(`${context}: dashboardId missing`);
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

        this.dashboards = response.data.dashboards || [];

        if (this.dashboards.length === 0) {
          this.showError('No dashboard IDs configured in backend appsettings.json');
          return;
        }

        this.selectDashboard(this.dashboards[0]);
      },
      error: (err: any) => this.showError(err)
    });
  }

  private updateIframeUrl(): void {
    this.ensurePrereqs('updateIframeUrl');
    this.dashboardIframeUrl = this.sanitizer.bypassSecurityTrustResourceUrl(
      `${this.instanceUrl}/embed/dashboardsv3/${this.selectedDashboardId}?o=${this.workspaceId}`);
  }

  onDashboardChange(): void { this.hideError(); this.loadSelectedDashboard(); }

  loadSelectedDashboard(): void {
    if (!this.selectedDashboardId) return;
    this.currentViewMode === 'iframe' ? this.loadIframeView() : this.loadTokenView();
  }

  selectDashboard(dashboard: DashboardSummary): void {
    this.selectedDashboardId = dashboard.dashboardId;
    this.selectedDashboardLabel = dashboard.label;
    this.instanceUrl = dashboard.instanceUrl;
    this.workspaceId = dashboard.workspaceId;
    this.onDashboardChange();
  }

  private showError(error: unknown): void {
    this.errorMessage = this.extractErrorMessage(error);
    this.isLoading = false;
  }

  private extractErrorMessage(error: unknown): string {
    if (typeof error === 'string') return error;
    if (error instanceof Error) return error.message;
    if (error && typeof error === 'object') {
      const e = error as any;
      if (typeof e.error === 'string' && e.error.trim()) return e.error;
      if (e.error?.message?.trim()) return e.error.message;
      if (e.message) return e.message;
      if (typeof e.status === 'number') {
        return e.status === 0
          ? 'Unable to reach backend API. Ensure backend is running and accessible.'
          : `API request failed (${e.status}${e.statusText ? ' ' + e.statusText : ''}).`;
      }
      try { return JSON.stringify(error); } catch { return 'Unexpected error'; }
    }
    return 'Unexpected error';
  }

  private hideError(): void { this.errorMessage = ''; }

  clearAll(): void {
    this.showIframe = this.showContainer = this.isLoading = false;
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
      .getEmbedToken(this.selectedDashboardId)
      .subscribe({
        next: (response: ApiResponse<EmbedTokenResponse>) => {
          const embedToken = response.data?.accessToken;
          if (!response.success || !embedToken) {
            this.showError(response.message || 'Embed token missing in response');
            return;
          }
          this.initializedDashboard(embedToken);
        },
        error: (err: any) => this.showError(err)
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
  handleIframeError(): void { this.showError('Failed to load iframe'); }
}
