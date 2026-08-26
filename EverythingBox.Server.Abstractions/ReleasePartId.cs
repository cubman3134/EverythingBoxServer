using System.Text;

namespace EverythingBox.Server.Abstractions;

/// <summary>
/// The id of ONE FILE INSIDE ONE RELEASE — a release id and a file name, joined so the
/// pair survives a round trip to the client and back.
/// <para>
/// A release is very often many files: a folder of numbered audio parts, a set of parts
/// with an internal table of contents, a cover image and a text file beside them.
/// Resolving such a release to "a link" hands back whichever file the narrowing happened
/// to rank first, which is one arbitrary slice of the thing the user asked for. So a
/// source that can enumerate a release names each file, and each name has to be something
/// the client can send back when it reaches that file.
/// </para>
/// <para>
/// WHAT A PART IS IDENTIFIED BY — its release and its file name, and never a link. A
/// resolved link is signed and short-lived: it is minted for one playback, and a listener
/// reaches the fortieth part of a fifteen-hour recording days after the first part was
/// signed. An id built out of a link is therefore an id that expires, and an expired id
/// looks to a user like the app breaking rather than a link ageing out. This id carries
/// no url, no credential and no expiry; the link is fetched fresh when the part is
/// reached.
/// </para>
/// <para>
/// THE SHAPE is <c>&lt;releaseId&gt;~&lt;base64url(fileName)&gt;</c>. The separator is
/// '~' because it is outside the base64url alphabet (A-Z a-z 0-9 '-' '_'), which every
/// release id in this host is spelled in, so the split point can never fall inside either
/// half. The file name is encoded rather than appended raw because it is arbitrary text
/// from a stranger's torrent — it contains spaces, slashes, '#', '?', '%' and worse, and
/// any of those unencoded would be re-interpreted by the URL path the id travels in.
/// </para>
/// <para>
/// Decoding is TOTAL and never throws: an id arrives from a client and is never trusted.
/// Anything that is not exactly this shape — no separator, an empty half, base64 that
/// does not decode, bytes that are not UTF-8 — is simply "not a part id", which every
/// caller already has to handle because most ids are not.
/// </para>
/// </summary>
public static class ReleasePartId
{
    /// <summary>The separator. Public so a test names the same character the codec does.</summary>
    public const char Separator = '~';

    /// <summary>
    /// Joins a release id and one of its file names into a part id. Returns an empty
    /// string when either half is missing — an id with no release or no file names
    /// nothing, and handing one out would produce a row that can never resolve.
    /// </summary>
    public static string Encode(string? releaseId, string? fileName)
    {
        if (string.IsNullOrEmpty(releaseId) || string.IsNullOrEmpty(fileName))
            return string.Empty;
        if (releaseId.Contains(Separator))
            return string.Empty;   // ambiguous: the split below could not tell the halves apart
        return releaseId + Separator + ToBase64Url(Encoding.UTF8.GetBytes(fileName));
    }

    /// <summary>
    /// Splits a part id back into its release id and file name. False — with both outputs
    /// empty — for anything that is not one, including a plain release id, which is the
    /// case every caller hits most often.
    /// </summary>
    public static bool TryDecode(string? partId, out string releaseId, out string fileName)
    {
        releaseId = string.Empty;
        fileName = string.Empty;
        if (string.IsNullOrEmpty(partId))
            return false;

        // The FIRST separator: a release id may not contain one (Encode refuses to mint
        // such an id), so the first is also the only one, and a '~' inside the encoded
        // file name is impossible because base64url has no such character.
        var cut = partId.IndexOf(Separator);
        if (cut <= 0 || cut == partId.Length - 1)
            return false;

        byte[] bytes;
        try
        {
            bytes = FromBase64Url(partId[(cut + 1)..]);
        }
        catch (FormatException)
        {
            return false;
        }

        string decoded;
        try
        {
            // Throw-on-invalid, not the replacement-character default: a file name that is
            // not the UTF-8 we encoded did not come from Encode, and silently turning bad
            // bytes into 'U+FFFD' would produce a name that matches no file in the release
            // — a failure one layer further on, with nothing left to say why.
            decoded = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (decoded.Length == 0)
            return false;

        releaseId = partId[..cut];
        fileName = decoded;
        return true;
    }

    /// <summary>Whether <paramref name="id"/> names a part rather than a whole release.</summary>
    public static bool IsPartId(string? id) => TryDecode(id, out _, out _);

    /// <summary>
    /// Which of <paramref name="available"/> is the file a part id named, or null when none
    /// of them is. THE ONE RULE, so that every source matching a part back to a file matches
    /// it the same way.
    /// <para>
    /// Exact first. Then case-insensitively, because some services normalise the case of a
    /// path segment between calls and a listener should not lose the rest of a book to that.
    /// Then — only when it is UNAMBIGUOUS — on the last path segment, so a listing that
    /// gained or lost a folder prefix still resolves. Two files called <c>01.mp3</c> in
    /// different folders are two different files, and ambiguity is refused rather than
    /// guessed: playing the wrong part is the defect this whole path exists to remove, and a
    /// wrong part plays perfectly, which is what makes it so hard to notice.
    /// </para>
    /// </summary>
    public static string? MatchFileName(IEnumerable<string> available, string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;

        var names = available as IReadOnlyList<string> ?? available.ToList();

        foreach (var name in names)
            if (string.Equals(name, fileName, StringComparison.Ordinal))
                return name;

        foreach (var name in names)
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                return name;

        var leaf = LastSegment(fileName);
        string? only = null;
        foreach (var name in names)
        {
            if (!string.Equals(LastSegment(name), leaf, StringComparison.OrdinalIgnoreCase))
                continue;
            if (only is not null)
                return null;
            only = name;
        }
        return only;
    }

    private static string LastSegment(string path)
    {
        var segments = path.Replace('\\', '/').Split('/');
        return segments[^1];
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}
