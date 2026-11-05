namespace MinerBot.Dashboard.Hosting
{
    internal static class StaticFiles
    {
        // A week, which is safe because every link to them carries asp-append-version's ?v= hash. SecurityHeaders keeps a
        // Cache-Control that's already there, so these are the only responses that may be stored.
        public static StaticFileOptions Options => new()
        {
            OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = "public, max-age=604800"
        };
    }
}
