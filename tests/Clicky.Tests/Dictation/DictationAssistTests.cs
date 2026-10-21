using System.Text.Json;
using Clicky.Core.Dictation;
using Clicky.Core.Settings;

namespace Clicky.Tests.Dictation;

public sealed class DictationAssistTests
{
    [Theory]
    [InlineData("so i went to the store and they was closed", "So I went to the store, and they were closed.")]
    [InlineData("um can you send me the uh report", "Can you send me the report?")]
    [InlineData("first line\nsecond line", "First line.\nSecond line.")]
    public void Cleanup_that_only_fixes_punctuation_and_slips_is_kept(string original, string polished) =>
        Assert.True(ClaudeDictationPolisher.KeepsWords(original, polished));

    [Theory]
    [InlineData("send me the report", "Could you please send me the quarterly report?")]
    [InlineData("send me the report today", "Send the report.")]
    [InlineData("what time is the meeting", "The meeting is at 3 pm.")]
    [InlineData("first line\nsecond line", "First line. Second line.")]
    [InlineData("send me the report", "")]
    public void Cleanup_that_adds_drops_rewrites_or_answers_is_thrown_away(string original, string polished) =>
        Assert.False(ClaudeDictationPolisher.KeepsWords(original, polished));

    [Theory]
    [InlineData("Type a reply to this.")]
    [InlineData("type a reply to this saying I'll be there at five")]
    [InlineData("Draft a response to this email, politely declining.")]
    [InlineData("Write an answer to this.")]
    [InlineData("Please type a quick reply saying thanks.")]
    [InlineData("Reply to this saying yes.")]
    [InlineData("Okay, write a reply.")]
    public void Drafting_requests_are_recognised(string dictated) =>
        Assert.Equal(dictated.Trim(), ScreenDrafter.FindRequest(dictated));

    [Theory]
    [InlineData("I'll type a reply later.")]
    [InlineData("The reply to this is no.")]
    [InlineData("Typewriters are great.")]
    [InlineData("Write the code first.")]
    public void Ordinary_dictation_is_not_a_drafting_request(string dictated) =>
        Assert.Null(ScreenDrafter.FindRequest(dictated));

    [Fact]
    public void Drafts_come_out_as_plain_text()
    {
        Assert.Equal("Sounds good, see you at 5.", ScreenDrafter.Clean("\"Sounds good — see you at 5.\""));
        Assert.Equal("I can do Friday.", ScreenDrafter.Clean("I can do **Friday**."));
    }

    [Fact]
    public void The_draft_prompt_carries_the_request_and_the_app()
    {
        var question = ScreenDrafter.BuildQuestion("Type a reply to this saying yes.", "slack.exe", hasScreenshot: true);
        Assert.Contains("slack.exe", question, StringComparison.Ordinal);
        Assert.Contains("\"Type a reply to this saying yes.\"", question, StringComparison.Ordinal);
        Assert.Contains("screenshot", question, StringComparison.Ordinal);
    }

    [Fact]
    public void Dictation_settings_default_when_missing_and_are_clamped()
    {
        var settings = JsonSerializer.Deserialize("""{ "dictation": { "maxMinutes": 999, "keepHistoryDays": -3 } }""", SettingsJsonContext.Default.ClickySettings)!.Normalize();

        Assert.Equal(DictationSettings.MaxMaxMinutes, settings.Dictation.MaxMinutes);
        Assert.Equal(0, settings.Dictation.KeepHistoryDays);
        Assert.True(settings.Dictation.TypeWhileSpeaking);
        Assert.True(settings.Dictation.LearnCorrections);
        Assert.Equal(DictationCleanup.Auto, settings.Dictation.Cleanup);
        Assert.Empty(settings.Dictation.TerminalApps);

        var none = JsonSerializer.Deserialize("{}", SettingsJsonContext.Default.ClickySettings)!.Normalize();
        Assert.Equal(new DictationSettings(), none.Dictation);
    }

    [Fact]
    public void Dictation_settings_compare_lists_by_content()
    {
        Assert.Equal(new DictationSettings(TerminalApps: ["a.exe"]), new DictationSettings(TerminalApps: ["a.exe"]));
        Assert.NotEqual(new DictationSettings(PasteApps: ["a.exe"]), new DictationSettings(PasteApps: ["b.exe"]));
    }
}
