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
  // Overrides the default "every required field is set" check - used for
  // Trading212, where either the demo pair or the live pair (not
  // necessarily both, not a specific one) satisfies the step.
  isComplete?: (statuses: KeyStatusesDto | null, pendingValues: Record<string, string>) => boolean;
}

function hasPair(
  statuses: KeyStatusesDto | null,
  pendingValues: Record<string, string>,
  keyField: ApiKeyProvider,
  secretField: ApiKeyProvider,
): boolean {
  const keySet = statuses?.[keyField] !== 'NotSet' || (pendingValues[keyField] ?? '').trim().length > 0;
  const secretSet = statuses?.[secretField] !== 'NotSet' || (pendingValues[secretField] ?? '').trim().length > 0;
  return keySet && secretSet;
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
      'You only need one pair to continue — add the other (or switch later) any time in Settings. You can only ' +
      'switch TradingMode to an environment once its pair is saved.',
    helpUrl: 'https://www.trading212.com/en/api-docs',
    fields: [
      { key: 'Trading212DemoKey', label: 'Demo API key', placeholder: 'Paste your Trading 212 demo API key', required: false },
      { key: 'Trading212DemoSecret', label: 'Demo API secret', placeholder: 'Paste your Trading 212 demo API secret', required: false },
      { key: 'Trading212LiveKey', label: 'Live API key', placeholder: 'Paste your Trading 212 live API key', required: false },
      { key: 'Trading212LiveSecret', label: 'Live API secret', placeholder: 'Paste your Trading 212 live API secret', required: false },
    ],
    isComplete: (statuses, pendingValues) =>
      hasPair(statuses, pendingValues, 'Trading212DemoKey', 'Trading212DemoSecret') ||
      hasPair(statuses, pendingValues, 'Trading212LiveKey', 'Trading212LiveSecret'),
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

  // First step of the wizard, ahead of the API-key steps - the email in the
  // auth token can't be trusted for every identity provider (e.g. some
  // Google-federated CIAM sign-ins never return one), so this is asked for
  // directly instead of relying on it.
  emailConfirmed = signal(false);
  emailInput = '';
  confirmingEmail = signal(false);

  currentStep = () => this.steps[this.currentIndex()];
  isLastStep = () => this.currentIndex() === this.steps.length - 1;

  constructor() {
    this.api.getKeyStatuses().subscribe({ next: (s) => this.keyStatuses.set(s) });
    this.api.getMe().subscribe({ next: (me) => this.emailConfirmed.set(me.hasConfirmedEmail) });
    // Not pre-filled from auth.currentUser()?.email - that's frequently a
    // synthetic {objectId}@tenant fallback for identity providers that
    // don't return a real email claim, so it added no value and risked
    // being submitted unnoticed.
  }

  confirmEmail(): void {
    const email = this.emailInput.trim();
    if (!email || !email.includes('@')) return;

    this.confirmingEmail.set(true);
    this.api.updateMyEmail(email).subscribe({
      next: () => {
        this.confirmingEmail.set(false);
        this.emailConfirmed.set(true);
      },
      error: () => {
        this.confirmingEmail.set(false);
        this.snackbar.open('Failed to save your email — try again.', 'Dismiss', { duration: 4000 });
      },
    });
  }

  // "Complete" only considers required fields by default - a step with a
  // custom isComplete (Trading212's "either pair" rule) uses that instead.
  isStepComplete(step: WizardStep): boolean {
    if (step.isComplete) return step.isComplete(this.keyStatuses(), {});
    const statuses = this.keyStatuses();
    if (!statuses) return false;
    return step.fields.filter((f) => f.required).every((f) => statuses[f.key] !== 'NotSet');
  }

  canProceed(): boolean {
    const step = this.currentStep();
    if (step.isComplete) return step.isComplete(this.keyStatuses(), this.values);
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
