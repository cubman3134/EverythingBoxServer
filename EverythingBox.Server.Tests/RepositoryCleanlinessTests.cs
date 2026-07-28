using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace EverythingBox.Server.Tests;

/// <summary>
/// This repository is public and ships no content source. These tests fail the build if
/// one appears — in the working tree OR anywhere in history, because history is what
/// people grep and a later deletion does not unpublish a name.
///
/// Prowlarr and Jackett are deliberately allowed: they are indexer MANAGERS a user
/// points the server at, not sources, and supporting them is the point.
/// </summary>
public class RepositoryCleanlinessTests
{
    /// <summary>Matched case-insensitively against file contents and git history.
    /// Add a term here when a source is retired; never remove one.</summary>
    private static readonly string[] Denylist =
    [
        "piratebay", "pirate bay", "apibay",
        "audiobookbay", "rarbg", "torrents-csv", "torrentscsv",
        "getcomics", "libgen", "library genesis", "mangadex",
        "lolroms", "blueroms", "archive.org", "internet archive",
        "flaresolverr", "allarr", "cinemeta", "1337x", "nyaa",
        "internetarchive",
    ];

    // .superpowers is gitignored planning scratch (task briefs/reports) that legitimately
    // discusses denylisted terms as documentation, not shipped repository content — skip it
    // the same way .git itself is skipped.
    private static readonly string[] SkipDirectories =
        [".git", "bin", "obj", ".vs", "artifacts", "TestResults", ".superpowers"];

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")) && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void No_content_source_appears_in_the_working_tree()
    {
        var root = RepositoryRoot();
        var offences = new List<string>();

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path);
            if (relative.Split(Path.DirectorySeparatorChar).Any(SkipDirectories.Contains)) continue;

            // This test file necessarily contains the denylist itself. Compare the file
            // name exactly (not a suffix match) — a suffix match would also exempt e.g.
            // "MyRepositoryCleanlinessTests.cs".
            if (Path.GetFileName(relative) == nameof(RepositoryCleanlinessTests) + ".cs") continue;

            foreach (var term in FindDenylistedTermsInFile(path))
                offences.Add($"{relative}: '{term}'");

            // Contents aren't the whole story — a file organized under a denylisted
            // directory/file name (e.g. Providers/<Source>/<Source>Provider.cs) can have
            // entirely generic contents and still identify the source. Check the path itself.
            foreach (var term in FindDenylistedTermsInPath(relative))
                offences.Add($"{relative}: '{term}' (in path)");
        }

        Assert.True(offences.Count == 0,
            "This repository must not name a content source:\n  " + string.Join("\n  ", offences));
    }

    private static List<string> FindDenylistedTermsInPath(string relativePath)
        => Denylist.Where(term => relativePath.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// File.ReadAllText trusts BOM sniffing and falls back to UTF-8, which misreads a
    /// BOM-less UTF-16LE file as UTF-8 — interleaving nulls and breaking the substring
    /// match, so a denylisted term in such a file would pass silently. Decode the raw
    /// bytes both ways instead of trusting one encoding; this is a guard, not a parser,
    /// so over-matching is the safe direction.
    /// </summary>
    private static List<string> FindDenylistedTermsInFile(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (IOException) { return []; }

        var asUtf8 = Encoding.UTF8.GetString(bytes);
        var asUtf16Le = Encoding.Unicode.GetString(bytes);

        return Denylist.Where(term =>
            asUtf8.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            asUtf16Le.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    [Fact]
    public void A_BOM_less_UTF16_file_containing_a_denylisted_term_is_still_caught()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            // Encoding.Unicode.GetBytes never writes a BOM (that only comes from
            // GetPreamble/StreamWriter) — this reproduces the case that ReadAllText misreads.
            File.WriteAllBytes(path, Encoding.Unicode.GetBytes($"prefix {Denylist[0]} suffix"));

            var found = FindDenylistedTermsInFile(path);

            Assert.Contains(Denylist[0], found);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // IMPORTANT: this test only sees what `git log --all` can see. A shallow clone
    // (`--depth=1`, e.g. `actions/checkout` without `fetch-depth: 0`) has no history to
    // search, so every check below passes vacuously on a clone that cannot prove
    // anything. CI must fetch full history or this gate is decorative.
    [Fact]
    public void No_content_source_appears_anywhere_in_history()
    {
        var root = RepositoryRoot();
        var pattern = string.Join("|", Denylist.Select(Regex.Escape));

        // -G searches added/removed lines across every commit; --all covers every ref.
        // This test file carries the denylist itself, so the commit that adds it would
        // otherwise trip the check — exclude it by pathspec.
        // NOTE: -G already treats its argument as a regex; --pickaxe-regex only modifies -S
        // and git rejects the combination with -G (exit 128). Passing --pickaxe-regex here
        // silently produced zero matches for every query, because the wrapper below only
        // reads stdout — a git error left `found` empty and the assertion vacuously green.
        var found = Git(root,
            $"log --all --format=%H -G\"{pattern}\" -i -- . \":(exclude)*RepositoryCleanlinessTests.cs\"");
        // --grep defaults to POSIX basic regex, where '|' is a literal pipe, not alternation —
        // the multi-term pattern above would silently match nothing without -E/--extended-regexp.
        var messages = Git(root, $"log --all --format=%H%n%B --grep=\"{pattern}\" -i -E");

        Assert.True(string.IsNullOrWhiteSpace(found),
            $"A content source appears in these commits' changes:\n{found}\n" +
            "History is public. Rewriting it is the only fix — do not just delete the line.");

        Assert.True(string.IsNullOrWhiteSpace(messages),
            $"A content source appears in these commit messages:\n{messages}");
    }

    [Fact]
    public void No_content_source_appears_in_a_path_that_ever_existed_in_history()
    {
        var root = RepositoryRoot();

        // -G/--grep above match line content and commit messages, never the
        // `diff --git a/... b/...` header, so a file whose entire identity is its path
        // (e.g. Providers/<Source>/<Source>Provider.cs with wholly generic contents)
        // is invisible to both. --name-only over every commit on every ref lists every
        // path that has ever existed, added or removed, so this catches that case —
        // including a path that was later renamed or deleted, since deletion in a public
        // repo does not unpublish history.
        var allPaths = Git(root, "log --all --name-only --format=")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var offences = new List<string>();
        foreach (var path in allPaths)
        {
            if (Path.GetFileName(path) == nameof(RepositoryCleanlinessTests) + ".cs") continue;

            foreach (var term in Denylist.Where(t => path.Contains(t, StringComparison.OrdinalIgnoreCase)))
                offences.Add($"{path}: '{term}'");
        }

        Assert.True(offences.Count == 0,
            "A content source appears in a file or directory name that existed in history:\n  " +
            string.Join("\n  ", offences) +
            "\nHistory is public. Rewriting it is the only fix — a later rename or deletion does not unpublish it.");
    }

    private static string Git(string workingDirectory, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        // A gate that shells out must not fail silently: a bad git invocation (wrong flag
        // combination, bad pathspec) exits non-zero with empty stdout, which would otherwise
        // read as "nothing found" and pass the assertion vacuously. Fail loudly instead.
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments} exited {process.ExitCode}: {error}");

        return output.Trim();
    }
}
