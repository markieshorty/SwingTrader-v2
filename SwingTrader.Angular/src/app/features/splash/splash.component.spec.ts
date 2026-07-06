import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, ActivatedRoute, convertToParamMap } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { SplashComponent } from './splash.component';

@Component({ standalone: true, template: '' })
class StubComponent {}

describe('SplashComponent', () => {
  let getAllAccounts: jasmine.Spy;

  function setup(queryParams: Record<string, string>) {
    getAllAccounts = jasmine.createSpy('getAllAccounts');

    TestBed.configureTestingModule({
      imports: [SplashComponent],
      providers: [
        provideRouter([{ path: 'dashboard', component: StubComponent }]),
        { provide: MsalService, useValue: { instance: { getAllAccounts }, loginRedirect: jasmine.createSpy() } },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap(queryParams) } },
        },
      ],
    });
  }

  afterEach(() => sessionStorage.clear());

  it('shows Register/Log In when signed out and no invite token', () => {
    setup({});
    getAllAccounts.and.returnValue([]);

    const fixture = TestBed.createComponent(SplashComponent);
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.textContent).toContain('Register');
    expect(compiled.textContent).toContain('Log In');
  });

  it('shows a single Join Account button when signed out with an invite token', () => {
    setup({ invite: 'abc123' });
    getAllAccounts.and.returnValue([]);

    const fixture = TestBed.createComponent(SplashComponent);
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.textContent).toContain('Join Account');
    expect(compiled.textContent).not.toContain('Register');
  });

  it('clicking Join Account stashes the invite token and a join-return marker before redirecting', () => {
    setup({ invite: 'abc123' });
    getAllAccounts.and.returnValue([]);

    const fixture = TestBed.createComponent(SplashComponent);
    fixture.detectChanges();
    (fixture.nativeElement as HTMLElement).querySelector('button')?.dispatchEvent(new Event('click'));

    expect(sessionStorage.getItem('pendingInviteToken')).toBe('abc123');
    expect(sessionStorage.getItem('inviteJoinReturn')).toBe('1');
  });

  it('does NOT show "invite doesn\'t apply" when returning from a just-completed join redirect', async () => {
    // Simulates MSAL restoring the exact /join?invite=... URL after the
    // OAuth round-trip completes for a brand-new visitor who just clicked
    // Join Account - regression test for the bug where this looked
    // identical to an already-signed-in visitor hitting a fresh invite link.
    sessionStorage.setItem('inviteJoinReturn', '1');
    setup({ invite: 'abc123' });
    getAllAccounts.and.returnValue([{ username: 'new-user' }]);

    const fixture = TestBed.createComponent(SplashComponent);
    const router = TestBed.inject(Router);
    const navigateSpy = spyOn(router, 'navigateByUrl');

    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).not.toContain("doesn't apply");
    expect(navigateSpy).toHaveBeenCalledWith('/dashboard');
    expect(sessionStorage.getItem('inviteJoinReturn')).toBeNull();
  });

  it('shows "invite doesn\'t apply" when an already-signed-in visitor opens a fresh invite link', () => {
    // No inviteJoinReturn marker - this visitor never clicked Join Account
    // in this session, they already had an active MSAL session beforehand.
    setup({ invite: 'someone-elses-invite' });
    getAllAccounts.and.returnValue([{ username: 'existing-user' }]);

    const fixture = TestBed.createComponent(SplashComponent);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain("doesn't apply");
  });
});
