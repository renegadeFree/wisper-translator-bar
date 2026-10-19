using WisperTranslator.Core.Vad;

namespace WisperTranslator.Tests;

/// <summary>
/// Il range di parole agisce sulla segmentazione: il silenzio non deve chiudere una frase corta
/// e il massimo deve chiuderla. Sono le due regole che evitano di incollare due trascrizioni.
/// </summary>
public sealed class SegmentationRangeTests
{
    private static EndpointDetector Detector() =>
        new(new EndpointOptions { SampleRate = 16000, FrameSamples = 512 });

    private static void Speak(EndpointDetector detector)
    {
        for (var i = 0; i < 12 && !detector.InSpeech; i++)
        {
            detector.Process(0.9);
        }

        Assert.True(detector.InSpeech);
    }

    [Fact]
    public void IlSilenzioChiudeLaFraseQuandoIlMinimoNonEAttivo()
    {
        var detector = Detector();
        Speak(detector);

        var signal = EndpointSignal.None;
        for (var i = 0; i < 30 && signal == EndpointSignal.None; i++)
        {
            signal = detector.Process(0.0);
        }

        Assert.Equal(EndpointSignal.SpeechEnd, signal);
        Assert.False(detector.InSpeech);
    }

    [Fact]
    public void ConIlMinimoAttivoIlSilenzioNonChiudeLaFrase()
    {
        var detector = Detector();
        Speak(detector);
        detector.HoldSilence = true;

        // Molto più del silenzio minimo: l'enunciato resta aperto.
        for (var i = 0; i < 60; i++)
        {
            Assert.Equal(EndpointSignal.None, detector.Process(0.0));
        }

        Assert.True(detector.InSpeech);

        // Raggiunto il minimo, il silenzio chiude normalmente.
        detector.HoldSilence = false;
        var signal = EndpointSignal.None;
        for (var i = 0; i < 30 && signal == EndpointSignal.None; i++)
        {
            signal = detector.Process(0.0);
        }

        Assert.Equal(EndpointSignal.SpeechEnd, signal);
    }

    [Fact]
    public void IlMassimoChiudeLaFraseAncheMentreSiParla()
    {
        var detector = Detector();
        Speak(detector);
        detector.HoldSilence = true;
        detector.RequestClose();

        Assert.Equal(EndpointSignal.SpeechEnd, detector.Process(0.9));
        Assert.False(detector.InSpeech);
    }

    [Fact]
    public void LaDurataMassimaChiudeLaFraseAncheConIlMinimoAttivo()
    {
        var detector = Detector();
        Speak(detector);
        detector.HoldSilence = true;

        var signal = EndpointSignal.None;
        for (var i = 0; i < 400 && signal == EndpointSignal.None; i++)
        {
            signal = detector.Process(0.9);
        }

        Assert.Equal(EndpointSignal.SpeechEnd, signal);
    }
}
