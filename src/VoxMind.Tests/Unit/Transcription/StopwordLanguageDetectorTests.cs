using VoxMind.Core.Transcription;
using Xunit;

namespace VoxMind.Tests.Unit.Transcription;

public class StopwordLanguageDetectorTests
{
    private readonly StopwordLanguageDetector _detector = new();

    [Theory]
    [InlineData("Bonjour, je suis ravi de vous rencontrer aujourd'hui.", "fr")]
    [InlineData("La transcription vocale fonctionne très bien avec ce modèle.", "fr")]
    [InlineData("Hello, I am happy to meet you today and it is a pleasure.", "en")]
    [InlineData("The transcription model works very well with this audio.", "en")]
    [InlineData("Guten Tag, das ist ein Test für die Sprache und das Modell.", "de")]
    [InlineData("Hola, ¿cómo estás hoy? Me alegro de verte aquí.", "es")]
    [InlineData("Buongiorno, come stai oggi? È un piacere essere qui con voi.", "it")]
    [InlineData("Olá, como você está hoje? É um prazer estar aqui.", "pt")]
    public void Detect_OnUnambiguousSentence_ReturnsExpectedCode(string text, string expected)
    {
        var result = _detector.DetectLanguage(text);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ok")]
    [InlineData("oui non")]
    public void Detect_OnEmptyOrTooShort_ReturnsUnd(string text)
    {
        var result = _detector.DetectLanguage(text);
        Assert.Equal("und", result);
    }

    [Fact]
    public void Detect_OnFrenchTextRestrictedToFrEn_ReturnsFr()
    {
        var result = _detector.DetectLanguage(
            "Le module de synthèse vocale est presque terminé.",
            new[] { "fr", "en" });
        Assert.Equal("fr", result);
    }

    [Fact]
    public void Detect_OnEnglishTextRestrictedToFrEn_ReturnsEn()
    {
        var result = _detector.DetectLanguage(
            "The voice synthesis module is almost done.",
            new[] { "fr", "en" });
        Assert.Equal("en", result);
    }

    [Fact]
    public void Detect_OnGarbageText_ReturnsUnd()
    {
        var result = _detector.DetectLanguage("zxc qwerty bvcx mnbv asdf qwer tyui");
        Assert.Equal("und", result);
    }
}
