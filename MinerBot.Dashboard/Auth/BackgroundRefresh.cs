namespace MinerBot.Dashboard.Auth
{
    // refresh.js marks each status box refresh with a header of its own, and the session store never counts a marked GET as
    // use, so a page left open ends its session 30 minutes after its last real use. Every other check applies to it as to
    // any request, and one past the idle limit ends the session like any other. The marker can only make a session end
    // sooner, so it's safe to take from the browser.
    internal static class BackgroundRefresh
    {
        // Hyphens only, since nginx drops a header with an underscore in its name
        public const string Header = "X-MinerBot-Refresh";
        public const string Value = "background";

        // On the fragment's answer, set on the response itself, so the exception handler's clearing takes it off any error
        // page: the script replaces a box only with an answer that has it, never with a sign-in page or an error
        public const string FragmentHeader = "X-MinerBot-Fragment";

        // Beside a request box's marker, also on the response itself: the seconds before the script looks again, from 2 to
        // 30, or end once what the box shows can't change, when the script stops without its note
        public const string NextHeader = "X-MinerBot-Refresh-Next";
        public const string NextEnd = "end";

        public static bool Marks(HttpRequest request) => HttpMethods.IsGet(request.Method) && request.Headers[Header] == Value;
    }
}
