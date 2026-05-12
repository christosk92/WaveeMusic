using FluentAssertions;
using Wavee.Local.Subtitles;

namespace Wavee.Local.Tests.Subtitles;

public class LocalSubtitleDiscovererTests
{
    [Fact]
    public void Finds_sibling_srt_in_same_dir()
    {
        var fs = new InMemoryFileSystem();
        fs.AddDir(@"D:\v");
        fs.AddFile(@"D:\v\Movie.mkv");
        fs.AddFile(@"D:\v\Movie.srt");
        fs.AddFile(@"D:\v\Movie.en.srt");

        var subs = LocalSubtitleDiscoverer.Discover(@"D:\v\Movie.mkv", fs);
        subs.Should().HaveCount(2);
        subs.Should().Contain(s => s.Path.EndsWith("Movie.srt"));
        subs.Should().Contain(s => s.Language == "en");
    }

    [Fact]
    public void Finds_subs_in_sibling_Subs_folder_flat()
    {
        var fs = new InMemoryFileSystem();
        fs.AddDir(@"D:\v");
        fs.AddDir(@"D:\v\Subs");
        fs.AddFile(@"D:\v\Movie.mkv");
        fs.AddFile(@"D:\v\Subs\Movie.eng.srt");

        var subs = LocalSubtitleDiscoverer.Discover(@"D:\v\Movie.mkv", fs);
        subs.Should().HaveCount(1);
        subs[0].Language.Should().Be("en");
    }

    [Fact]
    public void Finds_subs_in_RARBG_per_episode_subdir_layout()
    {
        var fs = new InMemoryFileSystem();
        // Layout: Person.of.Interest.S01.1080p.BluRay.x265-RARBG\Subs\Person.of.Interest.S01E01\English.srt
        fs.AddDir(@"D:\v\Person.of.Interest.S01.1080p.BluRay.x265-RARBG");
        fs.AddDir(@"D:\v\Person.of.Interest.S01.1080p.BluRay.x265-RARBG\Subs");
        fs.AddDir(@"D:\v\Person.of.Interest.S01.1080p.BluRay.x265-RARBG\Subs\Person.of.Interest.S01E01");
        fs.AddFile(@"D:\v\Person.of.Interest.S01.1080p.BluRay.x265-RARBG\Person.of.Interest.S01E01.1080p.BluRay.x265-RARBG.mkv");
        fs.AddFile(@"D:\v\Person.of.Interest.S01.1080p.BluRay.x265-RARBG\Subs\Person.of.Interest.S01E01\English.srt");

        var subs = LocalSubtitleDiscoverer.Discover(
            @"D:\v\Person.of.Interest.S01.1080p.BluRay.x265-RARBG\Person.of.Interest.S01E01.1080p.BluRay.x265-RARBG.mkv",
            fs);
        subs.Should().NotBeEmpty();
        subs.Should().Contain(s => s.Language == "en");
    }

    [Fact]
    public void Detects_forced_flag()
    {
        var fs = new InMemoryFileSystem();
        fs.AddDir(@"D:\v");
        fs.AddFile(@"D:\v\Movie.mkv");
        fs.AddFile(@"D:\v\Movie.en.forced.srt");

        var subs = LocalSubtitleDiscoverer.Discover(@"D:\v\Movie.mkv", fs);
        subs.Should().HaveCount(1);
        subs[0].Forced.Should().BeTrue();
    }

    /// <summary>
    /// Minimal in-memory <see cref="IFileSystem"/> for subtitle-discoverer
    /// tests. Stores directory structure and bare file existence — no contents.
    /// </summary>
    private sealed class InMemoryFileSystem : IFileSystem
    {
        private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

        public void AddDir(string path) => _dirs.Add(path);
        public void AddFile(string path) => _files.Add(path);

        public bool DirectoryExists(string path) => _dirs.Contains(path);
        public bool FileExists(string path) => _files.Contains(path);

        public IEnumerable<string> EnumerateFiles(string dir) =>
            _files.Where(f => string.Equals(Path.GetDirectoryName(f), dir, StringComparison.OrdinalIgnoreCase))
                  .Select(Path.GetFileName)!;

        public IEnumerable<string> EnumerateDirectories(string dir) =>
            _dirs.Where(d => string.Equals(Path.GetDirectoryName(d), dir, StringComparison.OrdinalIgnoreCase))
                 .Select(Path.GetFileName)!;
    }
}
