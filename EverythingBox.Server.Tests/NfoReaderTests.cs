using EverythingBox.Server.LocalLibrary;

namespace EverythingBox.Server.Tests;

public class NfoReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ebs-nfo-" + Guid.NewGuid().ToString("N"));
    public NfoReaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } GC.SuppressFinalize(this); }

    private string Write(string name, string xml) { var p = Path.Combine(_dir, name); File.WriteAllText(p, xml); return p; }

    [Fact]
    public void Reads_movie_title_year_and_plot()
    {
        var p = Write("m.nfo", "<movie><title>The Matrix</title><year>1999</year><plot>A hacker learns the truth.</plot></movie>");
        var info = NfoReader.TryRead(p);
        Assert.NotNull(info);
        Assert.Equal("The Matrix", info!.Title);
        Assert.Equal(1999, info.Year);
        Assert.Equal("A hacker learns the truth.", info.Plot);
    }

    [Fact]
    public void Reads_episodedetails_and_tvshow_roots()
    {
        Assert.Equal("Pilot", NfoReader.TryRead(Write("e.nfo", "<episodedetails><title>Pilot</title><plot>It begins.</plot></episodedetails>"))!.Title);
        Assert.Equal("Some Show", NfoReader.TryRead(Write("t.nfo", "<tvshow><title>Some Show</title><plot>About a show.</plot></tvshow>"))!.Title);
    }

    [Fact]
    public void Malformed_or_missing_yields_null()
    {
        Assert.Null(NfoReader.TryRead(Write("bad.nfo", "<movie><title>oops")));
        Assert.Null(NfoReader.TryRead(Path.Combine(_dir, "does-not-exist.nfo")));
    }

    [Fact]
    public void A_DTD_entity_nfo_is_refused_without_expanding_it()
    {
        // If the reader honored the DTD it would either expand &xxe; or throw on the external ref.
        // Hardened settings must make this return null and never read the referenced file.
        var secret = Write("secret.txt", "TOPSECRET");
        var xml = $"<?xml version=\"1.0\"?><!DOCTYPE movie [<!ENTITY xxe SYSTEM \"file://{secret.Replace("\\","/")}\">]><movie><title>&xxe;</title></movie>";
        var info = NfoReader.TryRead(Write("xxe.nfo", xml));
        // Either null (DTD prohibited → throw → null) or a non-null with Title NOT containing the secret.
        Assert.True(info is null || !(info.Title ?? "").Contains("TOPSECRET"));
    }
}
