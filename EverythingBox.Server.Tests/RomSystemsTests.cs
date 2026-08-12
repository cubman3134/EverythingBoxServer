using EverythingBox.Server.RomLibrary;
using Xunit;

namespace EverythingBox.Server.Tests;

public class RomSystemsTests
{
    [Theory]
    [InlineData("snes", "snes")]
    [InlineData("Super Famicom", "snes")]
    [InlineData("megadrive", "genesis")]
    [InlineData("Mega Drive", "genesis")]
    [InlineData("psx", "psx")]
    [InlineData("PlayStation", "psx")]
    [InlineData("tg16", "pce")]
    public void Resolves_known_folders_to_their_system_id(string folder, string expectedId)
        => Assert.Equal(expectedId, RomSystems.Resolve(folder)!.Value.Id);

    [Fact]
    public void Known_folder_has_a_nonempty_console_title()
        => Assert.False(string.IsNullOrWhiteSpace(RomSystems.Resolve("snes")!.Value.Title));

    [Fact]
    public void Unknown_folder_resolves_to_null()
        => Assert.Null(RomSystems.Resolve("totallynotaconsole"));
}
