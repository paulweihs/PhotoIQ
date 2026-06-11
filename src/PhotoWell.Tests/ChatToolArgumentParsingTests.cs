using System.Text.Json;
using PhotoWell.Services.Chat;
using Xunit;

namespace PhotoWell.Tests;

/// <summary>
/// Small local models frequently emit tool arguments with wrong JSON types
/// (booleans as strings, numbers as strings). The parsers must be lenient —
/// a thrown exception here surfaces to the model as a tool failure and derails
/// the conversation (observed live: confirmed_only sent as "true").
/// </summary>
public class ChatToolArgumentParsingTests
{
    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement;

    // ── GetBool ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"confirmed_only": true}""",    true)]
    [InlineData("""{"confirmed_only": false}""",   false)]
    [InlineData("""{"confirmed_only": "true"}""",  true)]   // string boolean — the live failure
    [InlineData("""{"confirmed_only": "True"}""",  true)]
    [InlineData("""{"confirmed_only": "false"}""", false)]
    [InlineData("""{"confirmed_only": 1}""",       true)]
    [InlineData("""{"confirmed_only": 0}""",       false)]
    public void GetBool_accepts_mistyped_values(string json, bool expected)
        => Assert.Equal(expected, ChatAssistantService.GetBool(Args(json), "confirmed_only", defaultValue: !expected));

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"confirmed_only": null}""")]
    [InlineData("""{"confirmed_only": "maybe"}""")]
    [InlineData("""{"confirmed_only": []}""")]
    public void GetBool_falls_back_to_default_when_unparseable(string json)
    {
        Assert.True(ChatAssistantService.GetBool(Args(json), "confirmed_only", defaultValue: true));
        Assert.False(ChatAssistantService.GetBool(Args(json), "confirmed_only", defaultValue: false));
    }

    [Fact]
    public void GetBool_does_not_throw_on_non_object_args()
        => Assert.True(ChatAssistantService.GetBool(Args("\"not an object\""), "key", defaultValue: true));

    // ── GetString / GetStringOpt ──────────────────────────────────────────────

    [Fact]
    public void GetString_reads_plain_string()
        => Assert.Equal("Jacob", ChatAssistantService.GetString(Args("""{"name":"Jacob"}"""), "name"));

    [Theory]
    [InlineData("""{"name": 42}""",    "42")]
    [InlineData("""{"name": true}""",  "True")]
    public void GetString_stringifies_mistyped_values(string json, string expected)
        => Assert.Equal(expected, ChatAssistantService.GetString(Args(json), "name"));

    [Fact]
    public void GetString_returns_empty_when_missing()
        => Assert.Equal("", ChatAssistantService.GetString(Args("{}"), "name"));

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"person_name": null}""")]
    public void GetStringOpt_returns_null_when_missing_or_null(string json)
        => Assert.Null(ChatAssistantService.GetStringOpt(Args(json), "person_name"));

    [Fact]
    public void GetStringOpt_does_not_throw_on_non_object_args()
        => Assert.Null(ChatAssistantService.GetStringOpt(Args("[1,2]"), "person_name"));
}
