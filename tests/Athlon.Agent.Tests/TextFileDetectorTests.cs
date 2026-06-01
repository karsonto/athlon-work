using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class TextFileDetectorTests
{
    [Theory]
    [InlineData("Program.cs", true)]
    [InlineData("readme.md", true)]
    [InlineData("Dockerfile", true)]
    [InlineData("image.png", false)]
    [InlineData("archive.zip", false)]
    public void IsTextFile_UsesExtensionAndNameRules(string fileName, bool expected) =>
        Assert.Equal(expected, TextFileDetector.IsTextFile(Path.Combine("workspace", fileName)));

    [Fact]
    public void ContainsBinaryContent_DetectsNulByte() =>
        Assert.True(TextFileDetector.ContainsBinaryContent(new byte[] { 0x48, 0x00, 0x69 }));

    [Fact]
    public async Task LooksBinaryOnDiskAsync_DetectsBinaryFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"athlon-bin-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 0, 3 });
        try
        {
            Assert.True(await TextFileDetector.LooksBinaryOnDiskAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
