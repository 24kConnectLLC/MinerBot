using MinerBot.Shared.Armor;

namespace MinerBot.Shared.Tests
{
    // Both apps reference the shared library, so anything it pulled in would reach the other app too
    public class SharedReferencesTests
    {
        [Fact]
        public void ReferencesNothingButDotNet()
        {
            var references = typeof(ArmorStatus).Assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? "").ToList();

            Assert.NotEmpty(references);
            Assert.All(references, name =>
                Assert.True(name == "System" || name.StartsWith("System.", StringComparison.Ordinal), $"MinerBot.Shared references {name}"));
        }
    }
}
