using System.IO;
using System.Reflection;
using Xunit;
using RemotePlay.Services;

namespace RemotePlay.Tests;

public class UpdateFileHelperTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public UpdateFileHelperTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // -- FilesAreIdentical ----------------------------------------------------

    [Fact]
    public void FilesAreIdentical_SameContent_ReturnsTrue()
    {
        var a = Path.Combine(_tempDir, "a.bin");
        var b = Path.Combine(_tempDir, "b.bin");
        File.WriteAllBytes(a, new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(b, new byte[] { 1, 2, 3, 4 });

        Assert.True(UpdateFileHelper.FilesAreIdentical(a, b));
    }

    [Fact]
    public void FilesAreIdentical_DifferentContent_SameSize_ReturnsFalse()
    {
        var a = Path.Combine(_tempDir, "a.bin");
        var b = Path.Combine(_tempDir, "b.bin");
        File.WriteAllBytes(a, new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(b, new byte[] { 1, 2, 3, 5 });

        Assert.False(UpdateFileHelper.FilesAreIdentical(a, b));
    }

    [Fact]
    public void FilesAreIdentical_DifferentSize_ReturnsFalse()
    {
        var a = Path.Combine(_tempDir, "a.bin");
        var b = Path.Combine(_tempDir, "b.bin");
        File.WriteAllBytes(a, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(b, new byte[] { 1, 2, 3, 4 });

        Assert.False(UpdateFileHelper.FilesAreIdentical(a, b));
    }

    [Fact]
    public void FilesAreIdentical_MissingFile_ReturnsFalse()
    {
        var a = Path.Combine(_tempDir, "a.bin");
        File.WriteAllBytes(a, new byte[] { 1, 2, 3 });

        Assert.False(UpdateFileHelper.FilesAreIdentical(a, Path.Combine(_tempDir, "missing.bin")));
    }

    // -- ReadVersionFile ------------------------------------------------------

    [Fact]
    public void ReadVersionFile_WithVersionTxt_ReturnsTrimedVersion()
    {
        File.WriteAllText(Path.Combine(_tempDir, "version.txt"), "  2.1.0  \n");

        Assert.Equal("2.1.0", UpdateFileHelper.ReadVersionFile(_tempDir));
    }

    [Fact]
    public void ReadVersionFile_NoFile_ReturnsNull()
    {
        Assert.Null(UpdateFileHelper.ReadVersionFile(_tempDir));
    }

    [Fact]
    public void ReadVersionFile_EmptyFile_ReturnsNull()
    {
        File.WriteAllText(Path.Combine(_tempDir, "version.txt"), "   ");

        Assert.Null(UpdateFileHelper.ReadVersionFile(_tempDir));
    }

    // -- GetAssemblyVersion ---------------------------------------------------

    [Fact]
    public void GetAssemblyVersion_ReturnsNonEmptyString()
    {
        var version = UpdateFileHelper.GetAssemblyVersion();

        Assert.NotEmpty(version);
        // Format is Major.Minor.Build or fallback "1.0.0"
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
    }

    // -- CollectChangedFiles --------------------------------------------------

    [Fact]
    public void CollectChangedFiles_NewFile_IsIncluded()
    {
        var src = Path.Combine(_tempDir, "source");
        var dst = Path.Combine(_tempDir, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(src, "new.dll"), "new");

        var changed = UpdateFileHelper.CollectChangedFiles(src, dst);

        Assert.Single(changed);
        Assert.EndsWith("new.dll", changed[0].Source);
    }

    [Fact]
    public void CollectChangedFiles_IdenticalFile_IsExcluded()
    {
        var src = Path.Combine(_tempDir, "source");
        var dst = Path.Combine(_tempDir, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        var content = new byte[] { 10, 20, 30 };
        File.WriteAllBytes(Path.Combine(src, "same.dll"), content);
        File.WriteAllBytes(Path.Combine(dst, "same.dll"), content);

        var changed = UpdateFileHelper.CollectChangedFiles(src, dst);

        Assert.Empty(changed);
    }

    [Fact]
    public void CollectChangedFiles_ModifiedFile_IsIncluded()
    {
        var src = Path.Combine(_tempDir, "source");
        var dst = Path.Combine(_tempDir, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(src, "app.exe"), "new content");
        File.WriteAllText(Path.Combine(dst, "app.exe"), "old content");

        var changed = UpdateFileHelper.CollectChangedFiles(src, dst);

        Assert.Single(changed);
    }
}
