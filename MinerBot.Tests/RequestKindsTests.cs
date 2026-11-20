using System.Reflection;
using MinerBot.Shared.Requests;
using MinerBot_2._0.Attributes;
using MinerBot_2._0.Handlers;

namespace MinerBot.Tests
{
    // The request list lives in MinerBot.Shared, which can't see the bot, so the bot's side of it is checked here
    public class RequestKindsTests
    {
        // A kind that runs a job names it as the bot names its jobs, <class>.<method>, so it must be one of the bot's [Job]
        // methods, found the way JobHandler finds them
        [Fact]
        public void EveryRequestedJobExists()
        {
            var jobs = typeof(JobHandler).Assembly.GetTypes()
                .SelectMany(type => type.GetMethods())
                .Where(method => method.GetCustomAttribute<JobAttribute>() != null)
                .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
                .ToList();
            var requested = RequestKinds.All.Where(kind => kind.Job != null).ToList();

            Assert.Equal([RequestKinds.JobNotifications, RequestKinds.JobColors], requested.Select(kind => kind.Word));
            Assert.All(requested, kind => Assert.Contains(kind.Job!, jobs));
        }
    }
}
