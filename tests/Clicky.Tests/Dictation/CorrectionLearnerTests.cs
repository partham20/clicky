using Clicky.Core.Dictation;

namespace Clicky.Tests.Dictation;

public sealed class CorrectionLearnerTests
{
    private const string Prefix = "Notes: ";

    [Theory]
    [InlineData("Ask Parta about it.", "Ask Partha about it.", "Parta", "Partha")]
    [InlineData("We use clicky daily.", "We use Clicky daily.", "clicky", "Clicky")]
    [InlineData("Push it to git hub.", "Push it to GitHub.", "git hub", "GitHub")]
    [InlineData("Thanks, clawed.", "Thanks, Claude.", "clawed", "Claude")]
    [InlineData("Runs on volcan.", "Runs on Vulkan.", "volcan", "Vulkan")]
    public void Small_fixes_inside_the_inserted_text_are_learned(string inserted, string edited, string heard, string written)
    {
        var correction = Find(inserted, edited);

        Assert.NotNull(correction);
        Assert.Equal(heard, correction.Heard);
        Assert.Equal(written, correction.Written);
    }

    [Theory]
    [InlineData("Ask Parta about it.", "Ask Parta about it.")]
    [InlineData("Ask Parta about it.", "Ask about it.")]
    [InlineData("Ask Parta about it.", "Ask Bob about it.")]
    [InlineData("Ask Parta about it.", "Ask Parta about it. And more typed after.")]
    [InlineData("The quick brown fox jumps.", "A slow red dog sleeps.")]
    [InlineData("Ask Parta about it.", "Ask Parta about it!")]
    [InlineData("I like it.", "I like it a lot.")]
    public void Rewrites_deletions_new_typing_and_punctuation_are_not_learned(string inserted, string edited) =>
        Assert.Null(Find(inserted, edited));

    [Fact]
    public void Edits_outside_the_inserted_text_are_ignored()
    {
        var before = Prefix + "Ask Parta.";
        var after = "Notez: Ask Parta.";
        Assert.Null(CorrectionLearner.Find(before, Prefix.Length, "Ask Parta.".Length, after));
    }

    [Fact]
    public void Capitalising_a_name_is_learned_but_lowercasing_one_is_not()
    {
        Assert.NotNull(Find("we met dave today.", "we met Dave today."));
        Assert.Null(Find("We met Dave today.", "We met dave today."));
    }

    private static Correction? Find(string inserted, string edited) =>
        CorrectionLearner.Find(Prefix + inserted, Prefix.Length, inserted.Length, Prefix + edited);
}
