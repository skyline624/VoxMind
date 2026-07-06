using System.Linq;
using FluentAssertions;
using VoxMind.Core.Tts;
using Xunit;

namespace VoxMind.Tests.Unit.Tts;

/// <summary>
/// Couvre <see cref="TtsTextSegmenter"/> (extrait de KokoroTtsService, partagé avec Qwen3) : nettoyage du
/// markdown/emojis et découpe en phrases pour le streaming.
/// </summary>
public class TtsTextSegmenterTests
{
    [Fact]
    public void CleanText_StripsMarkdownEmojisAndLinks()
    {
        var input = "# Titre\n**gras** et `code` 😀 [lien](http://exemple.fr)";

        var output = TtsTextSegmenter.CleanText(input);

        output.Should().Contain("Titre").And.Contain("gras").And.Contain("code").And.Contain("lien");
        output.Should().NotContain("#").And.NotContain("**").And.NotContain("`")
                       .And.NotContain("😀").And.NotContain("http");
    }

    [Fact]
    public void CleanText_MarkdownTable_ReadsCellsAsList_NoPipesOrDashes()
    {
        var input = "Voici :\n| Backend | Voix | Clonage |\n|---|---|---|\n| qwen3 | clonée | oui |\nFin.";

        var output = TtsTextSegmenter.CleanText(input);

        // Les cellules sont lues comme une liste ; plus aucun « | » ni « --- » (séparateur retiré).
        output.Should().NotContain("|").And.NotContain("---");
        output.Should().Contain("Backend, Voix, Clonage");
        output.Should().Contain("qwen3, clonée, oui");
        output.Should().Contain("Fin.");
    }

    [Fact]
    public void SplitSentences_SplitsOnSentenceEnders()
    {
        var parts = TtsTextSegmenter.SplitSentences("Un. Deux! Trois?").ToList();

        parts.Should().HaveCount(3);
        parts[0].Should().Be("Un.");
        parts[1].Should().Be("Deux!");
        parts[2].Should().Be("Trois?");
    }

    [Fact]
    public void SplitSentences_LongRunOnWithoutEnder_BreaksWithinMaxChars()
    {
        var runOn = string.Join(", ", Enumerable.Range(0, 60).Select(i => "segment" + i)) + ".";

        var parts = TtsTextSegmenter.SplitSentences(runOn, maxChars: 40).ToList();

        parts.Should().NotBeEmpty();
        parts.Should().OnlyContain(p => p.Length <= 40);
    }

    [Fact]
    public void SplitSentences_EmptyAfterCleaning_YieldsNothing()
    {
        var parts = TtsTextSegmenter.SplitSentences("   ").ToList();

        parts.Should().BeEmpty();
    }
}
