using System.IO;
using Xunit;
using RemotePlay;

namespace RemotePlay.Tests;

public class RplinkHelperTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public RplinkHelperTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // -- IsRplinkFile ---------------------------------------------------------

    [Fact]
    public void IsRplinkFile_WithRplinkExtension_ReturnsTrue()
    {
        Assert.True(RplinkHelper.IsRplinkFile("movie.rplink"));
    }

    [Fact]
    public void IsRplinkFile_WithUppercaseExtension_ReturnsTrue()
    {
        Assert.True(RplinkHelper.IsRplinkFile("movie.RPLINK"));
    }

    [Fact]
    public void IsRplinkFile_WithDifferentExtension_ReturnsFalse()
    {
        Assert.False(RplinkHelper.IsRplinkFile("movie.mp4"));
    }

    [Theory]
    [InlineData("1942 - SeriesTitle", "1940s")]
    [InlineData("1974 - MovieTitle", "1970s")]
    public void GetMasterLibraryTargetDirectory_UsesDecadeForYearPrefixedNames(
        string itemName,
        string expectedDecade)
    {
        var masterLibrary = Path.Combine(_tempDir, "@Master.Movies");

        Assert.Equal(
            Path.Combine(masterLibrary, expectedDecade),
            RplinkHelper.GetMasterLibraryTargetDirectory(masterLibrary, itemName));
    }

    [Fact]
    public void GetMasterLibraryTargetDirectory_WithoutYearKeepsOriginalLocation()
    {
        var masterLibrary = Path.Combine(_tempDir, "@Master.Movies");

        Assert.Equal(
            masterLibrary,
            RplinkHelper.GetMasterLibraryTargetDirectory(masterLibrary, "MovieTitle"));
    }

    // -- TryReadTargetRaw -----------------------------------------------------

    [Fact]
    public void TryReadTargetRaw_WithAbsolutePath_ReturnsThatPath()
    {
        var targetPath = @"C:\movies\film.mkv";
        var linkPath = Path.Combine(_tempDir, "link.rplink");
        File.WriteAllText(linkPath, targetPath);

        var result = RplinkHelper.TryReadTargetRaw(linkPath);

        Assert.Equal(targetPath, result);
    }

    [Fact]
    public void TryReadTargetRaw_WithRelativePath_ReturnsAbsolutePath()
    {
        var linkPath = Path.Combine(_tempDir, "link.rplink");
        File.WriteAllText(linkPath, "film.mkv");

        var result = RplinkHelper.TryReadTargetRaw(linkPath);

        Assert.Equal(Path.Combine(_tempDir, "film.mkv"), result);
    }

    [Fact]
    public void TryReadTargetRaw_WithQuotedAbsolutePath_ReturnsUnquotedPath()
    {
        var targetPath = Path.Combine(_tempDir, "quoted.mkv");
        var linkPath = Path.Combine(_tempDir, "quoted_link.rplink");
        File.WriteAllText(linkPath, $"\"{targetPath}\"");

        var result = RplinkHelper.TryReadTargetRaw(linkPath);

        Assert.Equal(targetPath, result);
    }

    [Fact]
    public void TryReadTargetRaw_WithTrailingExtraQuote_ReturnsNormalizedPath()
    {
        var targetPath = Path.Combine(_tempDir, "quoted2.mkv");
        var linkPath = Path.Combine(_tempDir, "quoted2_link.rplink");
        File.WriteAllText(linkPath, $"\"{targetPath}\"\"");

        var result = RplinkHelper.TryReadTargetRaw(linkPath);

        Assert.Equal(targetPath, result);
    }

    [Fact]
    public void TryReadTargetRaw_EmptyFileWithoutMatch_ReturnsExpectedMoviePattern()
    {
        var libraryRoot = Path.Combine(_tempDir, "EmptyLinkLibrary");
        var masterSeries = Path.Combine(libraryRoot, "@Master.Series");
        var masterMovies = Path.Combine(libraryRoot, "@Master.Movies");
        var links = Path.Combine(libraryRoot, "Links");
        Directory.CreateDirectory(masterSeries);
        Directory.CreateDirectory(masterMovies);
        Directory.CreateDirectory(links);

        var linkPath = Path.Combine(links, "link.rplink");
        File.WriteAllText(linkPath, "   ");

        var expectedPattern = Path.Combine(masterMovies, "link.*");
        var inspection = RplinkHelper.InspectTarget(linkPath);

        Assert.Equal(expectedPattern, RplinkHelper.TryReadTargetRaw(linkPath));
        Assert.Equal(RplinkHelper.RplinkTargetStatus.Missing, inspection.Status);
        Assert.Equal(expectedPattern, inspection.ResolvedTarget);
    }

    [Fact]
    public void TryReadTargetRaw_NonExistentFile_ReturnsNull()
    {
        Assert.Null(RplinkHelper.TryReadTargetRaw(Path.Combine(_tempDir, "missing.rplink")));
    }

    // -- IsTargetFolder -------------------------------------------------------

    [Fact]
    public void IsTargetFolder_WhenTargetIsExistingDirectory_ReturnsTrue()
    {
        var subDir = Path.Combine(_tempDir, "subdir");
        Directory.CreateDirectory(subDir);
        var linkPath = Path.Combine(_tempDir, "link.rplink");
        File.WriteAllText(linkPath, subDir);

        Assert.True(RplinkHelper.IsTargetFolder(linkPath));
    }

    [Fact]
    public void IsTargetFolder_WhenTargetIsExistingFile_ReturnsFalse()
    {
        var filePath = Path.Combine(_tempDir, "video.mkv");
        File.WriteAllText(filePath, "data");
        var linkPath = Path.Combine(_tempDir, "link.rplink");
        File.WriteAllText(linkPath, filePath);

        Assert.False(RplinkHelper.IsTargetFolder(linkPath));
    }

    // -- TryReadTarget --------------------------------------------------------

    [Fact]
    public void TryReadTarget_WhenTargetFileExists_ReturnsPath()
    {
        var filePath = Path.Combine(_tempDir, "video.mkv");
        File.WriteAllText(filePath, "data");
        var linkPath = Path.Combine(_tempDir, "link.rplink");
        File.WriteAllText(linkPath, filePath);

        Assert.Equal(filePath, RplinkHelper.TryReadTarget(linkPath));
    }

    [Fact]
    public void TryReadTarget_WithAtPrefixedPath_ResolvesAgainstNearestMasterRoot()
    {
        var libraryRoot = Path.Combine(_tempDir, "LibraryRoot");
        var masterRoot = Path.Combine(libraryRoot, "@Master.Movies");
        var nested = Path.Combine(libraryRoot, "Genre - Comedy", "Movies");
        Directory.CreateDirectory(masterRoot);
        Directory.CreateDirectory(nested);

        var targetFile = Path.Combine(masterRoot, "1941 - Abbott And Costello Buck Privates.mp4");
        File.WriteAllText(targetFile, "data");

        var linkPath = Path.Combine(nested, "1941 - Abbott And Costello Buck Privates.rplink");
        File.WriteAllText(linkPath, "@\\@Master.Movies\\1941 - Abbott And Costello Buck Privates.mp4");

        var result = RplinkHelper.TryReadTarget(linkPath);

        Assert.Equal(targetFile, result);
    }

    [Fact]
    public void TryReadTarget_WithAtPrefixedDecadeMoviePath_ResolvesAgainstNearestMasterRoot()
    {
        var libraryRoot = Path.Combine(_tempDir, "DecadeAnchoredLibrary");
        var masterMovies = Path.Combine(libraryRoot, "@Master.Movies");
        var decadeMovies = Path.Combine(masterMovies, "1940s");
        var linkDirectory = Path.Combine(libraryRoot, "Links");
        Directory.CreateDirectory(decadeMovies);
        Directory.CreateDirectory(linkDirectory);

        var movieName = "1942 - MovieTitle";
        var targetFile = Path.Combine(decadeMovies, movieName + ".mp4");
        File.WriteAllText(targetFile, "data");

        var linkPath = Path.Combine(linkDirectory, movieName + ".rplink");
        File.WriteAllText(linkPath, @"@\@Master.Movies\1940s\1942 - MovieTitle.mp4");

        Assert.Equal(targetFile, RplinkHelper.TryReadTarget(linkPath));
    }

    [Fact]
    public void InspectTarget_WithLegacyFlatMoviePath_ResolvesToDecadeFolder()
    {
        var libraryRoot = Path.Combine(_tempDir, "LegacyFlatMovieLibrary");
        var masterMovies = Path.Combine(libraryRoot, "@Master.Movies");
        var movieName = "1942 - MovieTitle";
        var decadeTarget = Path.Combine(masterMovies, "1940s", movieName + ".mp4");
        var linkPath = Path.Combine(libraryRoot, "Links", "movie-link.rplink");
        Directory.CreateDirectory(Path.GetDirectoryName(decadeTarget)!);
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        File.WriteAllText(decadeTarget, "movie");
        File.WriteAllText(linkPath, Path.Combine(masterMovies, movieName + ".mp4"));

        var inspection = RplinkHelper.InspectTarget(linkPath);

        Assert.Equal(RplinkHelper.RplinkTargetStatus.Resolved, inspection.Status);
        Assert.Equal(decadeTarget, inspection.ResolvedTarget);
        Assert.Equal(decadeTarget, RplinkHelper.TryReadTarget(linkPath));
    }

    [Fact]
    public void InspectTarget_WithLegacyFlatSeriesPath_ResolvesToDecadeFolder()
    {
        var libraryRoot = Path.Combine(_tempDir, "LegacyFlatSeriesLibrary");
        var masterSeries = Path.Combine(libraryRoot, "@Master.Series");
        var seriesName = "2019 - SeriesTitle";
        var decadeTarget = Path.Combine(masterSeries, "2010s", seriesName);
        var linkPath = Path.Combine(libraryRoot, "Links", "series-link.rplink");
        Directory.CreateDirectory(decadeTarget);
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        File.WriteAllText(linkPath, Path.Combine(masterSeries, seriesName));

        var inspection = RplinkHelper.InspectTarget(linkPath);

        Assert.Equal(RplinkHelper.RplinkTargetStatus.Resolved, inspection.Status);
        Assert.Equal(decadeTarget, inspection.ResolvedTarget);
        Assert.Equal(decadeTarget, RplinkHelper.TryReadTarget(linkPath));
        Assert.True(RplinkHelper.IsTargetFolder(linkPath));
    }

    [Fact]
    public void InspectTarget_DoesNotResolveLegacyMovieNameOutsideMasterFolder()
    {
        var libraryRoot = Path.Combine(_tempDir, "UnrelatedMovieLibrary");
        var decadeTarget = Path.Combine(libraryRoot, "@Master.Movies", "1940s", "1942 - MovieTitle.mp4");
        var staleTarget = Path.Combine(libraryRoot, "OldMovies", "1942 - MovieTitle.mp4");
        var linkPath = Path.Combine(libraryRoot, "Links", "movie-link.rplink");
        Directory.CreateDirectory(Path.GetDirectoryName(decadeTarget)!);
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        File.WriteAllText(decadeTarget, "movie");
        File.WriteAllText(linkPath, staleTarget);

        var inspection = RplinkHelper.InspectTarget(linkPath);

        Assert.Equal(RplinkHelper.RplinkTargetStatus.Missing, inspection.Status);
        Assert.Null(RplinkHelper.TryReadTarget(linkPath));
    }

    [Fact]
    public void TryReadTarget_WithSeriesDirective_ResolvesToFolderNamedAfterLink()
    {
        var libraryRoot = Path.Combine(_tempDir, "SeriesLibrary");
        var masterSeries = Path.Combine(libraryRoot, "@Master.Series");
        var links = Path.Combine(libraryRoot, "Links");
        var expectedFolder = Path.Combine(masterSeries, "1970 - Whatever");
        Directory.CreateDirectory(Path.Combine(libraryRoot, "@Master.Movies"));
        Directory.CreateDirectory(links);
        Directory.CreateDirectory(expectedFolder);
        Directory.CreateDirectory(Path.Combine(masterSeries, "1970s", "1970 - Whatever"));

        var linkPath = Path.Combine(links, "1970 - Whatever.rplink");
        File.WriteAllText(linkPath, "@series");

        Assert.Equal(expectedFolder, RplinkHelper.TryReadTarget(linkPath));
        Assert.True(RplinkHelper.IsTargetFolder(linkPath));
    }

    [Fact]
    public void TryReadTarget_WithSeriesDirective_FindsSeriesInDecadeFolder()
    {
        var libraryRoot = Path.Combine(_tempDir, "DecadeSeriesLibrary");
        var masterSeries = Path.Combine(libraryRoot, "@Master.Series");
        var links = Path.Combine(libraryRoot, "Links");
        var seriesName = "1942 - SeriesTitle";
        var seriesTarget = Path.Combine(masterSeries, "1940s", seriesName);
        Directory.CreateDirectory(seriesTarget);
        Directory.CreateDirectory(links);

        var linkPath = Path.Combine(links, seriesName + ".rplink");
        File.WriteAllText(linkPath, "@Series");

        Assert.Equal(seriesTarget, RplinkHelper.TryReadTarget(linkPath));
        Assert.True(RplinkHelper.IsTargetFolder(linkPath));
    }

    [Fact]
    public void InspectTarget_WithMissingSeriesDirective_ReturnsMissingFolder()
    {
        var libraryRoot = Path.Combine(_tempDir, "MissingSeriesLibrary");
        var masterSeries = Path.Combine(libraryRoot, "@Master.Series");
        var links = Path.Combine(libraryRoot, "Links");
        Directory.CreateDirectory(masterSeries);
        Directory.CreateDirectory(links);

        var linkPath = Path.Combine(links, "1970 - Missing.Series.rplink");
        File.WriteAllText(linkPath, "@Series");

        var inspection = RplinkHelper.InspectTarget(linkPath);

        Assert.Equal(RplinkHelper.RplinkTargetStatus.Missing, inspection.Status);
        Assert.Equal(
            Path.Combine(masterSeries, "1970s", "1970 - Missing.Series"),
            inspection.ResolvedTarget);
        Assert.True(RplinkHelper.IsTargetFolder(linkPath));
    }

    [Fact]
    public void TryReadTarget_WithMovieDirective_SelectsFirstSupportedMovieFile()
    {
        var libraryRoot = Path.Combine(_tempDir, "MovieLibrary");
        var masterMovies = Path.Combine(libraryRoot, "@Master.Movies");
        var links = Path.Combine(libraryRoot, "Links");
        var movieName = "1971 - MovieName";
        Directory.CreateDirectory(masterMovies);
        Directory.CreateDirectory(links);
        var decadeMovies = Path.Combine(masterMovies, "1970s");
        Directory.CreateDirectory(decadeMovies);

        var expectedMovie = Path.Combine(masterMovies, movieName + ".avi");
        File.WriteAllText(Path.Combine(masterMovies, movieName + ".mp4"), "mp4");
        File.WriteAllText(Path.Combine(decadeMovies, movieName + ".mkv"), "decade mkv");
        File.WriteAllText(expectedMovie, "avi");
        File.WriteAllText(Path.Combine(masterMovies, movieName + ".nfo"), "metadata");

        var linkPath = Path.Combine(links, movieName + ".rplink");
        File.WriteAllText(linkPath, "@Movie");

        Assert.Equal(expectedMovie, RplinkHelper.TryReadTarget(linkPath));
        Assert.False(RplinkHelper.IsTargetFolder(linkPath));
    }

    [Fact]
    public void TryReadTarget_WithMovieDirective_FindsVideoInDecadeFolder()
    {
        var libraryRoot = Path.Combine(_tempDir, "DecadeMovieLibrary");
        var masterMovies = Path.Combine(libraryRoot, "@Master.Movies");
        var links = Path.Combine(libraryRoot, "Links");
        var movieName = "1942 - MovieTitle";
        var decadeMovies = Path.Combine(masterMovies, "1940s");
        var moviePath = Path.Combine(decadeMovies, movieName + ".mov");
        Directory.CreateDirectory(decadeMovies);
        Directory.CreateDirectory(links);
        File.WriteAllText(Path.Combine(masterMovies, movieName + ".nfo"), "metadata");
        File.WriteAllText(moviePath, "movie");

        var linkPath = Path.Combine(links, movieName + ".rplink");
        File.WriteAllText(linkPath, "@Movie");

        Assert.Equal(moviePath, RplinkHelper.TryReadTarget(linkPath));
        Assert.False(RplinkHelper.IsTargetFolder(linkPath));
    }

    [Fact]
    public void InspectTarget_WithMissingMovieDirective_ReturnsExpectedWildcardAsMissing()
    {
        var libraryRoot = Path.Combine(_tempDir, "MissingMovieLibrary");
        var masterSeries = Path.Combine(libraryRoot, "@Master.Series");
        var masterMovies = Path.Combine(libraryRoot, "@Master.Movies");
        var links = Path.Combine(libraryRoot, "Links");
        Directory.CreateDirectory(masterSeries);
        Directory.CreateDirectory(links);

        var linkPath = Path.Combine(links, "1972 - MissingMovie.rplink");
        File.WriteAllText(linkPath, "@Movie");

        var expectedPattern = Path.Combine(masterMovies, "1970s", "1972 - MissingMovie.*");
        var inspection = RplinkHelper.InspectTarget(linkPath);

        Assert.Equal(expectedPattern, RplinkHelper.TryReadTargetRaw(linkPath));
        Assert.Null(RplinkHelper.TryReadTarget(linkPath));
        Assert.Equal(RplinkHelper.RplinkTargetStatus.Missing, inspection.Status);
        Assert.Equal(expectedPattern, inspection.ResolvedTarget);
        Assert.False(RplinkHelper.IsTargetFolder(linkPath));
    }

    [Fact]
    public void TryReadTarget_WithEmptyLink_PrefersMatchingSeriesFolder()
    {
        var libraryRoot = Path.Combine(_tempDir, "AutoSeriesLibrary");
        var masterSeries = Path.Combine(libraryRoot, "@Master.Series");
        var masterMovies = Path.Combine(libraryRoot, "@Master.Movies");
        var links = Path.Combine(libraryRoot, "Links");
        var seriesName = "1973 - Auto Series";
        var seriesTarget = Path.Combine(masterSeries, seriesName);
        Directory.CreateDirectory(seriesTarget);
        Directory.CreateDirectory(masterMovies);
        Directory.CreateDirectory(links);
        File.WriteAllText(Path.Combine(masterMovies, seriesName + ".mp4"), "movie");

        var linkPath = Path.Combine(links, seriesName + ".rplink");
        File.WriteAllText(linkPath, string.Empty);

        Assert.Equal(seriesTarget, RplinkHelper.TryReadTarget(linkPath));
        Assert.True(RplinkHelper.IsTargetFolder(linkPath));
    }

    [Fact]
    public void TryReadTarget_WithEmptyLink_FindsDecadeSeriesBeforeMovie()
    {
        var libraryRoot = Path.Combine(_tempDir, "AutoDecadeSeriesLibrary");
        var masterSeries = Path.Combine(libraryRoot, "@Master.Series");
        var masterMovies = Path.Combine(libraryRoot, "@Master.Movies");
        var links = Path.Combine(libraryRoot, "Links");
        var seriesName = "1942 - SeriesTitle";
        var seriesTarget = Path.Combine(masterSeries, "1940s", seriesName);
        Directory.CreateDirectory(seriesTarget);
        Directory.CreateDirectory(masterMovies);
        Directory.CreateDirectory(links);
        File.WriteAllText(Path.Combine(masterMovies, seriesName + ".mp4"), "movie");

        var linkPath = Path.Combine(links, seriesName + ".rplink");
        File.WriteAllText(linkPath, string.Empty);

        Assert.Equal(seriesTarget, RplinkHelper.TryReadTarget(linkPath));
        Assert.True(RplinkHelper.IsTargetFolder(linkPath));
    }

    [Fact]
    public void TryReadTarget_WithEmptyLink_FindsNestedSeriesBeforeMovie()
    {
        var libraryRoot = Path.Combine(_tempDir, "AutoNestedSeriesLibrary");
        var masterSeries = Path.Combine(libraryRoot, "@Master.Series");
        var masterMovies = Path.Combine(libraryRoot, "@Master.Movies");
        var links = Path.Combine(libraryRoot, "Links");
        var seriesName = "Auto Series";
        var seriesTarget = Path.Combine(masterSeries, "Drama", "International", seriesName);
        Directory.CreateDirectory(seriesTarget);
        Directory.CreateDirectory(Path.Combine(masterMovies, "Drama"));
        Directory.CreateDirectory(links);
        File.WriteAllText(Path.Combine(masterMovies, "Drama", seriesName + ".mp4"), "movie");

        var linkPath = Path.Combine(links, seriesName + ".rplink");
        File.WriteAllText(linkPath, string.Empty);

        Assert.Equal(seriesTarget, RplinkHelper.TryReadTarget(linkPath));
        Assert.True(RplinkHelper.IsTargetFolder(linkPath));
    }

    [Fact]
    public void TryReadTarget_WithEmptyLinkAndNoSeries_ResolvesSupportedMovieExtension()
    {
        var libraryRoot = Path.Combine(_tempDir, "AutoMovieLibrary");
        var masterMovies = Path.Combine(libraryRoot, "@Master.Movies");
        var links = Path.Combine(libraryRoot, "Links");
        var movieName = "1974 - Auto Movie";
        var decadeMovies = Path.Combine(masterMovies, "1970s");
        Directory.CreateDirectory(decadeMovies);
        Directory.CreateDirectory(links);

        var moviePath = Path.Combine(decadeMovies, movieName + ".flv");
        File.WriteAllText(moviePath, "movie");
        var linkPath = Path.Combine(links, movieName + ".rplink");
        File.WriteAllText(linkPath, " \r\n");

        Assert.Equal(moviePath, RplinkHelper.TryReadTarget(linkPath));
        Assert.False(RplinkHelper.IsTargetFolder(linkPath));
    }

    [Fact]
    public void TryReadTarget_WithEmptyLink_FindsNestedMovieWhenNoSeriesMatches()
    {
        var libraryRoot = Path.Combine(_tempDir, "AutoNestedMovieLibrary");
        var masterMovies = Path.Combine(libraryRoot, "@Master.Movies");
        var links = Path.Combine(libraryRoot, "Links");
        var movieName = "Auto Movie";
        var moviePath = Path.Combine(masterMovies, "Drama", "International", movieName + ".mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(moviePath)!);
        Directory.CreateDirectory(Path.Combine(libraryRoot, "@Master.Series"));
        Directory.CreateDirectory(links);
        File.WriteAllText(moviePath, "movie");

        var linkPath = Path.Combine(links, movieName + ".rplink");
        File.WriteAllText(linkPath, string.Empty);

        Assert.Equal(moviePath, RplinkHelper.TryReadTarget(linkPath));
        Assert.False(RplinkHelper.IsTargetFolder(linkPath));
    }

    [Fact]
    public void TryReadTarget_WhenTargetDoesNotExist_ReturnsNull()
    {
        var linkPath = Path.Combine(_tempDir, "link.rplink");
        File.WriteAllText(linkPath, Path.Combine(_tempDir, "ghost.mkv"));

        Assert.Null(RplinkHelper.TryReadTarget(linkPath));
    }

    [Fact]
    public void TryReadTarget_WhenTargetFileExistsAndPathIsQuoted_ReturnsPath()
    {
        var filePath = Path.Combine(_tempDir, "video_quoted.mkv");
        File.WriteAllText(filePath, "data");
        var linkPath = Path.Combine(_tempDir, "quoted_target_link.rplink");
        File.WriteAllText(linkPath, $"\"{filePath}\"\"");

        Assert.Equal(filePath, RplinkHelper.TryReadTarget(linkPath));
    }

    [Fact]
    public void InspectTarget_WhenTargetExists_ReturnsResolved()
    {
        var filePath = Path.Combine(_tempDir, "video2.mkv");
        File.WriteAllText(filePath, "data");
        var linkPath = Path.Combine(_tempDir, "link2.rplink");
        File.WriteAllText(linkPath, filePath);

        var inspection = RplinkHelper.InspectTarget(linkPath);

        Assert.Equal(RplinkHelper.RplinkTargetStatus.Resolved, inspection.Status);
        Assert.Equal(filePath, inspection.ResolvedTarget);
    }

    [Fact]
    public void InspectTarget_WhenTargetMissing_ReturnsMissing()
    {
        var missingPath = Path.Combine(_tempDir, "ghost2.mkv");
        var linkPath = Path.Combine(_tempDir, "link3.rplink");
        File.WriteAllText(linkPath, missingPath);

        var inspection = RplinkHelper.InspectTarget(linkPath);

        Assert.Equal(RplinkHelper.RplinkTargetStatus.Missing, inspection.Status);
        Assert.Equal(missingPath, inspection.ResolvedTarget);
    }

    [Fact]
    public void InspectTarget_WhenEmptyLinkHasNoMatch_ReturnsMissingMoviePattern()
    {
        var libraryRoot = Path.Combine(_tempDir, "EmptyLinkInspectionLibrary");
        var masterSeries = Path.Combine(libraryRoot, "@Master.Series");
        var masterMovies = Path.Combine(libraryRoot, "@Master.Movies");
        var links = Path.Combine(libraryRoot, "Links");
        Directory.CreateDirectory(masterSeries);
        Directory.CreateDirectory(masterMovies);
        Directory.CreateDirectory(links);

        var linkPath = Path.Combine(links, "empty.rplink");
        File.WriteAllText(linkPath, " ");
        var expectedPattern = Path.Combine(masterMovies, "empty.*");

        var inspection = RplinkHelper.InspectTarget(linkPath);

        Assert.Equal(RplinkHelper.RplinkTargetStatus.Missing, inspection.Status);
        Assert.Equal(expectedPattern, inspection.ResolvedTarget);
    }

    // -- Create ---------------------------------------------------------------

    [Fact]
    public void Create_WritesTargetPathToFile()
    {
        var linkPath = Path.Combine(_tempDir, "link.rplink");
        var targetPath = @"C:\movies\film.mkv";

        RplinkHelper.Create(linkPath, targetPath);

        Assert.Equal(targetPath, File.ReadAllText(linkPath));
    }

    [Fact]
    public void Create_WithEmptyLinkPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => RplinkHelper.Create("", @"C:\target.mkv"));
    }

    // -- MakeRelativeIfPossible -----------------------------------------------

    [Fact]
    public void MakeRelativeIfPossible_SameVolume_ReturnsRelativePath()
    {
        var linkPath = Path.Combine(_tempDir, "sub", "link.rplink");
        var targetPath = Path.Combine(_tempDir, "sub", "video.mkv");

        var result = RplinkHelper.MakeRelativeIfPossible(linkPath, targetPath);

        Assert.Equal("video.mkv", result);
    }
}
