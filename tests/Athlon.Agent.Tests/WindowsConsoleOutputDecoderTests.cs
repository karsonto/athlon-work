using System.Text;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class WindowsConsoleOutputDecoderTests
{
    [Fact]
    public void DecodeLine_StrictUtf8_ReturnsChineseText()
    {
        var bytes = Encoding.UTF8.GetBytes("你好Athlon");

        var decoded = WindowsConsoleOutputDecoder.DecodeLine(bytes);

        Assert.Equal("你好Athlon", decoded);
    }

    [Fact]
    public void DecodeLine_GbkCmdError_FallsBackToSystemAnsi()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding(936);
        var bytes = gbk.GetBytes("'head' 不是内部或外部命令，也不是可运行的程序或批处理文件。");

        var decoded = WindowsConsoleOutputDecoder.DecodeLine(bytes);

        Assert.Contains("不是内部或外部命令", decoded, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFFFD", decoded);
    }

    [Fact]
    public void DecodeLine_InvalidUtf8WithReplacement_FallsBackToSystemAnsi()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding(936);
        var bytes = gbk.GetBytes("错误输出");

        var decoded = WindowsConsoleOutputDecoder.DecodeLine(bytes);

        Assert.Equal("错误输出", decoded);
    }

    [Theory]
    [InlineData("完成")]
    [InlineData("成功")]
    [InlineData("你好世界")]
    [InlineData("ok 完成")]
    [InlineData("用户名：admin")]
    public void DecodeLine_Utf8Chinese_StaysUtf8WhenGbkDecodeIsLonger(string text)
    {
        var gbk = Gbk();
        var decoded = WindowsConsoleOutputDecoder.DecodeLine(Encoding.UTF8.GetBytes(text), gbk);

        Assert.Equal(text, decoded);
    }

    [Fact]
    public void DecodeLine_GbkCmdError_WithExplicit936_ReturnsChinese()
    {
        var gbk = Gbk();
        var text = "'head' 不是内部或外部命令，也不是可运行的程序或批处理文件。";

        var decoded = WindowsConsoleOutputDecoder.DecodeLine(gbk.GetBytes(text), gbk);

        Assert.Equal(text, decoded);
    }

    [Fact]
    public void DecodeLine_GbkCharacterThatIsAlsoValidUtf8_StaysGbk()
    {
        var gbk = Gbk();

        var decoded = WindowsConsoleOutputDecoder.DecodeLine(gbk.GetBytes("山"), gbk);

        Assert.Equal("山", decoded);
    }

    private static Encoding Gbk()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936);
    }
}
