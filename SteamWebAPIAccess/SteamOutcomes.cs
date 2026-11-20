namespace SteamWebAPIAccess
{
    // What a renewal of the Steam token did, for the daily job's detail and for the dashboard's request
    public enum TokenRenewalOutcome
    {
        // There's no token, or the bot isn't signed in, so Steam wasn't asked
        NotSignedIn,

        // The token file couldn't be saved first, so Steam wasn't asked: a token it replaced would have been lost
        NotSaved,

        // Steam issued a new token, which was saved
        Renewed,

        // Steam kept the token it had, or a newer sign-in replaced it meanwhile
        Kept,

        // Steam didn't renew it
        Failed
    }

    // Expires is the date the token in use ends, when it's known. FailureType is the exception's type, never its message.
    public sealed record TokenRenewal(TokenRenewalOutcome Outcome, DateOnly? Expires = null, string? FailureType = null);

    // What a reconnect the dashboard asked for did
    public enum SteamReconnect
    {
        // The connection was dropped, and the pump logs on again after 2 seconds
        Started,

        // Steam isn't logged on, so the pump is already trying
        NotLoggedOn,

        // A sign-in is under way, which a new connection would start over
        SigningIn
    }
}
