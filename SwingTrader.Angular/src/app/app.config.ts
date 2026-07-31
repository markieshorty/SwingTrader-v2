import { APP_INITIALIZER, ApplicationConfig, ErrorHandler, importProvidersFrom } from '@angular/core';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { HTTP_INTERCEPTORS, provideHttpClient, withInterceptors, withInterceptorsFromDi } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { provideCharts, withDefaultRegisterables } from 'ng2-charts';
import { MsalModule, MsalService, MsalGuard, MsalInterceptor, MsalBroadcastService } from '@azure/msal-angular';
import { PublicClientApplication, InteractionType, BrowserCacheLocation, LogLevel } from '@azure/msal-browser';

import { routes } from './app.routes';
import { errorInterceptor } from './core/interceptors/error.interceptor';
import { inviteTokenInterceptor } from './core/interceptors/invite-token.interceptor';
import { GlobalErrorHandler } from './core/global-error-handler';
import { environment } from '../environments/environment';

// Empty clientId/authority (before Phase 10c's manual B2C setup is
// complete) means MSAL initializes but every loginRedirect() attempt
// fails cleanly rather than the app crashing outright.
const msalConfig = {
  auth: {
    clientId: environment.b2cClientId,
    authority: environment.b2cAuthority,
    // CIAM tokens are issued from a GUID-based ciamlogin.com host even when
    // authority is configured with the subdomain-based host - both must be
    // listed or MSAL rejects the issuer.
    knownAuthorities: [environment.b2cDomain, environment.b2cTenantId + '.ciamlogin.com'],
    redirectUri: typeof window !== 'undefined' ? window.location.origin : '',
    // MSAL does NOT fall back to redirectUri for this - without it set
    // explicitly, logoutRedirect() lands on a generic Microsoft/CIAM
    // "you've signed out" page instead of coming back to the app's splash
    // screen.
    postLogoutRedirectUri: typeof window !== 'undefined' ? window.location.origin : '',
  },
  cache: {
    // LocalStorage (was SessionStorage, 24 Jul 2026): per-tab SessionStorage
    // wiped tokens on every browser close, forcing a full interactive login
    // each session - maximising exposure to Microsoft's stale-login-page
    // AADSTS165000 failures - and caused state_not_found when a redirect
    // landed in a tab that didn't own the login state. LocalStorage shares
    // tokens across tabs and survives restarts; MSAL renews silently.
    cacheLocation: BrowserCacheLocation.LocalStorage,
  },
  system: {
    // Surface MSAL's internal warnings/errors in the console - login
    // failures were hanging the boot spinner with a completely silent
    // console, leaving nothing to diagnose from (30 Jul 2026).
    loggerOptions: {
      loggerCallback: (_level: LogLevel, message: string) => console.warn('[MSAL]', message),
      logLevel: LogLevel.Warning,
      piiLoggingEnabled: false,
    },
  },
};

const msalInstance = new PublicClientApplication(msalConfig);

// msal-browser 3.x requires an explicit initialize() + handleRedirectPromise()
// before any other MSAL API call (loginRedirect, getAllAccounts, etc.) is
// safe to use - MsalModule.forRoot() alone does not guarantee this
// completes before route guards run under standalone bootstrapApplication.
// Without this, authGuard's loginRedirect() call throws
// "uninitialized_public_client_application" on first load.
// LocalStorage regression guard (24 Jul 2026): with the token cache in
// LocalStorage, an interrupted login's "interaction in progress" marker no
// longer dies with the tab - it persists FOREVER and bricks every future
// loginRedirect on that browser profile (users could only log in from
// incognito). A fresh page load that is NOT processing an auth redirect can
// never legitimately have an interaction in flight, so sweep the stale
// interaction/temp markers before MSAL initialises.
function sweepStaleInteractionMarkers(): void {
  // The auth response can arrive in the FRAGMENT or the QUERY string
  // depending on the app registration's platform/response mode - and an
  // error response carries no code at all. Wiping the in-flight request
  // state (PKCE verifier, nonce) in any of those cases kills the token
  // exchange and strands the user on the boot spinner.
  const responseCarrier = window.location.hash + window.location.search;
  const processingRedirect = ['code=', 'state=', 'error='].some((m) => responseCarrier.includes(m));
  if (processingRedirect) return;
  for (const store of [sessionStorage, localStorage]) {
    Object.keys(store)
      .filter((k) => k.startsWith('msal.') && (k.includes('interaction') || k.includes('request.params') || k.includes('request.origin')))
      .forEach((k) => store.removeItem(k));
  }
}

// Last-resort unbrick: everything msal.* goes, tokens included - the next
// load is a genuinely clean slate (what incognito was giving users manually).
function nukeMsalStorage(): void {
  for (const store of [sessionStorage, localStorage]) {
    Object.keys(store)
      .filter((k) => k.startsWith('msal.'))
      .forEach((k) => store.removeItem(k));
  }
}

