namespace Wavee;

public static partial class Spotify
{
    public static partial class Api
    {
        readonly record struct RequestAccount(string Account, Scope? Scope, uint ScopeEpoch, uint SessionEpoch);
        [ThreadStatic] static RequestAccount? s_requestAccount;

        /// <summary>A synchronous API-worker operation may only use the account and session it captured.</summary>
        public readonly struct AccountRequest : IDisposable
        {
            readonly RequestAccount? _previous;

            internal AccountRequest(string account)
            {
                _previous = s_requestAccount;
                Scope? scope = Entities.Current;
                s_requestAccount = new RequestAccount(account, scope, scope?.Epoch ?? 0, Current.Epoch);
            }

            public void Dispose() => s_requestAccount = _previous;
        }

        public static AccountRequest ForAccount(string account) => new(account);

        public static bool AccountRequestAllowed(string expectedAccount, string currentAccount,
            uint expectedSessionEpoch, uint currentSessionEpoch, bool sameScope)
            => sameScope && expectedSessionEpoch == currentSessionEpoch
                && string.Equals(expectedAccount, currentAccount, StringComparison.Ordinal);

        static bool AccountRequestIsCurrent()
        {
            if (s_requestAccount is not { } request) return true;
            Scope? scope = Entities.Current;
            return AccountRequestAllowed(request.Account, scope?.Key.Account ?? "",
                request.SessionEpoch, Current.Epoch,
                ReferenceEquals(request.Scope, scope) && request.ScopeEpoch == (scope?.Epoch ?? 0));
        }
    }
}
