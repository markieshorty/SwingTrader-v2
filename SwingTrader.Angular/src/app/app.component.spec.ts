import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MsalService } from '@azure/msal-angular';
import { PublicClientApplication } from '@azure/msal-browser';
import { AppComponent } from './app.component';

@Component({ standalone: true, template: '' })
class StubComponent {}

describe('AppComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AppComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'dashboard', component: StubComponent }]),
        provideNoopAnimations(),
        { provide: MsalService, useValue: { instance: new PublicClientApplication({ auth: { clientId: 'test' } }) } },
      ],
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(AppComponent);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });

  it('should render the sidenav with the SwingTrader logo on an in-app route', async () => {
    // The default '/' route is the public splash page (no chrome) - the
    // sidenav only renders on actual in-app routes, so navigate there first.
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/dashboard');

    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.logo')?.textContent).toContain('SwingTrader');
  });

  it('should not render the sidenav on the splash route', () => {
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.logo')).toBeNull();
  });
});
