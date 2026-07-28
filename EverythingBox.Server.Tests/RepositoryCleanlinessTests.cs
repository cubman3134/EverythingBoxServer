using System.Diagnostics;
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
    ];

    // .superpowers is gitignored planning scratch (task briefs/reports) that legitimately
    // discusses denylisted terms as documentation, not shipped repository content — skip it
    // the same way .git itself is skipped.
    private static readonly string[] SkipDirectories =
        [".git", "bin", "obj", ".vs", "artifacts", "TestResults", ".superpowers"];

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
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

            // This test file necessarily contains the denylist itself.
            if (relative.EndsWith(nameof(RepositoryCleanlinessTests) + ".cs", StringComparison.Ordinal)) continue;

            string text;
            try { text = File.ReadAllText(path); }
            catch (IOException) { continue; }

            foreach (var term in Denylist)
                if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                    offences.Add($"{relative}: '{term}'");
        }

        Assert.True(offences.Count == 0,
            "This repository must not name a content source:\n  " + string.Join("\n  ", offences));
    }

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
        var messages = Git(root, $"log --all --format=%H%n%B --grep=\"{pattern}\" -i");

        Assert.True(string.IsNullOrWhiteSpace(found),
            $"A content source appears in these commits' changes:\n{found}\n" +
            "History is public. Rewriting it is the only fix — do not just delete the line.");

        Assert.True(string.IsNullOrWhiteSpace(messages),
            $"A content source appears in these commit messages:\n{messages}");
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
