import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../../core/services/api.service';
import { AuthService } from '../../core/services/auth.service';
import { ApiKeyProvider, KeyStatusesDto } from '../../core/models/dtos';

interface WizardStep {
  provider: ApiKeyProvider;
  title: string;
  description: string;
  helpUrl: string;
  fields: { key: string; label: string; placeholder: string }[];
}

const STEPS: WizardStep[] = [
  {
    provider: 'Finnhub',
    title: 'Finnhub',
    description: 'Powers live quotes and earnings data for every signal the Research agent scores.',
    helpUrl: 'https://finnhub.io/dashboard',
    fields: [{ key: 'Finnhub', label: 'API key', placeholder: 'Paste your Finnhub API key' }],
  },
  {
    provider: 'Tiingo',
    title: 'Tiingo',
    description: 'Provides historical candles used for indicators and relative-strength scoring.',
    helpUrl: 'https://www.tiingo.com/account/api/token',
    fields: [{ key: 'Tiingo', label: 'API key', placeholder: 'Paste your Tiingo API token' }],
  },
  {
    provider: 'Trading212Key',
    title: 'Trading 212',
    description:
      'Connects your Trading 212 account so the system can read your portfolio and (once you enable live trading) place orders. Start on a demo/practice account.',
    helpUrl: 'https://www.trading212.com/en/api-docs',
    fields: [
      { key: 'Trading212Key', label: 'API key', placeholder: 'Paste your Trading 212 API key' },
      { key: 'Trading212Secret', label: 'API secret', placeholder: 'Paste your Trading 212 API secret' },
    ],
  },
  {
    provider: 'Claude',
    title: 'Claude (Anthropic)',
    description: 'Used for research narratives, watchlist selection, and refinement commentary.',
    helpUrl: 'https://console.anthropic.com/settings/keys',
    fields: [{ key: 'Claude', label: 'API key', placeholder: 'Paste your Anthropic API key' }],
  },
];

@Component({
  selector: 'app-onboarding',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './onboarding.component.html',
  styleUrl: './onboarding.component.scss',
})
export class OnboardingComponent {
  private api = inject(ApiService);
  private router = inject(Router);
  private snackbar = inject(MatSnackBar);
  auth = inject(AuthService);

  steps = STEPS;
  currentIndex = signal(0);
  values: Record<string, string> = {};
  saving = signal(false);
  keyStatuses = signal<KeyStatusesDto | null>(null);

  currentStep = () => this.steps[this.currentIndex()];
  isLastStep = () => this.currentIndex() === this.steps.length - 1;

  constructor() {
    this.api.getKeyStatuses().subscribe({ next: (s) => this.keyStatuses.set(s) });
  }

  isStepComplete(step: WizardStep): boolean {
    const statuses = this.keyStatuses();
    if (!statuses) return false;
    return step.fields.every((f) => statuses[f.key as ApiKeyProvider] !== 'NotSet');
  }

  canProceed(): boolean {
    const step = this.currentStep();
    return step.fields.every((f) => (this.values[f.key] ?? '').trim().length > 0) || this.isStepComplete(step);
  }

  async saveAndContinue(): Promise<void> {
    const step = this.currentStep();

    if (!this.isStepComplete(step)) {
      this.saving.set(true);
      try {
        for (const field of step.fields) {
          const value = (this.values[field.key] ?? '').trim();
          if (!value) continue;
          await firstValueFrom(this.api.saveKey(field.key, value));
        }
        const statuses = await firstValueFrom(this.api.getKeyStatuses());
        this.keyStatuses.set(statuses);
      } catch {
        this.snackbar.open('Failed to save — check the key and try again.', 'Dismiss', { duration: 4000 });
        this.saving.set(false);
        return;
      }
      this.saving.set(false);
    }

    if (this.isLastStep()) {
      this.router.navigateByUrl('/dashboard');
    } else {
      this.currentIndex.set(this.currentIndex() + 1);
    }
  }

  goBack(): void {
    if (this.currentIndex() > 0) this.currentIndex.set(this.currentIndex() - 1);
  }
}