function initializeMsal(): () => Promise<void> {
  return () => {
    sweepStaleInteractionMarkers();

    // Boot watchdog (30 Jul 2026): a hung initialize()/handleRedirectPromise()
    // - not rejected, HUNG - left users on the spinner forever with nothing
    // in the console. If MSAL hasn't finished in 15s, wipe its storage and
    // reload the bare origin: worst case the user re-authenticates once.
    let settled = false;
    const watchdog = new Promise<void>((resolve) => {
      setTimeout(() => {
        if (!settled) {
          console.error('[MSAL] initialization did not complete within 15s — clearing MSAL storage and reloading');
          nukeMsalStorage();
          window.location.replace(window.location.origin);
        }
        resolve();
      }, 15000);
    });

    const boot = msalInstance.initialize()
      .then(() => msalInstance.handleRedirectPromise())
      .then(async (result) => {
        // Boot-time session validation (31 Jul 2026): a cached account whose
        // SERVER session has died (cookies cleared/expired -> AADSTS160021)
        // let the app boot "logged in", then every API call's silent token
        // renewal failed and ten concurrent MsalInterceptor recovery
        // interactions fought over one interaction lock - nothing navigated
        // and the user sat on the spinner. Prove the cached account can
        // still mint a token BEFORE trusting it; if it can't, wipe the MSAL
        // cache so the app boots to the splash for one clean login.
        const account = result?.account ?? msalInstance.getAllAccounts()[0];
        if (!account) return;
        msalInstance.setActiveAccount(account);
        try {
          await msalInstance.acquireTokenSilent({ account, scopes: [environment.b2cScope] });
        } catch (err) {
          console.warn('[MSAL] cached account cannot renew silently — clearing MSAL cache for a clean login', err);
          nukeMsalStorage();
        }
      })
      .then(() => undefined)
      // A rejected handleRedirectPromise (transient token-exchange failure,
      // expired auth code, state_not_found when the redirect lands in a tab
      // whose SessionStorage lacks the login state, stale interaction marker
      // from an interrupted attempt) used to reject the APP_INITIALIZER -
      // Angular then refused to bootstrap and the user was stuck on a dead
      // spinner, recoverable only by closing the tab (SessionStorage dies
      // with it). Recover instead: clear the stale interaction markers so
      // the next loginRedirect isn't blocked by interaction_in_progress,
      // and boot to the splash for a one-click retry.
      .catch((err) => {
        console.error('MSAL redirect handling failed — booting to splash for a clean retry', err);
        for (const store of [sessionStorage, localStorage]) {
          Object.keys(store)
            .filter((k) => k.startsWith('msal.') && k.includes('interaction'))
            .forEach((k) => store.removeItem(k));
        }
      })
      .finally(() => { settled = true; });

    return Promise.race([boot, watchdog]);
  };
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(routes, withComponentInputBinding()),
    provideAnimationsAsync(),
    // MsalInterceptor (registered below via HTTP_INTERCEPTORS) is what
    // actually attaches "Authorization: Bearer {token}" to requests
    // matching protectedResourceMap - withInterceptorsFromDi() bridges the
    // classic DI-based interceptor into the functional pipeline alongside
    // errorInterceptor. core/interceptors/auth.interceptor.ts is therefore
    // unused now; kept only as the placeholder file the Phase 10b spec
    // originally described.
    provideHttpClient(withInterceptorsFromDi(), withInterceptors([inviteTokenInterceptor, errorInterceptor])),
    provideCharts(withDefaultRegisterables()),
    { provide: ErrorHandler, useClass: GlobalErrorHandler },
    { provide: HTTP_INTERCEPTORS, useClass: MsalInterceptor, multi: true },
    { provide: APP_INITIALIZER, useFactory: initializeMsal, multi: true },
    importProvidersFrom(
      MsalModule.forRoot(
        msalInstance,
        {
          interactionType: InteractionType.Redirect,
          authRequest: {
            scopes: ['openid', 'profile', 'email', environment.b2cScope],
          },
        },
        {
          interactionType: InteractionType.Redirect,
          // /run/* (manual "run now" triggers) is also RequireAuthorization()
          // on the backend but was missing here, so MsalInterceptor never
          // attached a Bearer token to those requests - every click 401'd.
          protectedResourceMap: new Map([
            [`${environment.apiUrl}/api/*`, [environment.b2cScope]],
            [`${environment.apiUrl}/run/*`, [environment.b2cScope]],
          ]),
        },
      ),
    ),
    MsalService,
    MsalGuard,
    MsalBroadcastService,
  ],
};
