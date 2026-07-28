using System.Reflection;

namespace EverythingBox.Server.Core.Tests;

/// <summary>
/// Core is BCL-only by design: a plugin author should be able to depend on it without
/// dragging in a dependency tree. Anything needing a package belongs in the host behind
/// an interface. This test makes that a build failure rather than a convention.
/// </summary>
public class CoreDependencyTests
{
    private static readonly string[] Allowed =
    [
        "System",                                    // and every System.* assembly
        "netstandard",
        "EverythingBox.Server.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions", // transitively, via Abstractions
    ];

    [Fact]
    public void Core_references_only_the_BCL_and_Abstractions()
    {
        var core = typeof(EverythingBox.Server.Core.TorrentGrabber).Assembly;

        var unexpected = core.GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .Where(name => !Allowed.Contains(name, StringComparer.Ordinal)
                        && !name.StartsWith("System.", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(unexpected.Length == 0,
            "EverythingBox.Server.Core must stay BCL-only. Unexpected references: " +
            string.Join(", ", unexpected) +
            ". If a package is genuinely needed, it belongs in the host behind an interface " +
            "(see INestedArchiveReader / ITorrentDownloader), not in Core.");
    }
}
