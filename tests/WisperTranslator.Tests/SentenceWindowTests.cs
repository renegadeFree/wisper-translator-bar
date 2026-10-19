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
    public void LeRigheRispettanoIlMassimoEChiudonoSoloAlMinimo()
    {
        var text = string.Join(' ', Enumerable.Range(1, 20).Select(i => $"parola{i}"));
        var (rows, leftover) = SentenceWindow.Split(text, minWords: 8, maxWords: 15);

        Assert.Single(rows);
        Assert.Equal(15, SentenceWindow.CountWords(rows[0]));
        Assert.Equal("parola16 parola17 parola18 parola19 parola20", leftover);
    }

    [Fact]
    public void SottoIlMinimoLaRigaRestaAperta()
    {
        var (rows, leftover) = SentenceWindow.Split("una frase molto corta", minWords: 8, maxWords: 15);

        Assert.Empty(rows);
        Assert.Equal("una frase molto corta", leftover);
    }

    [Fact]
    public void SopraIlMinimoLaRigaSiChiude()
    {
        var text = string.Join(' ', Enumerable.Range(1, 10).Select(i => $"parola{i}"));
        var (rows, leftover) = SentenceWindow.Split(text, minWords: 8, maxWords: 15);

        Assert.Single(rows);
        Assert.Equal(string.Empty, leftover);
    }

    [Fact]
    public void UnaFraseLungaSiSpezzaInPiuRighe()
    {
        var text = string.Join(' ', Enumerable.Range(1, 31).Select(i => $"parola{i}"));
        var (rows, leftover) = SentenceWindow.Split(text, minWords: 8, maxWords: 15);

        Assert.Equal(2, rows.Count);
        Assert.Equal(15, SentenceWindow.CountWords(rows[0]));
        Assert.Equal(15, SentenceWindow.CountWords(rows[1]));
        Assert.Equal("parola31", leftover);
    }

    [Fact]
    public void IlMassimoNonPuoScendereSottoIlMinimo()
    {
        var (rows, leftover) = SentenceWindow.Split("uno due tre", minWords: 10, maxWords: 4);

        Assert.Empty(rows);
        Assert.Equal("uno due tre", leftover);
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
