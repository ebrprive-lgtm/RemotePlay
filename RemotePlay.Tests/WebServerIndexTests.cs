using System.IO;
using System.Reflection;
using RemotePlay.Models;
using RemotePlay.Services;
using Xunit;

namespace RemotePlay.Tests;

/// <summary>
/// Tests for the in-memory index query and mutation methods on <see cref="WebServer"/>
/// (CountIndexedLinksPointingIntoFolder, GetIndexedLinkSourcesForFile, GetIndexedPathSet,
///  IndexRemoveUnderPath, IndexAddOrUpdateFile, IndexRenamePrefix, IndexRenameFile).
/// These methods only touch in-memory state so no HTTP server is started.
/// </summary>
public sealed class WebServerIndexTests : IDisposable
{
    private readonly WebServer _server;

    public WebServerIndexTests()
    {
        _server = new WebServer(new AppConfig { Port = 0 }, BuildMinimalCallbacks());
    }

    public void Dispose() => _server.Stop();

    // ── CountIndexedLinksPointingIntoFolder ─────────────────────────────────

    [Fact]
    public void CountIndexedLinksPointingIntoFolder_EmptyIndex_ReturnsZero()
    {
        var count = _server.CountIndexedLinksPointingIntoFolder(@"C:\Movies");

        Assert.Equal(0, count);
    }

    [Fact]
    public void CountIndexedLinksPointingIntoFolder_NoLinksInFolder_ReturnsZero()
    {
        SeedIndex([
            MakeFile(@"C:\Movies\Action\film.mp4"),
            MakeFile(@"C:\Movies\Drama\drama.mp4"),
        ]);

        var count = _server.CountIndexedLinksPointingIntoFolder(@"C:\Movies\Action");

        Assert.Equal(0, count);
    }

    [Fact]
    public void CountIndexedLinksPointingIntoFolder_LinkInsideFolder_ReturnsOne()
    {
        SeedIndex([
            MakeFile(@"C:\Movies\Action\film.mp4"),
            MakeLink(@"C:\Movies\Action\alias.mp4", @"C:\Links\alias.rplink"),
        ]);

        var count = _server.CountIndexedLinksPointingIntoFolder(@"C:\Movies\Action");

        Assert.Equal(1, count);
    }

    [Fact]
    public void CountIndexedLinksPointingIntoFolder_MultipleLinksInSubfolders_CountsAll()
    {
        SeedIndex([
            MakeLink(@"C:\Movies\Action\a.mp4", @"C:\Links\a.rplink"),
            MakeLink(@"C:\Movies\Action\sub\b.mp4", @"C:\Links\b.rplink"),
            MakeLink(@"C:\Movies\Drama\c.mp4", @"C:\Links\c.rplink"),
        ]);

        var count = _server.CountIndexedLinksPointingIntoFolder(@"C:\Movies\Action");

        Assert.Equal(2, count);
    }

    [Fact]
    public void CountIndexedLinksPointingIntoFolder_IsCaseInsensitive()
    {
        SeedIndex([MakeLink(@"C:\Movies\action\film.mp4", @"C:\Links\film.rplink")]);

        var count = _server.CountIndexedLinksPointingIntoFolder(@"C:\Movies\ACTION");

        Assert.Equal(1, count);
    }

    // ── GetIndexedLinkSourcesForFile ─────────────────────────────────────────

    [Fact]
    public void GetIndexedLinkSourcesForFile_EmptyIndex_ReturnsNull()
    {
        var result = _server.GetIndexedLinkSourcesForFile(@"C:\Movies\film.mp4");

        Assert.Null(result);
    }

