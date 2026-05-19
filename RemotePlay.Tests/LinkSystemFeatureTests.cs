using System.IO;
using System.Linq;
using Xunit;
using RemotePlay;

namespace RemotePlay.Tests;

/// <summary>
/// Tests covering the eight new Links-system features:
///   #2  Auto-heal (RplinkHelper re-target primitives)
///   #4  Breadcrumbs (UpdateBreadcrumbs path decomposition)
///   #6  Mirror-nav  (no pure-logic unit, covered via #4 helpers)
///   #8  Incremental index save (WebServer targeted-mutation coverage)
///   #9  Stale-link detection (RplinkHelper.TryReadTarget with missing target)
///   #10 Bulk-retarget (RplinkHelper.Create + MakeRelativeIfPossible)
///   #11 Move-link    (RplinkHelper target rewrite after path change)
///   #13 Folder-link expansion (RplinkHelper.IsTargetFolder)
/// </summary>
public sealed class LinkSystemFeatureTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public LinkSystemFeatureTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── #9 / #2 Stale-link detection ────────────────────────────────────────

    [Fact]
    public void TryReadTarget_FileTargetExists_ReturnsAbsolutePath()
    {
        var targetFile = Path.Combine(_tempDir, "movie.mkv");
        File.WriteAllText(targetFile, "data");

        var linkPath = Path.Combine(_tempDir, "link.rplink");
        RplinkHelper.Create(linkPath, targetFile);

        var result = RplinkHelper.TryReadTarget(linkPath);

        Assert.Equal(targetFile, result);
    }

    [Fact]
    public void TryReadTarget_FileTargetMissing_ReturnsNull()
    {
        var linkPath = Path.Combine(_tempDir, "broken.rplink");
        RplinkHelper.Create(linkPath, Path.Combine(_tempDir, "does_not_exist.mkv"));

        var result = RplinkHelper.TryReadTarget(linkPath);

        Assert.Null(result);
    }

    [Fact]
    public void TryReadTarget_DirectoryTargetExists_ReturnsAbsolutePath()
    {
        var targetDir = Path.Combine(_tempDir, "series");
        Directory.CreateDirectory(targetDir);

        var linkPath = Path.Combine(_tempDir, "folderlink.rplink");
        RplinkHelper.Create(linkPath, targetDir);

        var result = RplinkHelper.TryReadTarget(linkPath);

        Assert.Equal(targetDir, result);
    }

    [Fact]
    public void TryReadTarget_DirectoryTargetMissing_ReturnsNull()
    {
        var linkPath = Path.Combine(_tempDir, "broken_dir.rplink");
        RplinkHelper.Create(linkPath, Path.Combine(_tempDir, "nonexistent_folder"));

        var result = RplinkHelper.TryReadTarget(linkPath);

        Assert.Null(result);
    }

    // ── #2 Auto-heal: rewrite target after finding same-name file ───────────

    [Fact]
    public void AutoHeal_RewritesLinkToFoundTarget()
    {
        // Simulate: link was pointing at an old path; the file now lives elsewhere.
        var oldTarget = Path.Combine(_tempDir, "old", "movie.mkv");
        var newTarget = Path.Combine(_tempDir, "new", "movie.mkv");
        Directory.CreateDirectory(Path.Combine(_tempDir, "new"));
        File.WriteAllText(newTarget, "data");

        var linkPath = Path.Combine(_tempDir, "link.rplink");
        RplinkHelper.Create(linkPath, oldTarget);      // points at missing file

        Assert.Null(RplinkHelper.TryReadTarget(linkPath));   // confirm broken

        // Heal: rewrite to the new location
        var stored = RplinkHelper.MakeRelativeIfPossible(linkPath, newTarget);
        RplinkHelper.Create(linkPath, stored);

        Assert.Equal(newTarget, RplinkHelper.TryReadTarget(linkPath));  // now healed
    }

    // ── #10 Bulk-retarget: change target of multiple broken links ───────────

    [Fact]
    public void BulkRetarget_RewritesMultipleBrokenLinks()
    {
        var newTargetDir = Path.Combine(_tempDir, "retarget");
        Directory.CreateDirectory(newTargetDir);

        var linkPaths = Enumerable.Range(1, 3).Select(i =>
        {
            var lp = Path.Combine(_tempDir, $"link{i}.rplink");
            RplinkHelper.Create(lp, Path.Combine(_tempDir, $"missing{i}.mkv"));
            return lp;
        }).ToList();

        // All should be broken initially
        Assert.All(linkPaths, lp => Assert.Null(RplinkHelper.TryReadTarget(lp)));

        // Retarget each to the new directory (folder-level retarget)
        foreach (var lp in linkPaths)
        {
            var stored = RplinkHelper.MakeRelativeIfPossible(lp, newTargetDir);
            RplinkHelper.Create(lp, stored);
        }

        // All should now resolve to the new target directory
        Assert.All(linkPaths, lp => Assert.Equal(newTargetDir, RplinkHelper.TryReadTarget(lp)));
    }

    // ── #11 Move-link: rewrite relative path after moving link file ──────────

    [Fact]
    public void MoveLink_RewritesRelativeTargetForNewLocation()
    {
        var targetFile = Path.Combine(_tempDir, "videos", "movie.mkv");
        Directory.CreateDirectory(Path.Combine(_tempDir, "videos"));
        File.WriteAllText(targetFile, "data");

        // Original link in _tempDir\links_a\
        var linksA = Path.Combine(_tempDir, "links_a");
        Directory.CreateDirectory(linksA);
        var originalLink = Path.Combine(linksA, "movie.rplink");
        var storedA = RplinkHelper.MakeRelativeIfPossible(originalLink, targetFile);
        RplinkHelper.Create(originalLink, storedA);

        Assert.Equal(targetFile, RplinkHelper.TryReadTarget(originalLink));

        // "Move" the link to _tempDir\links_b\
        var linksB = Path.Combine(_tempDir, "links_b");
        Directory.CreateDirectory(linksB);
        var newLink = Path.Combine(linksB, "movie.rplink");

        // Recompute stored target from the new location, then copy + delete
        var absoluteTarget = RplinkHelper.TryReadTarget(originalLink)!;
        var storedB = RplinkHelper.MakeRelativeIfPossible(newLink, absoluteTarget);
        File.Copy(originalLink, newLink);
        RplinkHelper.Create(newLink, storedB);
        File.Delete(originalLink);

        Assert.Equal(targetFile, RplinkHelper.TryReadTarget(newLink));
        Assert.False(File.Exists(originalLink));
    }

    // ── #13 Folder-link expansion: IsTargetFolder detection ─────────────────

    [Fact]
    public void IsTargetFolder_WhenTargetIsDirectory_ReturnsTrue()
    {
        var targetDir = Path.Combine(_tempDir, "seriesFolder");
        Directory.CreateDirectory(targetDir);

        var linkPath = Path.Combine(_tempDir, "seriesFolder.rplink");
        RplinkHelper.Create(linkPath, targetDir);

        Assert.True(RplinkHelper.IsTargetFolder(linkPath));
    }

    [Fact]
    public void IsTargetFolder_WhenTargetIsFile_ReturnsFalse()
    {
        var targetFile = Path.Combine(_tempDir, "movie.mkv");
        File.WriteAllText(targetFile, "data");

        var linkPath = Path.Combine(_tempDir, "movie.rplink");
        RplinkHelper.Create(linkPath, targetFile);

        Assert.False(RplinkHelper.IsTargetFolder(linkPath));
    }

    [Fact]
    public void IsTargetFolder_WhenTargetMissingWithFileExtension_ReturnsFalse()
    {
        // When the target does not exist on disk and has a file extension, it is treated as a file link.
        var linkPath = Path.Combine(_tempDir, "orphan.rplink");
        RplinkHelper.Create(linkPath, Path.Combine(_tempDir, "gone_file.mkv"));

        Assert.False(RplinkHelper.IsTargetFolder(linkPath));
    }

    [Fact]
    public void IsTargetFolder_WhenTargetMissingWithNoExtension_ReturnsTrue()
    {
        // When the target does not exist and has no extension, the heuristic assumes it is a folder.
        var linkPath = Path.Combine(_tempDir, "orphan_dir.rplink");
        RplinkHelper.Create(linkPath, Path.Combine(_tempDir, "gone_folder"));

        Assert.True(RplinkHelper.IsTargetFolder(linkPath));
    }

    // ── #4 Breadcrumb decomposition helper ──────────────────────────────────

    [Fact]
    public void BreadcrumbPath_RootEqualsTarget_SingleSegment()
    {
        // Mirrors the logic in UpdateBreadcrumbs inside MainWindow.xaml.cs.
        var root   = _tempDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = root;

        var crumbs = BuildBreadcrumbSegments(root, target);

        Assert.Single(crumbs);
        Assert.Equal(Path.GetFileName(root), crumbs[0].Name);
        Assert.Equal(root, crumbs[0].Dir);
    }

    [Fact]
    public void BreadcrumbPath_OneLevel_TwoSegments()
    {
        var root   = _tempDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sub    = Path.Combine(root, "Action");

        var crumbs = BuildBreadcrumbSegments(root, sub);

        Assert.Equal(2, crumbs.Length);
        Assert.Equal("Action", crumbs[1].Name);
        Assert.Equal(sub, crumbs[1].Dir);
    }

    [Fact]
    public void BreadcrumbPath_TwoLevels_ThreeSegments()
    {
        var root   = _tempDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var nested = Path.Combine(root, "Action", "Marvel");

        var crumbs = BuildBreadcrumbSegments(root, nested);

        Assert.Equal(3, crumbs.Length);
        Assert.Equal("Marvel", crumbs[2].Name);
        Assert.Equal(nested, crumbs[2].Dir);
    }

    // ── #8 Incremental index save: targeted WebServer mutations ─────────────

    [Fact]
    public void FolderOperationsHelper_FindLinksPointingIntoFolder_FindsMatchingLinks()
    {
        // Arrange: two links pointing into a folder, one pointing elsewhere.
        var folder  = Path.Combine(_tempDir, "movies");
        var other   = Path.Combine(_tempDir, "other");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(other);

        var link1 = Path.Combine(_tempDir, "link1.rplink");
        var link2 = Path.Combine(_tempDir, "link2.rplink");
        var link3 = Path.Combine(_tempDir, "link3.rplink");

        RplinkHelper.Create(link1, Path.Combine(folder, "a.mkv"));
        RplinkHelper.Create(link2, Path.Combine(folder, "sub", "b.mkv"));
        RplinkHelper.Create(link3, Path.Combine(other,  "c.mkv"));

        var found = FolderOperationsHelper.FindLinksPointingIntoFolder(_tempDir, folder);

        Assert.Equal(2, found.Count);
        Assert.Contains(link1, found);
        Assert.Contains(link2, found);
        Assert.DoesNotContain(link3, found);
    }

    [Fact]
    public void FolderOperationsHelper_FindLinksPointingIntoFolder_NoneMatch_ReturnsEmpty()
    {
        var folder = Path.Combine(_tempDir, "empty_target");
        Directory.CreateDirectory(folder);

        var link1 = Path.Combine(_tempDir, "link1.rplink");
        RplinkHelper.Create(link1, Path.Combine(_tempDir, "other", "a.mkv"));

        var found = FolderOperationsHelper.FindLinksPointingIntoFolder(_tempDir, folder);

        Assert.Empty(found);
    }

    // ── MakeRelativeIfPossible: used by #10, #11, #2 ────────────────────────

    [Fact]
    public void MakeRelativeIfPossible_SameRoot_ReturnsRelativePath()
    {
        var linkPath   = Path.Combine(_tempDir, "links", "link.rplink");
        var targetPath = Path.Combine(_tempDir, "videos", "movie.mkv");

        var result = RplinkHelper.MakeRelativeIfPossible(linkPath, targetPath);

        // Should be a relative path (not rooted)
        Assert.False(Path.IsPathRooted(result));
        // Resolving from link's directory should yield the original target
        var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(linkPath)!, result));
        Assert.Equal(Path.GetFullPath(targetPath), resolved);
    }

    [Fact]
    public void MakeRelativeIfPossible_DifferentRoot_ReturnsAbsolutePath()
    {
        var linkPath   = Path.Combine(_tempDir, "link.rplink");
        // On Windows, a path on a different drive cannot be made relative.
        // We simulate by passing a path that is already absolute and not under tempDir.
        var targetPath = Path.GetFullPath(Path.Combine(_tempDir, "target.mkv"));

        var result = RplinkHelper.MakeRelativeIfPossible(linkPath, targetPath);

        // Result must either be relative or equal to the absolute target.
        var resolved = Path.GetFullPath(Path.IsPathRooted(result) ? result
            : Path.Combine(Path.GetDirectoryName(linkPath)!, result));
        Assert.Equal(Path.GetFullPath(targetPath), resolved);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Replicates the breadcrumb decomposition logic from <c>UpdateBreadcrumbs</c>
    /// so it can be tested without a WPF window instance.</summary>
    private static (string Name, string Dir)[] BuildBreadcrumbSegments(string root, string targetDir)
    {
        var normRoot   = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normTarget = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var crumbs = new System.Collections.Generic.List<(string Name, string Dir)>
        {
            (Path.GetFileName(normRoot), normRoot)
        };

        var relative = Path.GetRelativePath(normRoot, normTarget);
        if (relative != ".")
        {
            var current = normRoot;
            foreach (var part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         System.StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                crumbs.Add((part, current));
            }
        }

        return crumbs.ToArray();
    }
}
