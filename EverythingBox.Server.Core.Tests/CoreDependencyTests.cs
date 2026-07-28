using System.Reflection;
using System.Xml.Linq;

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

    /// <summary>
    /// Proves that no package's types are statically consumed by Core's compiled IL.
    /// Does NOT prove the csproj file itself is clean — an unused PackageReference would
    /// still reach consumers' restore graphs (SDK-style projects are transitive by default)
    /// but would not appear in GetReferencedAssemblies. Use PackageReferenceListIsEmpty
    /// to catch that case.
    /// </summary>
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

    /// <summary>
    /// Proves that the .csproj file contains no PackageReference elements.
    /// An unused PackageReference would pass the IL test above but still poison consumers'
    /// restore graphs (SDK-style projects make all references transitive). This test catches
    /// that case and ensures a package needed by Core belongs in the host behind an interface,
    /// not as a direct dependency of Core.
    /// </summary>
    [Fact]
    public void PackageReferenceListIsEmpty()
    {
        var csprojPath = LocateCoreProjectFile();
        var doc = XDocument.Load(csprojPath);

        var packageReferences = doc.Root
            ?.Descendants(XName.Get("PackageReference"))
            .ToArray() ?? [];

        Assert.True(packageReferences.Length == 0,
            $"EverythingBox.Server.Core must stay BCL-only. Found {packageReferences.Length} PackageReference(s): " +
            string.Join(", ", packageReferences.Select(pr => pr.Attribute("Include")?.Value ?? "(unknown)")) +
            ". If a package is genuinely needed, it belongs in the host behind an interface, not in Core.");
    }

    private static string LocateCoreProjectFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        // First, walk up to the repository root (marked by .git)
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")) && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException("Could not find .git directory by walking up from " + AppContext.BaseDirectory);

        // Now walk down looking for EverythingBox.Server.Core/EverythingBox.Server.Core.csproj
        var repoRoot = dir.FullName;
        var csproj = Path.Combine(repoRoot, "EverythingBox.Server.Core", "EverythingBox.Server.Core.csproj");

        if (File.Exists(csproj))
            return csproj;

        throw new InvalidOperationException(
            "Could not locate EverythingBox.Server.Core/EverythingBox.Server.Core.csproj at " + csproj);
    }
}