    [Fact]
    public void GetIndexedLinkSourcesForFile_NoMatchingLink_ReturnsEmptyArray()
    {
        SeedIndex([MakeFile(@"C:\Movies\other.mp4")]);

        var result = _server.GetIndexedLinkSourcesForFile(@"C:\Movies\film.mp4");

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void GetIndexedLinkSourcesForFile_SingleMatchingLink_ReturnsLinkSourcePath()
    {
        SeedIndex([MakeLink(@"C:\Movies\film.mp4", @"C:\Links\film.rplink")]);

        var result = _server.GetIndexedLinkSourcesForFile(@"C:\Movies\film.mp4");

        Assert.NotNull(result);
        var single = Assert.Single(result);
        Assert.Equal(@"C:\Links\film.rplink", single);
    }

    [Fact]
    public void GetIndexedLinkSourcesForFile_MultipleLinksToSameFile_ReturnsAllSources()
    {
        SeedIndex([
            MakeLink(@"C:\Movies\film.mp4", @"C:\Links\link1.rplink"),
            MakeLink(@"C:\Movies\film.mp4", @"C:\Links\link2.rplink"),
        ]);

        var result = _server.GetIndexedLinkSourcesForFile(@"C:\Movies\film.mp4");

        Assert.NotNull(result);
        Assert.Equal(2, result.Length);
    }

    [Fact]
    public void GetIndexedLinkSourcesForFile_IsCaseInsensitive()
    {
        SeedIndex([MakeLink(@"C:\Movies\Film.mp4", @"C:\Links\film.rplink")]);

        var result = _server.GetIndexedLinkSourcesForFile(@"C:\Movies\FILM.MP4");

        Assert.NotNull(result);
        Assert.Single(result);
    }

    // ── GetIndexedPathSet ────────────────────────────────────────────────────

    [Fact]
    public void GetIndexedPathSet_EmptyIndex_ReturnsEmptySet()
    {
        var set = _server.GetIndexedPathSet();

        Assert.Empty(set);
    }

    [Fact]
    public void GetIndexedPathSet_RegularFile_ContainsFilePath()
    {
        SeedIndex([MakeFile(@"C:\Movies\film.mp4")]);

        var set = _server.GetIndexedPathSet();

        Assert.Contains(Path.GetFullPath(@"C:\Movies\film.mp4"), set);
    }

    [Fact]
    public void GetIndexedPathSet_LinkEntry_ContainsBothFilePathAndLinkSourcePath()
    {
        SeedIndex([MakeLink(@"C:\Movies\film.mp4", @"C:\Links\film.rplink")]);

        var set = _server.GetIndexedPathSet();

        Assert.Contains(Path.GetFullPath(@"C:\Movies\film.mp4"), set);
        Assert.Contains(Path.GetFullPath(@"C:\Links\film.rplink"), set);
    }

    [Fact]
    public void GetIndexedPathSet_IsCaseInsensitive()
    {
        SeedIndex([MakeFile(@"C:\Movies\film.mp4")]);

        var set = _server.GetIndexedPathSet();

        Assert.Contains(Path.GetFullPath(@"C:\Movies\FILM.MP4"), set);
    }

    // ── IndexRemoveUnderPath ─────────────────────────────────────────────────

    [Fact]
    public void IndexRemoveUnderPath_RemovesFilesUnderGivenFolder()
    {
        SeedIndex([
            MakeFile(@"C:\Movies\Action\film.mp4"),
            MakeFile(@"C:\Movies\Drama\drama.mp4"),
        ]);

        _server.IndexRemoveUnderPath(@"C:\Movies\Action");

        var set = _server.GetIndexedPathSet();
        Assert.DoesNotContain(Path.GetFullPath(@"C:\Movies\Action\film.mp4"), set);
        Assert.Contains(Path.GetFullPath(@"C:\Movies\Drama\drama.mp4"), set);
    }

    [Fact]
    public void IndexRemoveUnderPath_RemovesSingleExactFile()
    {
        SeedIndex([
            MakeFile(@"C:\Movies\film.mp4"),
            MakeFile(@"C:\Movies\other.mp4"),
        ]);

        _server.IndexRemoveUnderPath(@"C:\Movies\film.mp4");

        var set = _server.GetIndexedPathSet();
        Assert.DoesNotContain(Path.GetFullPath(@"C:\Movies\film.mp4"), set);
        Assert.Contains(Path.GetFullPath(@"C:\Movies\other.mp4"), set);
    }

    [Fact]
    public void IndexRemoveUnderPath_RemovesLinkEntriesWhoseLinkSourcePathMatchesPrefix()
    {
        SeedIndex([MakeLink(@"C:\Movies\film.mp4", @"C:\Links\sub\film.rplink")]);

        _server.IndexRemoveUnderPath(@"C:\Links\sub");

        Assert.Empty(_server.GetIndexedPathSet());
    }

    [Fact]
    public void IndexRemoveUnderPath_EmptyIndex_DoesNotThrow()
    {
        var exception = Record.Exception(() => _server.IndexRemoveUnderPath(@"C:\Movies\Action"));

        Assert.Null(exception);
    }

    // ── IndexRenamePrefix ────────────────────────────────────────────────────

    [Fact]
    public void IndexRenamePrefix_UpdatesFilePathsUnderOldPrefix()
    {
        SeedIndex([MakeFile(@"C:\Movies\Action\film.mp4")]);

        _server.IndexRenamePrefix(@"C:\Movies\Action", @"C:\Movies\Renamed");

        var set = _server.GetIndexedPathSet();
        Assert.Contains(Path.GetFullPath(@"C:\Movies\Renamed\film.mp4"), set);
        Assert.DoesNotContain(Path.GetFullPath(@"C:\Movies\Action\film.mp4"), set);
    }

    [Fact]
    public void IndexRenamePrefix_LeavesEntriesOutsidePrefixUnchanged()
    {
        SeedIndex([
            MakeFile(@"C:\Movies\Action\film.mp4"),
            MakeFile(@"C:\Movies\Drama\drama.mp4"),
        ]);

        _server.IndexRenamePrefix(@"C:\Movies\Action", @"C:\Movies\Renamed");

        var set = _server.GetIndexedPathSet();
        Assert.Contains(Path.GetFullPath(@"C:\Movies\Drama\drama.mp4"), set);
    }

    [Fact]
    public void IndexRenamePrefix_UpdatesLinkSourcePathsUnderOldPrefix()
    {
        SeedIndex([MakeLink(@"C:\Movies\film.mp4", @"C:\Links\old\film.rplink")]);

        _server.IndexRenamePrefix(@"C:\Links\old", @"C:\Links\new");

        var set = _server.GetIndexedPathSet();
        Assert.Contains(Path.GetFullPath(@"C:\Links\new\film.rplink"), set);
        Assert.DoesNotContain(Path.GetFullPath(@"C:\Links\old\film.rplink"), set);
    }

    // ── IndexRenameFile ──────────────────────────────────────────────────────

    [Fact]
    public void IndexRenameFile_UpdatesMatchingFilePath()
    {
        SeedIndex([MakeFile(@"C:\Movies\old.mp4")]);

        _server.IndexRenameFile(@"C:\Movies\old.mp4", @"C:\Movies\new.mp4");

        var set = _server.GetIndexedPathSet();
        Assert.Contains(Path.GetFullPath(@"C:\Movies\new.mp4"), set);
        Assert.DoesNotContain(Path.GetFullPath(@"C:\Movies\old.mp4"), set);
    }

    [Fact]
    public void IndexRenameFile_UpdatesMatchingLinkSourcePath()
    {
        SeedIndex([MakeLink(@"C:\Movies\film.mp4", @"C:\Links\old.rplink")]);

        _server.IndexRenameFile(@"C:\Links\old.rplink", @"C:\Links\new.rplink");

        var set = _server.GetIndexedPathSet();
        Assert.Contains(Path.GetFullPath(@"C:\Links\new.rplink"), set);
        Assert.DoesNotContain(Path.GetFullPath(@"C:\Links\old.rplink"), set);
    }

    [Fact]
    public void IndexRenameFile_LeavesUnrelatedEntriesUnchanged()
    {
        SeedIndex([
            MakeFile(@"C:\Movies\old.mp4"),
            MakeFile(@"C:\Movies\other.mp4"),
        ]);

        _server.IndexRenameFile(@"C:\Movies\old.mp4", @"C:\Movies\new.mp4");

        Assert.Contains(Path.GetFullPath(@"C:\Movies\other.mp4"), _server.GetIndexedPathSet());
    }

    [Fact]
    public void IndexRenameFile_EmptyIndex_DoesNotThrow()
    {
        var exception = Record.Exception(() =>
            _server.IndexRenameFile(@"C:\Movies\old.mp4", @"C:\Movies\new.mp4"));

        Assert.Null(exception);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void SeedIndex(LibraryFile[] files)
    {
        // Access the private _libraryIndex field to pre-populate the index
        // without triggering a real filesystem scan.
        var field = typeof(WebServer).GetField("_libraryIndex",
            BindingFlags.NonPublic | BindingFlags.Instance);
        field!.SetValue(_server, files);
    }

    private static LibraryFile MakeFile(string filePath) =>
        new(Name: Path.GetFileName(filePath),
            FilePath: filePath,
            EncodedPath: Uri.EscapeDataString(filePath),
            FolderName: Path.GetDirectoryName(filePath) ?? string.Empty,
            SearchText: Path.GetFileName(filePath),
            IsLink: false);

    private static LibraryFile MakeLink(string filePath, string linkSourcePath) =>
        new(Name: Path.GetFileName(filePath),
            FilePath: filePath,
            EncodedPath: Uri.EscapeDataString(filePath),
            FolderName: Path.GetDirectoryName(filePath) ?? string.Empty,
            SearchText: Path.GetFileName(filePath),
            IsLink: true,
            LinkSourcePath: linkSourcePath);

    private static WebServerCallbacks BuildMinimalCallbacks() => new()
    {
        GetStatus             = () => new PlaybackStatus(),
        Play                  = _ => { },
        Stop                  = () => { },
        Pause                 = () => { },
        Seek                  = _ => { },
        Skip                  = _ => { },
        SetVolume             = _ => { },
        ToggleMute            = () => { },
        SetBrightness         = _ => { },
        SetSaturation         = _ => { },
        SetZoom               = _ => { },
        SetAudioBoost         = _ => { },
        SetPlaybackSpeed      = _ => { },
        ToggleSubtitles       = () => { },
        SetAudioTrack         = _ => { },
        SetSubtitleTrack      = _ => { },
        SeekToChapter         = _ => { },
        SetEqPreset           = _ => { },
        SetReverbPreset       = _ => { },
        SetMusicReverbPreset  = _ => { },
        PlayAdjacent          = _ => { },
        Enqueue               = _ => { },
        RemoveFromQueue       = _ => { },
        MoveQueueItem         = (_, _) => { },
        ClearQueue            = () => { },
        ClearPlaybackHistory  = _ => { },
        MarkWatchedHistory    = (_, _) => { },
        GetDisplayDiagnostics = () => new DisplayDiagnostics(),
        FixAudio              = () => { },
        PlayMusic             = (_, _) => { },
        PauseMusic            = () => { },
        StopMusic             = () => { },
        GetMusicStatus        = () => new MusicStatus(false, false, string.Empty, string.Empty, string.Empty, string.Empty, 0, 0, string.Empty, -1, 0),
        SeekMusic             = _ => { },
        SetMusicVolume        = _ => { },
        SetMusicBoost         = _ => { },
        SetMusicNextTrack     = _ => { },
        RadioSearch           = (_, _, _, _, _) => Task.FromResult(new List<RadioStation>()),
        RadioTopStations      = (_, _) => Task.FromResult(new List<RadioStation>()),
        RadioGetTags          = _ => Task.FromResult(new List<string>()),
        RadioGetCountries     = () => Task.FromResult(new List<(string Code, string Name)>()),
        RadioPlay             = (_, _) => { },
        RadioStop             = () => { },
        RadioSetVolume        = _ => { },
        RadioSetBoost         = _ => { },
        RadioGetStatus        = () => new RadioStatus(false, string.Empty, string.Empty, string.Empty, 1, 1, 0, false, string.Empty, -1, 0),
        RadioGetFavorites     = () => new List<RadioStation>(),
        RadioToggleFavorite   = _ => { },
        RadioIsFavorite       = _ => false,
        RadioIsFavoriteByUrl  = _ => false,
        RadioIsFavoriteByName = (_, _) => false,
        RadioNotifyAlive      = () => { },
        RadioResolveUrl       = (_, _) => Task.FromResult(string.Empty),
        RadioSetReverbPreset  = _ => { },
        SetMusicEqPreset      = _ => { },
        RadioSetEqPreset      = _ => { },
        SaveExpertMode        = _ => { },
        SaveDebugMode         = _ => { },
        SaveSettings          = _ => { },
        RestartApp            = () => { },
    };
}
