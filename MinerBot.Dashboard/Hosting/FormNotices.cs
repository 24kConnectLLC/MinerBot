using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Requests;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Hosting
{
    // What a page's form was answered with when it wasn't sent on: the page's GET draws it at ?notice=, with today's words
    // and status. Words are fixed text, never what was typed; what was typed into a text box is kept in Kept, by the box's
    // field name, and a field's message as "problem." and the field. A typed word or a typed server name is never kept.
    internal sealed record FormNotice
    {
        // 32 hex digits in small letters, the ?notice= that names it, and the path of its page's address
        public string Id { get; init; } = "";
        public string Page { get; init; } = "";

        // When the form was answered, in UTC
        public DateTime Made { get; init; }

        // The handler or form it answers, one the page names when it looks for its notice
        public string Form { get; init; } = "";

        public int Status { get; init; }
        public string? Words { get; init; }

        // The request the words name, the form's key kept after a save the database didn't confirm, whether that's why, and
        // when a person's limit lets them ask again, for Retry-After
        public ulong? Request { get; init; }
        public string? Key { get; init; }
        public bool Unsure { get; init; }
        public DateTime? After { get; init; }

        // A word of the page's own for what it draws, and the blacklist add's account and lookup
        public string? State { get; init; }
        public uint? Account { get; init; }
        public ulong? Lookup { get; init; }

        public Dictionary<string, string>? Kept { get; init; }

        // The sender's answer, as every page that sends a request draws it when it wasn't sent: its status, its words and the
        // request they name, and the form's key after a save the database didn't confirm. Never for a request that was sent,
        // or one refused as Forbidden or MadeUp, whose answers are a redirect, Forbid() and a 400.
        public static FormNotice Of(string form, SendOutcome outcome)
        {
            var (words, request) = outcome switch
            {
                SendOutcome.SentBefore before => (RequestWords.SentBefore(before.Id), (ulong?)before.Id),
                SendOutcome.Refused refused => (refused.Words, refused.Waiting),
                SendOutcome.Unsure => (RequestWords.Unsure, null),
                _ => throw new InvalidOperationException("A request that was sent has no notice")
            };

            return new FormNotice
            {
                Form = form,
                Status = RequestWords.Status(outcome),
                Words = words,
                Request = request,
                Key = (outcome as SendOutcome.Unsure)?.Key,
                Unsure = outcome is SendOutcome.Unsure,
                After = (outcome as SendOutcome.Refused)?.After
            };
        }
    }

    // A form's answer is always a redirect, never a page drawn on the POST, which a reload, Back and Resubmit, or a restored
    // tab would send again, and send for real once the refusal's cause had gone. A form that wasn't sent on is kept here as
    // a notice, in a cookie of its own, protected with Data Protection under the session's key, as DbTicketStore protects
    // its tickets, so it opens only for the session that made it, and still opens after a restart. The answer is a 302 to
    // the page's own address with ?notice= and the notice's ID, whose GET draws it: a reload draws it again and sends
    // nothing. A notice lasts 30 minutes, as a session left unused does, and shows only at its own address. There is one per
    // browser, so a newer one replaces it. Its cookie is kept to one, well within nginx's 8 KB for a request's Cookie
    // header, beside the dashboard's own cookies and Cloudflare Access's token: past that, nginx would refuse every request.
    internal sealed class FormNotices(IDataProtectionProvider protection, TimeProvider time)
    {
        // A later change to the notice's form bumps it, so an older cookie fails to open and is deleted
        public const string Purpose = "MinerBot.Dashboard.FormNotice.v1";

        public static readonly TimeSpan Lifetime = DbTicketStore.IdleLimit;

        // The JSON's bytes at most. Protected and in base64url, about 2,780 characters, so the cookie's Set-Cookie stays
        // under 3,000 bytes.
        public const int Budget = 2000;

        // The address's parameter, the layout's ViewData entry for the line that says a notice has gone, and the start of a
        // kept field's message
        public const string Parameter = "notice";
        public const string GoneKey = "NoticeGone";
        public const string ProblemPrefix = "problem.";

        // Most text in the Basic Multilingual Plane is written as its UTF-8, 2 or 3 bytes a character, rather than as a 6-byte
        // escape, so more of what was typed fits. The encoder still escapes ' " < > & + and `, a no-break space and a few
        // others, at 6 bytes each, and writes a character past the plane as two escapes, so a box of those costs the most:
        // the budget measures the JSON itself, so it still holds. The JSON is only ever encrypted, never drawn or run.
        static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

        readonly IDataProtector notices = protection.CreateProtector(Purpose);

        // Keeps the notice for this browser, in place of any it had, and answers the form with a 302 to the page's own
        // address, with the route values given and ?notice=. Url.Page leaves out the POST's handler, as RedirectToPage does.
        // Over the budget, the box kept that takes the most room in the JSON goes first, until it fits: a field's message is
        // never dropped, and the fixed parts and the messages together are far smaller than the budget.
        public IActionResult Keep(PageModel page, FormNotice notice, object? routeValues = null)
        {
            ArgumentNullException.ThrowIfNull(page);
            ArgumentNullException.ThrowIfNull(notice);

            // Every form here is behind the Admin policy, so there's always a session; the error page answers a form that
            // throws with a redirect too
            var key = SessionKeys.Current(page.HttpContext) ?? throw new InvalidOperationException("A form's notice is kept only for a signed-in session");
            var id = RequestFields.NewKey();
            var address = page.Url.Page(null, null, new RouteValueDictionary(routeValues) { [Parameter] = id })
                ?? throw new InvalidOperationException("A form's page has no address to come back to");

            var stored = notice with
            {
                Id = id,
                Page = address.Split('?')[0],
                Made = time.GetUtcNow().UtcDateTime,
                Kept = notice.Kept == null ? null : new Dictionary<string, string>(notice.Kept, StringComparer.Ordinal)
            };

            var json = JsonSerializer.SerializeToUtf8Bytes(stored, Json);

            while (json.Length > Budget && Longest(stored.Kept) is { } longest)
            {
                stored.Kept!.Remove(longest);
                json = JsonSerializer.SerializeToUtf8Bytes(stored, Json);
            }

            page.Response.Cookies.Append(DashboardAuth.NoticeCookie, Base64Url.EncodeToString(notices.CreateProtector(key).Protect(json)), Options(Lifetime));
            return new RedirectResult(address);
        }

        // What was typed into a box, kept only within the box's maxlength in UTF-16 units, which is all a browser sends.
        // Longer text comes only from a form someone made up, so it isn't kept, and its problem still shows.
        public static string? Text(string? value, int limit) => value is { Length: var length } && length <= limit ? value : null;

        // The notice ?notice= names, for one of the page's forms, or null. Only chooses what to show, so an ID that isn't one
        // touches nothing. A notice that can't be opened, from another session, another format or a broken cookie, or that's
        // past its 30 minutes or dated ahead of the clock, fails closed: it's deleted, and the page says a notice has gone.
        // One with another ID, at another address or for another form is left alone, since it may be another tab's. The GET
        // never writes the cookie, so a reload finds the notice again for its 30 minutes.
        public FormNotice? Find(PageModel page, string? id, IEnumerable<string> forms)
        {
            ArgumentNullException.ThrowIfNull(page);
            ArgumentNullException.ThrowIfNull(forms);

            if (!IsWellFormed(id))
                return null;

            var context = page.HttpContext;

            if (context.Request.Cookies[DashboardAuth.NoticeCookie] is not { Length: > 0 } cookie || SessionKeys.Current(context) is not { } key)
                return Gone(page);

            if (Open(cookie, key) is not { } notice || !IsCurrent(notice))
            {
                Forget(context);
                return Gone(page);
            }

            if (notice.Id != id || !IsAt(notice, context.Request) || !forms.Contains(notice.Form))
                return Gone(page);

            return notice;
        }

        // Deletes this browser's notice, as a sign-out does
        public static void Forget(HttpContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            context.Response.Cookies.Delete(DashboardAuth.NoticeCookie, Options(null));
        }

        // For the error page, whose GET can't read the session while MinerBot's database is down: whether ?notice= names this
        // browser's notice of a save the database didn't confirm, at this address and within its 30 minutes, opened with
        // the session cookie as the browser sent it, since the ticket store failed before it found the session. Never
        // throws, and never writes the cookie.
        public bool IsUnsure(HttpContext context)
        {
            try
            {
                var request = context.Request;
                var id = request.Query[Parameter].ToString();

                if (!(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) || !IsWellFormed(id)
                    || request.Cookies[DashboardAuth.SessionCookie] is not { } key || !SessionKeys.IsWellFormed(key)
                    || request.Cookies[DashboardAuth.NoticeCookie] is not { Length: > 0 } cookie || Open(cookie, key) is not { } notice)
                    return false;

                return notice.Unsure && notice.Id == id && IsAt(notice, request) && IsCurrent(notice);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Fails closed, as FreshSignIn does: dated ahead of the clock, or made 30 minutes ago or more
        bool IsCurrent(FormNotice notice)
        {
            var now = time.GetUtcNow().UtcDateTime;
            return notice.Made <= now && now - notice.Made < Lifetime;
        }

        FormNotice? Open(string cookie, string key)
        {
            try
            {
                var notice = JsonSerializer.Deserialize<FormNotice>(notices.CreateProtector(key).Unprotect(Base64Url.DecodeFromChars(cookie)), Json);
                return notice is { Page.Length: > 0, Form.Length: > 0 } && IsWellFormed(notice.Id) ? notice : null;
            }
            catch (Exception e) when (e is CryptographicException or FormatException or JsonException)
            {
                return null;
            }
        }

        static FormNotice? Gone(PageModel page)
        {
            page.ViewData[GoneKey] = true;
            return null;
        }

        static bool IsAt(FormNotice notice, HttpRequest request) =>
            string.Equals(notice.Page, (request.PathBase + request.Path).Value, StringComparison.OrdinalIgnoreCase);

        static bool IsWellFormed(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigitLower);

        // The box kept that takes the most room in the JSON, never a field's message. Its UTF-8 isn't the measure: a box of
        // 100 '<' is 100 bytes of UTF-8 but 600 in the JSON, and going by UTF-8 could drop two boxes where one would do.
        static string? Longest(Dictionary<string, string>? kept) => kept?
            .Where(entry => !entry.Key.StartsWith(ProblemPrefix, StringComparison.Ordinal))
            .OrderByDescending(entry => JsonSerializer.SerializeToUtf8Bytes(entry.Value, Json).Length)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => entry.Key)
            .FirstOrDefault();

        // A __Host- cookie, as the session's is, so murderminershub.com can neither set nor replace it: Secure, Path=/ and
        // no Domain. Lax, so it comes with the redirect's GET and with a history navigation, as the session cookie does.
        static CookieOptions Options(TimeSpan? maxAge) => new()
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
            MaxAge = maxAge
        };
    }

    internal static class FormNoticeServices
    {
        public static IServiceCollection AddFormNotices(this IServiceCollection services)
        {
            services.AddSingleton<FormNotices>();
            return services;
        }
    }
}
