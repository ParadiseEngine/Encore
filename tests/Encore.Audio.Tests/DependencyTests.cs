using Encore.Audio;

namespace Encore.Tests;

/// <summary>The core library stays standalone: it references .NET itself and nothing else.</summary>
public sealed class DependencyTests
{
    [Test]
    public async Task TheCoreLibraryReferencesOnlyTheDotNetRuntime()
    {
        var references = typeof(AudioMixer).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        var foreign = references.Where(name => !(name is "netstandard" or "mscorlib" || name.StartsWith("System", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.", StringComparison.Ordinal))).ToList();
        await Assert.That(string.Join(", ", foreign)).IsEqualTo("");
        await Assert.That(references.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task TheCoreLibraryShipsNoDeviceOutput()
    {
        var types = typeof(AudioMixer).Assembly.GetTypes().Select(t => t.FullName ?? "").ToList();
        await Assert.That(types.Any(t => t.Contains("Sdl", StringComparison.OrdinalIgnoreCase))).IsFalse();
    }
}
