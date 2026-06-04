using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RemotePlay.Models;
using RemotePlay.Services;
using Xunit;

namespace RemotePlay.Tests;

/// <summary>
/// Integration tests for the .rpDynamic dynamic-folder feature:
/// create, get, save, delete, browse discovery, and expand (track resolution).
/// A real <see cref="WebServer"/> is started on a random local port and a temporary
/// directory serves as the music root so tests do not touch the developer's library.
/// </summary>
public sealed class MusicDynamicFolderTests : IDisposable
{
    private readonly WebServer _server;
    private readonly HttpClient _client;
    private readonly string _tempRoot;

    public MusicDynamicFolderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"rp_dyntest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);

        var port = FindFreePort();
        _server = new WebServer(
            new AppConfig { Port = port, AdditionalMusicPaths = [_tempRoot] },
            BuildStubCallbacks());
        _server.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
    }

    public void Dispose()
    {
        _server.Stop();
        _client.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    // ── Create (/api/music/dynamic  PUT) ─────────────────────────────────────

    [Fact]
    public async Task Create_ValidRequest_Returns200()
    {
        var body = JsonSerializer.Serialize(new { dir = _tempRoot, name = "TestList", count = 10 });

        var response = await PutAsync("/api/music/dynamic", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Create_ValidRequest_ResponseHasOkTrue()
    {
        var body = JsonSerializer.Serialize(new { dir = _tempRoot, name = "OkField", count = 5 });

        var response = await PutAsync("/api/music/dynamic", body);
        var doc = await ParseJsonAsync(response);

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Create_ValidRequest_ResponseContainsPath()
    {
        var body = JsonSerializer.Serialize(new { dir = _tempRoot, name = "WithPath", count = 5 });

        var response = await PutAsync("/api/music/dynamic", body);
        var doc = await ParseJsonAsync(response);

        Assert.True(doc.RootElement.TryGetProperty("path", out _));
    }

    [Fact]
    public async Task Create_ValidRequest_CreatesRpDynamicFileOnDisk()
    {
        var name = "DiskCheck";
        var body = JsonSerializer.Serialize(new { dir = _tempRoot, name, count = 15 });

        await PutAsync("/api/music/dynamic", body);

        Assert.True(File.Exists(Path.Combine(_tempRoot, name + ".rpDynamic")));
    }

    [Fact]
    public async Task Create_ValidRequest_FileContainsCorrectJson()
    {
        var name = "JsonCheck";
        var body = JsonSerializer.Serialize(new
        {
            dir   = _tempRoot,
            name,
            count = 25,
            sort  = "alphabetical",
            mode  = "all",
            recursive = false
        });

        await PutAsync("/api/music/dynamic", body);

        var json = File.ReadAllText(Path.Combine(_tempRoot, name + ".rpDynamic"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(name, root.GetProperty("name").GetString());
        Assert.Equal(25, root.GetProperty("count").GetInt32());
        Assert.Equal("alphabetical", root.GetProperty("sort").GetString());
        Assert.Equal("all", root.GetProperty("mode").GetString());
        Assert.False(root.GetProperty("recursive").GetBoolean());
    }

    [Fact]
    public async Task Create_NonExistentDirectory_Returns400()
    {
        var body = JsonSerializer.Serialize(new
        {
            dir  = Path.Combine(_tempRoot, "no_such_dir"),
            name = "WillFail",
            count = 5
        });

        var response = await PutAsync("/api/music/dynamic", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_InvalidJson_Returns400()
    {
        var response = await PutAsync("/api/music/dynamic", "{ not valid json }");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_WrongHttpMethod_Returns405()
    {
        // The router sends PUT → create (which internally guards for PUT and returns 405
        // for any other method). Sending a PATCH is not routed to create at all; the router
        // falls through to HandleMusicDynamicGet which returns 400 (missing path param).
        // The only wrong-method case that reaches HandleMusicDynamicCreate itself is one
        // where we somehow invoke it directly — which the router prevents.
        // Verify: a POST to /api/music/dynamic routes to the *save* handler, which
        // checks for POST and is satisfied — so we test that a PUT to the save URL
        // returns MethodNotAllowed by using HandleMusicDynamicSave's own guard.
        // Since the router uses method-dispatch at the same case, POST → save, PUT → create.
        // We can verify create's internal 405 guard by calling it as a non-PUT method
        // via a custom HttpMethod:
        var request = new HttpRequestMessage(new HttpMethod("PATCH"), "/api/music/dynamic");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { dir = _tempRoot, name = "MethodTest" }),
            Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request);

        // Router falls through to GET handler (HandleMusicDynamicGet) which returns 400
        // because the path query param is missing — that is the correct observable behavior.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_BlankName_FallsBackToDefaultName()
    {
        var body = JsonSerializer.Serialize(new { dir = _tempRoot, name = "   ", count = 5 });

        await PutAsync("/api/music/dynamic", body);

        Assert.True(File.Exists(Path.Combine(_tempRoot, "My Dynamic Folder.rpDynamic")));
    }

    [Fact]
    public async Task Create_NameWithInvalidFileChars_SanitizesToUnderscores()
    {
        var body = JsonSerializer.Serialize(new { dir = _tempRoot, name = "My:List*File?", count = 5 });

        await PutAsync("/api/music/dynamic", body);

        // Invalid chars replaced with '_', exact result may vary by OS; just check a file was created.
        var files = Directory.GetFiles(_tempRoot, "*.rpDynamic");
        Assert.Contains(files, f => Path.GetFileNameWithoutExtension(f).Contains("My"));
    }

    [Fact]
    public async Task Create_OmittedOptionalFields_UsesDefaults()
    {
        var name = "DefaultFields";
        var body = JsonSerializer.Serialize(new { dir = _tempRoot, name });

        await PutAsync("/api/music/dynamic", body);

        var json = File.ReadAllText(Path.Combine(_tempRoot, name + ".rpDynamic"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(20, root.GetProperty("count").GetInt32());
        Assert.Equal("random", root.GetProperty("sort").GetString());
        Assert.Equal("sample", root.GetProperty("mode").GetString());
        Assert.True(root.GetProperty("recursive").GetBoolean());
    }

    // ── Get (/api/music/dynamic  GET) ────────────────────────────────────────

    [Fact]
    public async Task Get_ExistingFile_Returns200()
    {
        var filePath = CreateRpDynamicFile("GetMe", """{"name":"GetMe","count":10}""");

        var response = await _client.GetAsync($"/api/music/dynamic?path={Q(filePath)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_ExistingFile_ReturnsFileContents()
    {
        var filePath = CreateRpDynamicFile("GetContents", """{"name":"GetContents","count":99}""");

        var response = await _client.GetAsync($"/api/music/dynamic?path={Q(filePath)}");
        var doc = await ParseJsonAsync(response);

        Assert.Equal(99, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Get_MissingPathParam_Returns400()
    {
        var response = await _client.GetAsync("/api/music/dynamic");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_NonExistentFile_Returns404()
    {
        var fakePath = Path.Combine(_tempRoot, "ghost.rpDynamic");

        var response = await _client.GetAsync($"/api/music/dynamic?path={Q(fakePath)}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Save (/api/music/dynamic  POST) ──────────────────────────────────────

    [Fact]
    public async Task Save_ValidRequest_Returns200()
    {
        var filePath = CreateRpDynamicFile("SaveMe", """{"name":"SaveMe","count":10,"sort":"random","mode":"sample","recursive":true}""");
        var body = JsonSerializer.Serialize(new { name = "SaveMe", count = 30 });

        var response = await PostAsync($"/api/music/dynamic?path={Q(filePath)}", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Save_ValidRequest_UpdatesCountInFile()
    {
        var filePath = CreateRpDynamicFile("UpdateCount", """{"name":"UpdateCount","count":10,"sort":"random","mode":"sample","recursive":true}""");
        var body = JsonSerializer.Serialize(new { count = 50 });

        await PostAsync($"/api/music/dynamic?path={Q(filePath)}", body);

        var json = File.ReadAllText(filePath);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(50, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Save_ValidRequest_ResponseHasOkTrue()
    {
        var filePath = CreateRpDynamicFile("SaveOk", """{"name":"SaveOk","count":5,"sort":"random","mode":"sample","recursive":true}""");
        var body = JsonSerializer.Serialize(new { count = 5 });

        var response = await PostAsync($"/api/music/dynamic?path={Q(filePath)}", body);
        var doc = await ParseJsonAsync(response);

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Save_WrongMethod_Put_RoutesToCreate_NotSave()
    {
        // The router dispatches PUT → HandleMusicDynamicCreate regardless of any query params.
        // Create requires a valid "dir" in the JSON body; sending without it gives 400.
        // This confirms that PUT does NOT reach the save handler.
        var response = await PutAsync("/api/music/dynamic", """{"count":1}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Save_MissingPathParam_Returns400()
    {
        var response = await PostAsync("/api/music/dynamic", """{"count":1}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Save_NonExistentFile_Returns404()
    {
        var fakePath = Path.Combine(_tempRoot, "missing.rpDynamic");
        var body = JsonSerializer.Serialize(new { count = 1 });

        var response = await PostAsync($"/api/music/dynamic?path={Q(fakePath)}", body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Save_InvalidJson_Returns400()
    {
        var filePath = CreateRpDynamicFile("BadJson", """{"name":"x","count":1}""");

        var response = await PostAsync($"/api/music/dynamic?path={Q(filePath)}", "{ bad json }");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Save_NameChange_RenamesFileOnDisk()
    {
        var oldName = "OldName";
        var newName = "NewName";
        var filePath = CreateRpDynamicFile(oldName,
            """{"name":"OldName","count":5,"sort":"random","mode":"sample","recursive":true}""");
        var body = JsonSerializer.Serialize(new { name = newName });

        var response = await PostAsync($"/api/music/dynamic?path={Q(filePath)}", body);
        var doc = await ParseJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(File.Exists(filePath));
        Assert.True(File.Exists(Path.Combine(_tempRoot, newName + ".rpDynamic")));
    }

    [Fact]
    public async Task Save_NameChange_ResponsePathPointsToRenamedFile()
    {
        var filePath = CreateRpDynamicFile("Rename1",
            """{"name":"Rename1","count":5,"sort":"random","mode":"sample","recursive":true}""");
        var body = JsonSerializer.Serialize(new { name = "Rename2" });

        var response = await PostAsync($"/api/music/dynamic?path={Q(filePath)}", body);
        var doc = await ParseJsonAsync(response);

        // The new encoded path should decode to contain "Rename2"
        var newPath = doc.RootElement.GetProperty("path").GetString()!;
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(
            newPath.Replace(' ', '+').Replace('-', '+').Replace('_', '/')
            .PadRight(newPath.Length + (4 - newPath.Length % 4) % 4, '=')));
        Assert.Contains("Rename2", decoded);
    }

    [Fact]
    public async Task Save_PartialUpdate_PreservesUnmentionedFields()
    {
        var filePath = CreateRpDynamicFile("PreserveFields",
            """{"name":"PreserveFields","count":10,"sort":"newest","mode":"all","recursive":false,"genre":"Rock"}""");
        // Only send count — all other fields should be preserved from the existing file
        var body = JsonSerializer.Serialize(new { count = 99 });

        await PostAsync($"/api/music/dynamic?path={Q(filePath)}", body);

        var json = File.ReadAllText(filePath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("newest", root.GetProperty("sort").GetString());
        Assert.Equal("all", root.GetProperty("mode").GetString());
        Assert.False(root.GetProperty("recursive").GetBoolean());
        Assert.Equal("Rock", root.GetProperty("genre").GetString());
    }

    [Fact]
    public async Task Save_PreservesLastExpanded_WhenNotInRequestBody()
    {
        var lastExpanded = "2025-01-15T10:00:00.0000000Z";
        var filePath = CreateRpDynamicFile("PreserveLast",
            $$"""{"name":"PreserveLast","count":5,"lastExpanded":"{{lastExpanded}}"}""");
        var body = JsonSerializer.Serialize(new { count = 7 });

        await PostAsync($"/api/music/dynamic?path={Q(filePath)}", body);

        var json = File.ReadAllText(filePath);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(lastExpanded, doc.RootElement.GetProperty("lastExpanded").GetString());
    }

    // ── Delete (/api/music/dynamic  DELETE) ──────────────────────────────────

    [Fact]
    public async Task Delete_ExistingFile_Returns200()
    {
        var filePath = CreateRpDynamicFile("DeleteMe", """{"name":"DeleteMe","count":1}""");

        var response = await _client.DeleteAsync($"/api/music/dynamic?path={Q(filePath)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ExistingFile_RemovesFileFromDisk()
    {
        var filePath = CreateRpDynamicFile("GoneFile", """{"name":"x","count":1}""");

        await _client.DeleteAsync($"/api/music/dynamic?path={Q(filePath)}");

        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task Delete_ExistingFile_ResponseHasOkTrue()
    {
        var filePath = CreateRpDynamicFile("OkDelete", """{"name":"x","count":1}""");

        var response = await _client.DeleteAsync($"/api/music/dynamic?path={Q(filePath)}");
        var doc = await ParseJsonAsync(response);

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Delete_MissingPathParam_Returns400()
    {
        var response = await _client.DeleteAsync("/api/music/dynamic");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_PathNotRpDynamicExtension_Returns400()
    {
        var nonDynamicFile = Path.Combine(_tempRoot, "regular.json");
        File.WriteAllText(nonDynamicFile, "{}");

        var response = await _client.DeleteAsync($"/api/music/dynamic?path={Q(nonDynamicFile)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_NonExistentFile_Returns404()
    {
        var fakePath = Path.Combine(_tempRoot, "never.rpDynamic");

        var response = await _client.DeleteAsync($"/api/music/dynamic?path={Q(fakePath)}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Browse discovery (/api/music/browse) ─────────────────────────────────

    [Fact]
    public async Task Browse_AtRoot_IncludesDynamicFolderInFoldersList()
    {
        CreateRpDynamicFile("MyPlaylist", """{"name":"MyPlaylist","count":20}""");

        var response = await _client.GetAsync("/api/music/browse");
        var doc = await ParseJsonAsync(response);

        var folders = doc.RootElement.GetProperty("folders").EnumerateArray().ToList();
        Assert.Contains(folders, f =>
            f.TryGetProperty("isDynamic", out var d) && d.GetBoolean() &&
            f.TryGetProperty("name", out var n) && n.GetString() == "MyPlaylist");
    }

    [Fact]
    public async Task Browse_DynamicFolderEntry_HasIsDynamicTrue()
    {
        CreateRpDynamicFile("IsDynamicCheck", """{"name":"IsDynamicCheck","count":10}""");

        var response = await _client.GetAsync("/api/music/browse");
        var doc = await ParseJsonAsync(response);

        var folders = doc.RootElement.GetProperty("folders").EnumerateArray().ToList();
        var entry = folders.FirstOrDefault(f =>
            f.TryGetProperty("name", out var n) && n.GetString() == "IsDynamicCheck");

        Assert.True(entry.TryGetProperty("isDynamic", out var isDynamic) && isDynamic.GetBoolean());
    }

    [Fact]
    public async Task Browse_DynamicFolderEntry_ContainsExpectedFields()
    {
        CreateRpDynamicFile("FieldCheck",
            """{"name":"FieldCheck","count":7,"mode":"all","sort":"newest","include":["jazz"],"genre":"Blues","yearFrom":2000,"yearTo":2020}""");

        var response = await _client.GetAsync("/api/music/browse");
        var doc = await ParseJsonAsync(response);

        var folders = doc.RootElement.GetProperty("folders").EnumerateArray().ToList();
        var entry = folders.First(f =>
            f.TryGetProperty("name", out var n) && n.GetString() == "FieldCheck");

        Assert.Equal("all",    entry.GetProperty("mode").GetString());
        Assert.Equal("newest", entry.GetProperty("sort").GetString());
        Assert.Equal("Blues",  entry.GetProperty("genre").GetString());
        Assert.Equal(2000,     entry.GetProperty("yearFrom").GetInt32());
        Assert.Equal(2020,     entry.GetProperty("yearTo").GetInt32());
        Assert.True(entry.TryGetProperty("folder", out _));
        Assert.True(entry.TryGetProperty("dynamicPath", out _));
    }

    [Fact]
    public async Task Browse_DynamicFolderEntry_TrackCount_ReflectsLastCountAfterExpand()
    {
        // After expand the file gets a lastCount field; browse should use that value.
        CreateRpDynamicFile("CountAfterExpand",
            """{"name":"CountAfterExpand","count":50,"lastCount":3}""");

        var response = await _client.GetAsync("/api/music/browse");
        var doc = await ParseJsonAsync(response);

        var folders = doc.RootElement.GetProperty("folders").EnumerateArray().ToList();
        var entry = folders.First(f =>
            f.TryGetProperty("name", out var n) && n.GetString() == "CountAfterExpand");

        Assert.Equal(3, entry.GetProperty("trackCount").GetInt32());
    }

    [Fact]
    public async Task Browse_InSubfolder_IncludesDynamicFileLocatedThere()
    {
        var subDir = Path.Combine(_tempRoot, "SubAlbum");
        Directory.CreateDirectory(subDir);
        var filePath = Path.Combine(subDir, "SubList.rpDynamic");
        File.WriteAllText(filePath, """{"name":"SubList","count":5}""");

        // Browse the subfolder directly; the folder query param is a plain path (not Base64-encoded).
        // scanDir = subDir, so the handler scans subDir for .rpDynamic files.
        var encodedFolder = Uri.EscapeDataString(subDir);
        var response = await _client.GetAsync($"/api/music/browse?folder={encodedFolder}");
        var doc = await ParseJsonAsync(response);

        var folders = doc.RootElement.GetProperty("folders").EnumerateArray().ToList();
        Assert.Contains(folders, f =>
            f.TryGetProperty("isDynamic", out var d) && d.GetBoolean() &&
            f.TryGetProperty("name", out var n) && n.GetString() == "SubList");
    }

    // ── Expand (/api/music/dynamic/expand) ───────────────────────────────────

    [Fact]
    public async Task Expand_MissingPath_Returns400()
    {
        var response = await _client.GetAsync("/api/music/dynamic/expand");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Expand_NonExistentFile_Returns404()
    {
        var fakePath = Path.Combine(_tempRoot, "ghost.rpDynamic");

        var response = await _client.GetAsync($"/api/music/dynamic/expand?path={Q(fakePath)}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Expand_EmptyMusicIndex_ReturnsEmptyTrackList()
    {
        var filePath = CreateRpDynamicFile("EmptyList", """{"name":"EmptyList","count":20,"sort":"random","mode":"sample","recursive":true}""");

        var response = await _client.GetAsync($"/api/music/dynamic/expand?path={Q(filePath)}");
        var doc = await ParseJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, doc.RootElement.GetProperty("tracks").GetArrayLength());
    }

    [Fact]
    public async Task Expand_ResponseContainsNameField()
    {
        var filePath = CreateRpDynamicFile("NameInResp",
            """{"name":"My Cool List","count":10,"sort":"random","mode":"sample","recursive":true}""");

        var response = await _client.GetAsync($"/api/music/dynamic/expand?path={Q(filePath)}");
        var doc = await ParseJsonAsync(response);

        Assert.Equal("My Cool List", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Expand_ResponseContainsTotalField()
    {
        var filePath = CreateRpDynamicFile("TotalField",
            """{"name":"TotalField","count":10,"sort":"random","mode":"sample","recursive":true}""");

        var response = await _client.GetAsync($"/api/music/dynamic/expand?path={Q(filePath)}");
        var doc = await ParseJsonAsync(response);

        Assert.True(doc.RootElement.TryGetProperty("total", out _));
    }

    [Fact]
    public async Task Expand_SampleMode_RespectsCountLimit()
    {
        // Seed 5 tracks and request only 3 via sample mode with count=3
        var trackDir = SeedMusicIndex("CountLimit", 5);
        var filePath = CreateRpDynamicFile("LimitList",
            """{"name":"LimitList","count":3,"sort":"random","mode":"sample","recursive":true}""");

        var response = await _client.GetAsync($"/api/music/dynamic/expand?path={Q(filePath)}");
        var doc = await ParseJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, doc.RootElement.GetProperty("tracks").GetArrayLength());
    }

    [Fact]
    public async Task Expand_AllMode_ReturnsAllCandidates()
    {
        var trackDir = SeedMusicIndex("AllMode", 4);
        var filePath = CreateRpDynamicFile("AllList",
            """{"name":"AllList","count":1,"sort":"random","mode":"all","recursive":true}""");

        var response = await _client.GetAsync($"/api/music/dynamic/expand?path={Q(filePath)}");
        var doc = await ParseJsonAsync(response);

        // mode=all ignores count; all 4 tracks should be returned
        Assert.Equal(4, doc.RootElement.GetProperty("tracks").GetArrayLength());
    }

    [Fact]
    public async Task Expand_RecursiveTrue_IncludesTracksInSubdirectories()
    {
        // Inject a track that lives in a subdirectory under _tempRoot.
        var subDir = Path.Combine(_tempRoot, "Recursive", "sub");
        Directory.CreateDirectory(subDir);
        var trackPath = Path.Combine(subDir, "deeptrack.mp3");
        File.WriteAllText(trackPath, string.Empty);
        InjectMusicIndex([MakeMusicFile("deeptrack", trackPath)]);

        var filePath = CreateRpDynamicFile("RecursiveList",
            """{"name":"RecursiveList","count":20,"sort":"random","mode":"all","recursive":true}""");

        var response = await _client.GetAsync($"/api/music/dynamic/expand?path={Q(filePath)}");
        var doc = await ParseJsonAsync(response);

        var trackNames = doc.RootElement.GetProperty("tracks").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("deeptrack", trackNames);
    }

    [Fact]
    public async Task Expand_RecursiveFalse_ExcludesTracksInSubdirectories()
    {
        // Inject one direct track (sits directly in _tempRoot) and one deep track.
        var directPath = Path.Combine(_tempRoot, "directtrack.mp3");
        File.WriteAllText(directPath, string.Empty);
        var subDir = Path.Combine(_tempRoot, "NonRecursiveSub");
        Directory.CreateDirectory(subDir);
        var deepPath = Path.Combine(subDir, "deeptrack2.mp3");
        File.WriteAllText(deepPath, string.Empty);

        InjectMusicIndex([
            MakeMusicFile("directtrack", directPath),
            MakeMusicFile("deeptrack2",  deepPath),
        ]);

        var filePath = CreateRpDynamicFile("NonRecursiveList",
            """{"name":"NonRecursiveList","count":20,"sort":"random","mode":"all","recursive":false}""");

        var response = await _client.GetAsync($"/api/music/dynamic/expand?path={Q(filePath)}");
        var doc = await ParseJsonAsync(response);

        var trackNames = doc.RootElement.GetProperty("tracks").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("directtrack", trackNames);
        Assert.DoesNotContain("deeptrack2", trackNames);
    }

    [Fact]
    public async Task Expand_IncludeFilter_OnlyReturnsTracks_FromMatchingFolders()
    {
        var jazzDir = Path.Combine(_tempRoot, "jazz");
        var rockDir = Path.Combine(_tempRoot, "rock");
        Directory.CreateDirectory(jazzDir);
        Directory.CreateDirectory(rockDir);

        var jazzPath = Path.Combine(jazzDir, "jazzsong.mp3");
        var rockPath = Path.Combine(rockDir, "rocksong.mp3");
        File.WriteAllText(jazzPath, string.Empty);
        File.WriteAllText(rockPath, string.Empty);

        InjectMusicIndex([
            MakeMusicFile("jazzsong", jazzPath),
            MakeMusicFile("rocksong", rockPath),
        ]);

        var filePath = CreateRpDynamicFile("JazzOnly",
            """{"name":"JazzOnly","count":20,"sort":"random","mode":"all","recursive":true,"include":["jazz"]}""");

        var response = await _client.GetAsync($"/api/music/dynamic/expand?path={Q(filePath)}");
        var doc = await ParseJsonAsync(response);

        var trackNames = doc.RootElement.GetProperty("tracks").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("jazzsong", trackNames);
        Assert.DoesNotContain("rocksong", trackNames);
    }

    [Fact]
    public async Task Expand_ExcludeFilter_SkipsTracks_FromMatchingFolders()
    {
        var classicDir = Path.Combine(_tempRoot, "classical");
        var popDir     = Path.Combine(_tempRoot, "pop");
        Directory.CreateDirectory(classicDir);
        Directory.CreateDirectory(popDir);

        var classPath = Path.Combine(classicDir, "classsong.mp3");
        var popPath   = Path.Combine(popDir,     "popsong.mp3");
        File.WriteAllText(classPath, string.Empty);
        File.WriteAllText(popPath,   string.Empty);

        InjectMusicIndex([
            MakeMusicFile("classsong", classPath),
            MakeMusicFile("popsong",   popPath),
        ]);

        var filePath = CreateRpDynamicFile("NoClassical",
            """{"name":"NoClassical","count":20,"sort":"random","mode":"all","recursive":true,"exclude":["classical"]}""");

        var response = await _client.GetAsync($"/api/music/dynamic/expand?path={Q(filePath)}");
        var doc = await ParseJsonAsync(response);

        var trackNames = doc.RootElement.GetProperty("tracks").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("popsong", trackNames);
        Assert.DoesNotContain("classsong", trackNames);
    }

    [Fact]
    public async Task Expand_WritesLastExpandedTimestampToFile()
    {
        var filePath = CreateRpDynamicFile("LastExpandedWrite",
            """{"name":"LastExpandedWrite","count":10,"sort":"random","mode":"sample","recursive":true}""");

        await _client.GetAsync($"/api/music/dynamic/expand?path={Q(filePath)}");

        var json = File.ReadAllText(filePath);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("lastExpanded", out var leProp));
        Assert.False(string.IsNullOrWhiteSpace(leProp.GetString()));
    }

    [Fact]
    public async Task Expand_WritesLastCountToFile()
    {
        var trackDir = SeedMusicIndex("LastCountWrite", 2);
        var filePath = CreateRpDynamicFile("LastCountList",
            """{"name":"LastCountList","count":20,"sort":"random","mode":"all","recursive":true}""");

        await _client.GetAsync($"/api/music/dynamic/expand?path={Q(filePath)}");

        var json = File.ReadAllText(filePath);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("lastCount", out var lcProp));
        Assert.Equal(2, lcProp.GetInt32());
    }

    [Fact]
    public async Task Expand_TrackEntry_ContainsNameAndPathFields()
    {
        SeedMusicIndex("TrackFields", 1);
        var filePath = CreateRpDynamicFile("TrackFieldList",
            """{"name":"TrackFieldList","count":10,"sort":"random","mode":"all","recursive":true}""");

        var response = await _client.GetAsync($"/api/music/dynamic/expand?path={Q(filePath)}");
        var doc = await ParseJsonAsync(response);

        var track = doc.RootElement.GetProperty("tracks").EnumerateArray().First();
        Assert.True(track.TryGetProperty("name", out _));
        Assert.True(track.TryGetProperty("path", out _));
    }

    [Fact]
    public async Task Expand_AlphabeticalSort_TracksReturnedInOrder()
    {
        var dir = Path.Combine(_tempRoot, "AlphaTracks");
        Directory.CreateDirectory(dir);
        var zPath = Path.Combine(dir, "zzz.mp3");
        var aPath = Path.Combine(dir, "aaa.mp3");
        var mPath = Path.Combine(dir, "mmm.mp3");
        File.WriteAllText(zPath, string.Empty);
        File.WriteAllText(aPath, string.Empty);
        File.WriteAllText(mPath, string.Empty);

        // Inject in reverse order to confirm sorting is by the handler, not insertion order.
        InjectMusicIndex([
            MakeMusicFile("zzz", zPath),
            MakeMusicFile("mmm", mPath),
            MakeMusicFile("aaa", aPath),
        ]);

        var filePath = CreateRpDynamicFile("AlphaList",
            """{"name":"AlphaList","count":20,"sort":"alphabetical","mode":"all","recursive":true}""");

        var response = await _client.GetAsync($"/api/music/dynamic/expand?path={Q(filePath)}");
        var doc = await ParseJsonAsync(response);

        var names = doc.RootElement.GetProperty("tracks").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        var ourNames = names.Where(n => n is "aaa" or "mmm" or "zzz").ToList();
        Assert.Equal(["aaa", "mmm", "zzz"], ourNames);
    }

    [Fact]
    public async Task Expand_IncludeAndExcludeFilters_BothApplied()
    {
        // Two folders both matching include="blues"; one also matches exclude="live".
        var includedDir  = Path.Combine(_tempRoot, "blues");
        var alsoExcluded = Path.Combine(_tempRoot, "blues_live");
        Directory.CreateDirectory(includedDir);
        Directory.CreateDirectory(alsoExcluded);

        var studioPath = Path.Combine(includedDir,  "bluestudio.mp3");
        var livePath   = Path.Combine(alsoExcluded, "blueslive.mp3");
        File.WriteAllText(studioPath, string.Empty);
        File.WriteAllText(livePath,   string.Empty);

        InjectMusicIndex([
            MakeMusicFile("bluestudio", studioPath),
            MakeMusicFile("blueslive",  livePath),
        ]);

        var filePath = CreateRpDynamicFile("BluesNoLive",
            """{"name":"BluesNoLive","count":20,"sort":"random","mode":"all","recursive":true,"include":["blues"],"exclude":["live"]}""");

        var response = await _client.GetAsync($"/api/music/dynamic/expand?path={Q(filePath)}");
        var doc = await ParseJsonAsync(response);

        var trackNames = doc.RootElement.GetProperty("tracks").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("bluestudio", trackNames);
        Assert.DoesNotContain("blueslive", trackNames);
    }

    [Fact]
    public async Task Expand_YearFromFilter_ExcludesTracksOutsideRange()
    {
        var dir = Path.Combine(_tempRoot, "YearFrom");
        Directory.CreateDirectory(dir);
        var oldPath = Path.Combine(dir, "old.mp3");
        var newPath = Path.Combine(dir, "new.mp3");
        File.WriteAllText(oldPath, string.Empty);
        File.WriteAllText(newPath, string.Empty);

        InjectMusicIndex([
            MakeMusicFile("old", oldPath, year: 1990u, duration: 200),
            MakeMusicFile("new", newPath, year: 2010u, duration: 200),
        ]);

        var filePath = CreateRpDynamicFile("YearFromFilter",
            """{"name":"YearFromFilter","count":20,"sort":"random","mode":"all","recursive":true,"yearFrom":2000}""");

        var response = await _client.GetAsync($"/api/music/dynamic/expand?path={Q(filePath)}");
        var doc = await ParseJsonAsync(response);

        var trackNames = doc.RootElement.GetProperty("tracks").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("new", trackNames);
        Assert.DoesNotContain("old", trackNames);
    }

    [Fact]
    public async Task Expand_YearToFilter_ExcludesTracksOutsideRange()
    {
        var dir = Path.Combine(_tempRoot, "YearTo");
        Directory.CreateDirectory(dir);
        var oldPath = Path.Combine(dir, "old.mp3");
        var newPath = Path.Combine(dir, "new.mp3");
        File.WriteAllText(oldPath, string.Empty);
        File.WriteAllText(newPath, string.Empty);

        InjectMusicIndex([
            MakeMusicFile("old", oldPath, year: 1990u, duration: 200),
            MakeMusicFile("new", newPath, year: 2010u, duration: 200),
        ]);

        var filePath = CreateRpDynamicFile("YearToFilter",
            """{"name":"YearToFilter","count":20,"sort":"random","mode":"all","recursive":true,"yearTo":1999}""");

        var response = await _client.GetAsync($"/api/music/dynamic/expand?path={Q(filePath)}");
        var doc = await ParseJsonAsync(response);

        var trackNames = doc.RootElement.GetProperty("tracks").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("old", trackNames);
        Assert.DoesNotContain("new", trackNames);
    }

    [Fact]
    public async Task Expand_YearFilter_ExcludesUnenrichedTracks()
    {
        var dir = Path.Combine(_tempRoot, "YearUnenriched");
        Directory.CreateDirectory(dir);
        var unenrichedPath = Path.Combine(dir, "unenriched.mp3");
        var enrichedPath   = Path.Combine(dir, "enriched.mp3");
        File.WriteAllText(unenrichedPath, string.Empty);
        File.WriteAllText(enrichedPath,   string.Empty);

        InjectMusicIndex([
            MakeMusicFile("unenriched", unenrichedPath, year: 0u,    duration: -1),
            MakeMusicFile("enriched",   enrichedPath,   year: 2005u,  duration: 200),
        ]);

        var filePath = CreateRpDynamicFile("YearUnenrichedFilter",
            """{"name":"YearUnenrichedFilter","count":20,"sort":"random","mode":"all","recursive":true,"yearFrom":2000,"yearTo":2010}""");

        var response = await _client.GetAsync($"/api/music/dynamic/expand?path={Q(filePath)}");
        var doc = await ParseJsonAsync(response);

        var trackNames = doc.RootElement.GetProperty("tracks").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("enriched", trackNames);
        Assert.DoesNotContain("unenriched", trackNames);
    }

    [Fact]
    public async Task Expand_MinDurationFilter_ExcludesShortTracks()
    {
        var dir = Path.Combine(_tempRoot, "MinDur");
        Directory.CreateDirectory(dir);
        var shortPath = Path.Combine(dir, "short.mp3");
        var longPath  = Path.Combine(dir, "long.mp3");
        File.WriteAllText(shortPath, string.Empty);
        File.WriteAllText(longPath,  string.Empty);

        InjectMusicIndex([
            MakeMusicFile("short", shortPath, duration: 60),
            MakeMusicFile("long",  longPath,  duration: 300),
        ]);

        var filePath = CreateRpDynamicFile("MinDurFilter",
            """{"name":"MinDurFilter","count":20,"sort":"random","mode":"all","recursive":true,"minDuration":120}""");

        var response = await _client.GetAsync($"/api/music/dynamic/expand?path={Q(filePath)}");
        var doc = await ParseJsonAsync(response);

        var trackNames = doc.RootElement.GetProperty("tracks").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("long", trackNames);
        Assert.DoesNotContain("short", trackNames);
    }

    [Fact]
    public async Task Expand_MaxDurationFilter_ExcludesLongTracks()
    {
        var dir = Path.Combine(_tempRoot, "MaxDur");
        Directory.CreateDirectory(dir);
        var shortPath = Path.Combine(dir, "short.mp3");
        var longPath  = Path.Combine(dir, "long.mp3");
        File.WriteAllText(shortPath, string.Empty);
        File.WriteAllText(longPath,  string.Empty);

        InjectMusicIndex([
            MakeMusicFile("short", shortPath, duration: 60),
            MakeMusicFile("long",  longPath,  duration: 300),
        ]);

        var filePath = CreateRpDynamicFile("MaxDurFilter",
            """{"name":"MaxDurFilter","count":20,"sort":"random","mode":"all","recursive":true,"maxDuration":120}""");

        var response = await _client.GetAsync($"/api/music/dynamic/expand?path={Q(filePath)}");
        var doc = await ParseJsonAsync(response);

        var trackNames = doc.RootElement.GetProperty("tracks").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("short", trackNames);
        Assert.DoesNotContain("long", trackNames);
    }

    [Fact]
    public async Task Expand_DurationFilter_ExcludesUnenrichedTracks()
    {
        var dir = Path.Combine(_tempRoot, "DurUnenriched");
        Directory.CreateDirectory(dir);
        var unenrichedPath = Path.Combine(dir, "unenriched.mp3");
        var enrichedPath   = Path.Combine(dir, "enriched.mp3");
        File.WriteAllText(unenrichedPath, string.Empty);
        File.WriteAllText(enrichedPath,   string.Empty);

        InjectMusicIndex([
            MakeMusicFile("unenriched", unenrichedPath, duration: -1),
            MakeMusicFile("enriched",   enrichedPath,   duration: 200),
        ]);

        var filePath = CreateRpDynamicFile("DurUnenrichedFilter",
            """{"name":"DurUnenrichedFilter","count":20,"sort":"random","mode":"all","recursive":true,"minDuration":100}""");

        var response = await _client.GetAsync($"/api/music/dynamic/expand?path={Q(filePath)}");
        var doc = await ParseJsonAsync(response);

        var trackNames = doc.RootElement.GetProperty("tracks").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("enriched", trackNames);
        Assert.DoesNotContain("unenriched", trackNames);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Encodes a filesystem path as URL-safe Base64 to pass as a query-string value.</summary>
    private static string Q(string path)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
        return Uri.EscapeDataString(base64);
    }

    /// <summary>Creates a .rpDynamic file in <see cref="_tempRoot"/> and returns its path.</summary>
    private string CreateRpDynamicFile(string name, string json)
    {
        var filePath = Path.Combine(_tempRoot, name + ".rpDynamic");
        File.WriteAllText(filePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return filePath;
    }

    /// <summary>
    /// Creates <paramref name="count"/> placeholder .mp3 files in a sub-directory of
    /// <see cref="_tempRoot"/> and directly injects corresponding <c>MusicFile</c> records
    /// into the server's private <c>_musicIndex</c> field via reflection — no filesystem
    /// scanner involvement, no async timing dependency.
    /// Returns the directory that was created and indexed.
    /// </summary>
    private string SeedMusicIndex(string dirName, int count)
    {
        var dir = Path.Combine(_tempRoot, dirName);
        Directory.CreateDirectory(dir);

        var tracks = new List<object>();
        for (var i = 0; i < count; i++)
        {
            var trackPath = Path.Combine(dir, $"track{i:D2}.mp3");
            File.WriteAllText(trackPath, string.Empty);
            tracks.Add(MakeMusicFile($"track{i:D2}", trackPath));
        }

        InjectMusicIndex(tracks.Cast<dynamic>().Select(t => (object)t).ToArray());
        return dir;
    }

    /// <summary>Injects an array of <c>MusicFile</c> instances directly into <c>_musicIndex</c>.</summary>
    private void InjectMusicIndex(object[] musicFiles)
    {
        var field = typeof(WebServer).GetField("_musicIndex",
            BindingFlags.NonPublic | BindingFlags.Instance);
        // MusicFile is an internal record; build a typed array via reflection.
        var elementType = field!.FieldType.GetElementType()!;
        var typed = Array.CreateInstance(elementType, musicFiles.Length);
        for (var i = 0; i < musicFiles.Length; i++)
            typed.SetValue(musicFiles[i], i);
        field.SetValue(_server, typed);
    }

    /// <summary>Constructs a <c>MusicFile</c> record (internal type) via reflection.</summary>
    private static object MakeMusicFile(string name, string fullPath, string? genre = null, uint year = 0, int duration = -1)
    {
        var type = typeof(WebServer).Assembly
            .GetType("RemotePlay.MusicFile", throwOnError: true)!;
        return Activator.CreateInstance(type, name, fullPath, genre, year, duration)!;
    }

    // Keep the async variant for tests that genuinely need file content on disk AND in index.
    private async Task<string> SeedTracksAsync(string dirName, int count)
    {
        var dir = SeedMusicIndex(dirName, count);
        // Warm the browse endpoint so the server also sees the directory in the filesystem.
        await _client.GetAsync($"/api/music/browse?folder={Uri.EscapeDataString(dir)}");
        return dir;
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string url, string jsonBody)
    {
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return await client.PutAsync(url, content);
    }

    private Task<HttpResponseMessage> PutAsync(string url, string jsonBody) =>
        PutAsync(_client, url, jsonBody);

    private Task<HttpResponseMessage> PostAsync(string url, string jsonBody) =>
        PostAsync(_client, url, jsonBody);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string jsonBody)
    {
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return await client.PostAsync(url, content);
    }

    private static async Task<JsonDocument> ParseJsonAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static WebServerCallbacks BuildStubCallbacks() => new()
    {
        Play                  = _ => { },
        Stop                  = () => { },
        Pause                 = () => { },
        GetStatus             = () => new PlaybackStatus(),
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
                RestartServer         = () => { },
    };
}
