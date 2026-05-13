using System;
using System.Reflection;
using Xunit;

namespace PhotoWell.Tests;

/// <summary>
/// Tests for App.ParseArgs(string[] args) — public static, no WPF/STA needed.
/// Uses reflection so the test assembly compiles against whatever Desktop.dll is present.
/// </summary>
public class AppIpcExtensionParseArgsTests
{
    private static (string? command, string? path) InvokeParseArgs(string[] args)
    {
        var method = typeof(PhotoWell.Desktop.App).GetMethod(
            "ParseArgs",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string[])],
            modifiers: null)
            ?? throw new InvalidOperationException("ParseArgs not found in App.");

        var raw  = method.Invoke(null, new object[] { args })!;
        var type = raw.GetType();
        var cmd  = (string?)type.GetField("Item1")!.GetValue(raw);
        var path = (string?)type.GetField("Item2")!.GetValue(raw);
        return (cmd, path);
    }

    // ── Supported single-file flags ───────────────────────────────────────────

    [Fact] public void Select_Flag()
    {
        var (cmd, path) = InvokeParseArgs(["--select", @"C:\photos\img.jpg"]);
        Assert.Equal("select", cmd);
        Assert.Equal(@"C:\photos\img.jpg", path);
    }

    [Fact] public void Describe_Flag()
    {
        var (cmd, path) = InvokeParseArgs(["--describe", @"C:\photos\img.jpg"]);
        Assert.Equal("describe", cmd);
        Assert.Equal(@"C:\photos\img.jpg", path);
    }

    [Fact] public void Similar_Flag()
    {
        var (cmd, path) = InvokeParseArgs(["--similar", @"C:\photos\img.jpg"]);
        Assert.Equal("similar", cmd);
        Assert.Equal(@"C:\photos\img.jpg", path);
    }

    [Fact] public void Related_Flag()
    {
        var (cmd, path) = InvokeParseArgs(["--related", @"C:\photos\img.jpg"]);
        Assert.Equal("related", cmd);
        Assert.Equal(@"C:\photos\img.jpg", path);
    }

    [Fact] public void Tag_Flag()
    {
        var (cmd, path) = InvokeParseArgs(["--tag", @"C:\photos\img.jpg"]);
        Assert.Equal("tag", cmd);
        Assert.Equal(@"C:\photos\img.jpg", path);
    }

    // ── Folder flags ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("--import-folder",           "import-folder")]
    [InlineData("--import-folder-recursive", "import-folder-recursive")]
    [InlineData("--remove-folder",           "remove-folder")]
    [InlineData("--remove-folder-recursive", "remove-folder-recursive")]
    public void Folder_Flags_AreRecognised(string flag, string expectedCmd)
    {
        var (cmd, path) = InvokeParseArgs([flag, @"C:\photos"]);
        Assert.Equal(expectedCmd, cmd);
        Assert.Equal(@"C:\photos", path);
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact] public void EmptyArgs_ReturnsNullNull()
    {
        var (cmd, path) = InvokeParseArgs([]);
        Assert.Null(cmd);
        Assert.Null(path);
    }

    [Fact] public void UnknownFlag_ReturnsNullNull()
    {
        var (cmd, path) = InvokeParseArgs(["--unknown", "path"]);
        Assert.Null(cmd);
        Assert.Null(path);
    }

    [Fact] public void FlagWithNoFollowingPath_ReturnsNullNull()
    {
        var (cmd, path) = InvokeParseArgs(["--select"]);
        Assert.Null(cmd);
        Assert.Null(path);
    }

    [Fact] public void Uppercase_Flag_IsCaseInsensitive()
    {
        var (cmd, path) = InvokeParseArgs(["--SELECT", "path"]);
        Assert.Equal("select", cmd);
        Assert.Equal("path", path);
    }

    [Fact] public void MixedCase_Flag_IsCaseInsensitive()
    {
        var (cmd, path) = InvokeParseArgs(["--DESCRIBE", @"C:\photos\img.jpg"]);
        Assert.Equal("describe", cmd);
        Assert.Equal(@"C:\photos\img.jpg", path);
    }

    [Fact] public void MultipleFlags_FirstMatchReturned()
    {
        var (cmd, path) = InvokeParseArgs(["--select", @"C:\a.jpg", "--describe", @"C:\b.jpg"]);
        Assert.Equal("select", cmd);
        Assert.Equal(@"C:\a.jpg", path);
    }
}
