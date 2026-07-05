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

interface WizardField {
  key: ApiKeyProvider;
  label: string;
  placeholder: string;
  required: boolean;
}

interface WizardStep {
  title: string;
  description: string;
  helpUrl: string;
  fields: WizardField[];
}

const STEPS: WizardStep[] = [
  {
    title: 'Finnhub',
    description: 'Powers live quotes and earnings data for every signal the Research agent scores.',
    helpUrl: 'https://finnhub.io/dashboard',
    fields: [{ key: 'Finnhub', label: 'API key', placeholder: 'Paste your Finnhub API key', required: true }],
  },
  {
    title: 'Tiingo',
    description: 'Provides historical candles used for indicators and relative-strength scoring.',
    helpUrl: 'https://www.tiingo.com/account/api/token',
    fields: [{ key: 'Tiingo', label: 'API key', placeholder: 'Paste your Tiingo API token', required: true }],
  },
  {
    title: 'Trading 212',
    description:
      'Trading 212 issues separate API credentials for demo and live accounts, so both are stored independently. ' +
      'Your account starts in Demo mode, so the demo pair is required here — add your live pair now or later in ' +
      'Settings once you\'re ready to switch TradingMode to Live.',
    helpUrl: 'https://www.trading212.com/en/api-docs',
    fields: [
      { key: 'Trading212DemoKey', label: 'Demo API key', placeholder: 'Paste your Trading 212 demo API key', required: true },
      { key: 'Trading212DemoSecret', label: 'Demo API secret', placeholder: 'Paste your Trading 212 demo API secret', required: true },
      { key: 'Trading212LiveKey', label: 'Live API key (optional)', placeholder: 'Paste your Trading 212 live API key', required: false },
      { key: 'Trading212LiveSecret', label: 'Live API secret (optional)', placeholder: 'Paste your Trading 212 live API secret', required: false },
    ],
  },
  {
    title: 'Claude (Anthropic)',
    description: 'Used for research narratives, watchlist selection, and refinement commentary.',
    helpUrl: 'https://console.anthropic.com/settings/keys',
    fields: [{ key: 'Claude', label: 'API key', placeholder: 'Paste your Anthropic API key', required: true }],
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

  // "Complete" only considers required fields - an optional field (e.g. the
  // Live Trading212 pair) left unset shouldn't block the step or the wizard.
  isStepComplete(step: WizardStep): boolean {
    const statuses = this.keyStatuses();
    if (!statuses) return false;
    return step.fields.filter((f) => f.required).every((f) => statuses[f.key] !== 'NotSet');
  }

  canProceed(): boolean {
    const step = this.currentStep();
    const statuses = this.keyStatuses();
    return step.fields
      .filter((f) => f.required)
      .every((f) => (this.values[f.key] ?? '').trim().length > 0 || statuses?.[f.key] !== 'NotSet');
  }

  async saveAndContinue(): Promise<void> {
    const step = this.currentStep();

    // Only fields the user actually typed into get saved - fields already
    // set from a previous visit are left untouched.
    const fieldsToSave = step.fields.filter((f) => (this.values[f.key] ?? '').trim().length > 0);

    if (fieldsToSave.length > 0) {
      this.saving.set(true);
      try {
        for (const field of fieldsToSave) {
          await firstValueFrom(this.api.saveKey(field.key, (this.values[field.key] ?? '').trim()));
        }
        const updated = await firstValueFrom(this.api.getKeyStatuses());
        this.keyStatuses.set(updated);
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
