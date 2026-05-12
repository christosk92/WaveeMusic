using FluentAssertions;

namespace Wavee.Local.Tests;

public class LocalUriTests
{
    [Fact]
    public void IsLocal_includes_library_context()
    {
        LocalUri.IsLocal(LocalUri.LibraryContextUri).Should().BeTrue();
        LocalUri.IsLibrary(LocalUri.LibraryContextUri).Should().BeTrue();
    }

    [Fact]
    public void TryParse_rejects_library_context_as_hashed_entity()
    {
        LocalUri.TryParse(LocalUri.LibraryContextUri, out var kind, out var hash).Should().BeFalse();
        kind.Should().Be(LocalUriKind.None);
        hash.Should().BeEmpty();
    }
}
