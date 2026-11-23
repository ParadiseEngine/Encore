namespace Encore.Tests;

/// <summary>Where the tests find the repository's files (the scores under assets/audio/scores).</summary>
internal static class TestScenes
{
    public static string RepositoryFile(string relative)
    {
        for (var directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
            if (File.Exists(Path.Combine(directory, "Encore.Audio.slnx"))) return Path.Combine(directory, relative);
        throw new FileNotFoundException("The repository root (Encore.Audio.slnx) is not above the test binaries.");
    }
}
