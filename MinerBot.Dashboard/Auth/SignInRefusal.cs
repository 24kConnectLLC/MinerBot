namespace MinerBot.Dashboard.Auth
{
    // Why a sign-in ended without a session. The denied page explains each in plain words, and only the word travels in
    // its address, so whatever else that address holds is never shown.
    internal enum SignInRefusal
    {
        None,
        Unlisted,
        NoTwoFactor,
        WrongScope,
        Cancelled,
        Failed
    }

    internal static class SignInRefusals
    {
        public static string Word(this SignInRefusal refusal) => refusal switch
        {
            SignInRefusal.Unlisted => "unlisted",
            SignInRefusal.NoTwoFactor => "mfa",
            SignInRefusal.WrongScope => "scope",
            SignInRefusal.Cancelled => "cancelled",
            SignInRefusal.Failed => "failed",
            _ => ""
        };

        public static SignInRefusal FromWord(string? word) => word switch
        {
            "unlisted" => SignInRefusal.Unlisted,
            "mfa" => SignInRefusal.NoTwoFactor,
            "scope" => SignInRefusal.WrongScope,
            "cancelled" => SignInRefusal.Cancelled,
            "failed" => SignInRefusal.Failed,
            _ => SignInRefusal.None
        };

        public static string DeniedPath(this SignInRefusal refusal) => "/denied?reason=" + refusal.Word();

        // What the audit row says, for the refusals that reach Discord's answer
        public static string Detail(this SignInRefusal refusal) => refusal switch
        {
            SignInRefusal.Unlisted => "Not on the dashboard's lists",
            SignInRefusal.NoTwoFactor => "Discord two-factor is off",
            SignInRefusal.WrongScope => "Discord granted other permissions than identify",
            _ => refusal.ToString()
        };
    }
}
