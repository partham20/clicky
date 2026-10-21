using Clicky.Core.Dictation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Dictation;

public sealed class PersonalVocabularyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clicky-tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_root, "dictionary.json");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_missing_file_is_an_empty_dictionary()
    {
        var vocabulary = Open();
        Assert.Empty(vocabulary.Entries);
        Assert.Equal(string.Empty, vocabulary.BuildPrompt());
        Assert.Equal("unchanged", vocabulary.Apply("unchanged"));
    }

    [Fact]
    public void Mishearings_are_replaced_as_whole_words_ignoring_case()
    {
        var vocabulary = Open();
        Assert.True(vocabulary.Save([new DictionaryEntry("Partha", ["Parta", "Partho"]), new DictionaryEntry("Clicky", ["clicky"])]));

        Assert.Equal("Ask Partha about Clicky.", vocabulary.Apply("Ask parta about clicky."));
        Assert.Equal("Partha's laptop", vocabulary.Apply("Partho's laptop"));

        // Not inside other words.
        Assert.Equal("partake", vocabulary.Apply("partake"));
    }

    [Fact]
    public void Distinctive_casing_is_enforced_without_listing_variants()
    {
        var vocabulary = Open();
        vocabulary.Save([new DictionaryEntry("GitHub"), new DictionaryEntry("WASAPI"), new DictionaryEntry("Will")]);

        Assert.Equal("Push to GitHub over WASAPI", vocabulary.Apply("Push to github over wasapi"));

        // An ordinary capitalised word is not forced on every sentence.
        Assert.Equal("I will go", vocabulary.Apply("I will go"));
    }

    [Fact]
    public void Multi_word_mishearings_match_across_spaces_and_hyphens()
    {
        var vocabulary = Open();
        vocabulary.Save([new DictionaryEntry("Kubernetes", ["cooper netties"])]);

        Assert.Equal("Deploy to Kubernetes now", vocabulary.Apply("Deploy to Cooper-Netties now"));
    }

    [Fact]
    public void The_prompt_lists_phrases_newest_first_within_whispers_limit()
    {
        var vocabulary = Open();
        vocabulary.Save(Enumerable.Range(0, 500).Select(i => new DictionaryEntry($"Word{i:000}")));

        var prompt = vocabulary.BuildPrompt();

        Assert.StartsWith("Word499, Word498", prompt, StringComparison.Ordinal);
        Assert.EndsWith(".", prompt, StringComparison.Ordinal);
        Assert.True(prompt.Length <= PersonalVocabulary.MaxPromptChars + 1);
    }

    [Fact]
    public void Learning_adds_a_word_or_a_sounds_like_and_survives_a_reload()
    {
        var vocabulary = Open();

        Assert.True(vocabulary.Learn("Parta", "Partha"));
        Assert.False(vocabulary.Learn("parta", "Partha"));
        Assert.True(vocabulary.Learn("Partho", "Partha"));

        var reloaded = Open();
        var entry = Assert.Single(reloaded.Entries);
        Assert.Equal("Partha", entry.Phrase);
        Assert.Equal(["Parta", "Partho"], entry.SoundsLike);
        Assert.True(entry.Learned);
    }

    [Fact]
    public void Hand_edits_to_the_file_are_picked_up()
    {
        var vocabulary = Open();
        vocabulary.Save([new DictionaryEntry("One")]);
        Assert.Single(vocabulary.Entries);

        File.WriteAllText(FilePath, """
            {
              // comments are fine
              "words": [ { "phrase": "Vulkan", "soundsLike": ["volcan"] }, ],
            }
            """);
        File.SetLastWriteTimeUtc(FilePath, DateTime.UtcNow.AddSeconds(5));

        Assert.Equal("Vulkan", Assert.Single(vocabulary.Entries).Phrase);
        Assert.Equal("Vulkan GPU", vocabulary.Apply("volcan GPU"));
    }

    [Fact]
    public void A_broken_file_is_never_overwritten_by_learning()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(FilePath, "{ not json");
        var vocabulary = Open();

        Assert.Empty(vocabulary.Entries);
        Assert.False(vocabulary.Learn("Parta", "Partha"));
        Assert.Equal("{ not json", File.ReadAllText(FilePath));
    }

    private PersonalVocabulary Open() => new(FilePath, NullLogger.Instance);
}
