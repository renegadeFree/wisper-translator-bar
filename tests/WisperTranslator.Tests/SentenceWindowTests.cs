using WisperTranslator.Core.Session;
using WisperTranslator.Core.Settings;

namespace WisperTranslator.Tests;

/// <summary>
/// Impaginazione delle righe e unione delle due frasi per la passata di continuità: sono regole
/// pure, quindi si verificano senza audio, modelli né finestre.
/// </summary>
public sealed class SentenceWindowTests
{
    [Fact]
    public void DueFrasiDiventanoUnaSola()
    {
        Assert.Equal(
            "Il gatto sul tavolo e la penna sul tavolo.",
            SentenceWindow.Join("Il gatto sul tavolo.", "e la penna sul tavolo."));
    }

    [Fact]
    public void LUnioneToglieSoloLaPunteggiaturaFinale()
    {
        Assert.Equal("Ho visto: Marco e Luisa", SentenceWindow.Join("Ho visto: Marco", "e Luisa"));
        Assert.Equal("Ciao", SentenceWindow.Join("Ciao", "  "));
        Assert.Equal("Ciao", SentenceWindow.Join("   ", "Ciao"));
    }

    [Fact]
    public void GliSpaziMultipliNonContanoComeParole()
    {
        Assert.Equal(3, SentenceWindow.CountWords("  una   frase   corta "));
    }

    [Fact]
    public void LeImpostazioniArrivanoAllaSessione()
    {
        var settings = new AppSettings
        {
            ContinuousTranslation = true,
            SentenceMinWords = 8,
            SentenceMaxWords = 15,
        };

        var options = settings.ToSessionOptions();

        Assert.True(options.ContinuousTranslation);
        Assert.Equal(8, options.SentenceMinWords);
        Assert.Equal(15, options.SentenceMaxWords);
    }

    [Fact]
    public void IlRangeDiParoleVieneCorrettoAlCaricamento()
    {
        var settings = new AppSettings { SentenceMinWords = 30, SentenceMaxWords = 10 };
        settings.NormalizeValues();

        Assert.Equal(30, settings.SentenceMinWords);
        Assert.Equal(30, settings.SentenceMaxWords);

        var outOfRange = new AppSettings { SentenceMinWords = -4, SentenceMaxWords = 999 };
        outOfRange.NormalizeValues();

        Assert.Equal(0, outOfRange.SentenceMinWords);
        Assert.Equal(60, outOfRange.SentenceMaxWords);
    }
}
