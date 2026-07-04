import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../../core/services/api.service';
import { WeightEditorComponent } from './weight-editor/weight-editor.component';
import { SuggestionCardComponent } from './suggestion-card/suggestion-card.component';
import { LoadingSpinnerComponent } from '../../shared/components/loading-spinner/loading-spinner.component';
import { RefinementStatusDto } from '../../core/models/dtos';

@Component({
  selector: 'app-refinement',
  standalone: true,
  imports: [CommonModule, MatCardModule, WeightEditorComponent, SuggestionCardComponent, LoadingSpinnerComponent],
  templateUrl: './refinement.component.html',
  styleUrl: './refinement.component.scss',
})
export class RefinementComponent {
  private api = inject(ApiService);
  private snackbar = inject(MatSnackBar);

  status = signal<RefinementStatusDto | null>(null);
  loaded = signal(false);

  constructor() {
    this.load();
  }

  private load(): void {
    this.api.getRefinementStatus().subscribe({
      next: (status) => {
        this.status.set(status);
        this.loaded.set(true);
      },
      error: () => this.loaded.set(true),
    });
  }

  apply(suggestionId: number): void {
    this.api.applyRefinement(suggestionId).subscribe({
      next: (result) => {
        this.snackbar.open(result.message ?? 'Applied', 'Dismiss', { duration: 4000 });
        this.load();
      },
      error: () => this.snackbar.open('Failed to apply suggestion', 'Dismiss', { duration: 4000 }),
    });
  }

  reject(suggestionId: number, note: string): void {
    this.api.rejectRefinement(suggestionId, note).subscribe({
      next: () => {
        this.snackbar.open('Suggestion rejected', 'Dismiss', { duration: 4000 });
        this.load();
      },
      error: () => this.snackbar.open('Failed to reject suggestion', 'Dismiss', { duration: 4000 }),
    });
  }
}
