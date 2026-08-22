using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core;

namespace EverythingBox.Server.Core.Tests;

// Renamed from ServerApiTests (Task 2 review): EverythingBox.Server.Tests also has a
// ServerApiTests testing the same ServerApi type. Two identically-named classes across
// projects meant a contributor grepping for the name could edit the wrong one with no
// compile error to catch it.
public class ServerApiContractTests
{
    [Fact]
    public void Version_is_1_19_now_that_a_romhack_patch_can_be_a_finished_rom()
    {
        Assert.Equal(1, ServerApi.Current.Major);
        Assert.Equal(19, ServerApi.Current.Minor);
    }

    [Fact]
    public void A_plugin_built_against_1_0_still_loads()
    {
        // Adding members to interfaces plugins only CALL is not a breaking change.
        Assert.True(ServerApi.IsCompatible(new Version(1, 0)));
    }

    [Fact]
    public void TorrentGrabber_satisfies_the_grabber_interface()
    {
        // IServerServices exposes ITorrentGrabber because Abstractions cannot
        // reference Core. If this stops compiling, that constraint was violated.
        Assert.True(typeof(ITorrentGrabber).IsAssignableFrom(typeof(TorrentGrabber)));
    }
}
