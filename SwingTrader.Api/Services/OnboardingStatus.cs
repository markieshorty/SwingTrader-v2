using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Api.Services;

// "Onboarding complete" is computed from key statuses (mirrors
// onboarding.guard.ts's isOnboardingComplete) rather than trusted from the
// AppUser.IsOnboarded DB column alone. Claude isn't required here - it has a
// shared fallback key (see UserKeyService.GetKeyAsync), so accounts never
// need their own. Shared between the /keys save endpoint (which flips
// IsOnboarded and kicks off the first Watchlist run) and the admin
// stats/users views.
public static class OnboardingStatus
{
    public static bool IsReallyOnboarded(Dictionary<string, KeyStatus> statuses)
    {
        bool HasPair(string keyProvider, string secretProvider) =>
            statuses.GetValueOrDefault(keyProvider) != KeyStatus.NotSet && statuses.GetValueOrDefault(secretProvider) != KeyStatus.NotSet;

        var hasCoreKeys = statuses.GetValueOrDefault(ApiKeyProviders.Finnhub) != KeyStatus.NotSet
            && statuses.GetValueOrDefault(ApiKeyProviders.Tiingo) != KeyStatus.NotSet;
        var hasTrading212Pair = HasPair(ApiKeyProviders.Trading212DemoKey, ApiKeyProviders.Trading212DemoSecret)
            || HasPair(ApiKeyProviders.Trading212LiveKey, ApiKeyProviders.Trading212LiveSecret);

        return hasCoreKeys && hasTrading212Pair;
    }
}
