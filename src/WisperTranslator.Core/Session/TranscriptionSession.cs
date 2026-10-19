using WisperTranslator.Core.Asr;
using WisperTranslator.Core.Audio;
using WisperTranslator.Core.History;
using WisperTranslator.Core.Models;
using WisperTranslator.Core.Translation;
using WisperTranslator.Core.Vad;

namespace WisperTranslator.Core.Session;

/// <summary>
/// Compone l'intera catena (sorgenti audio → VAD → ASR → traduzione) e pubblica le battute
/// pronte per la UI. È il punto unico da cui l'applicazione avvia e ferma tutto.
/// </summary>
public sealed class TranscriptionSession : IAsyncDisposable
{
    private readonly Dictionary<int, Cue> _cues = [];
    private readonly Dictionary<int, CueTranslationLane> _translationLanes = [];
    private readonly object _cueGate = new();
    private readonly SemaphoreSlim _fastTranslationGate;
    private readonly SemaphoreSlim _qualityTranslationGate = new(1, 1);
    private SessionStore? _history;
    private readonly bool _ownsHistory;
    private long _historySessionId;
    private string? _diagnosticPath;
    private readonly object _diagnosticGate = new();

    private AudioMixer? _mixer;
    private VoiceCapture? _systemCapture;
    private VoiceCapture? _microphoneCapture;
    private IAsrEngine? _partialEngine;
    private IAsrEngine? _finalEngine;
    private NeMoSpeechServer? _realtimeServer;
    private SpeakerTracker? _speakerTracker;
    private bool _ownsPartialEngine;
    private bool _ownsFinalEngine;
    private readonly List<VoiceCapture> _captures = [];
    private readonly List<Lane> _lanes = [];

    /// <summary>
    /// Corsia → battuta. Con più sorgenti gli enunciati di ognuna partono da 1: senza questa
    /// mappa le due corsie si sovrascriverebbero a vicenda.
    /// </summary>
    private readonly Dictionary<(int Lane, int Utterance), int> _cueIds = [];

    private int _nextCueId;

    /// <summary>Ultime battute finali: la passata di continuità traduce le ultime due come una sola.</summary>
    private readonly List<int> _recentFinalCues = [];
    private readonly object _continuityGate = new();
    private CancellationTokenSource? _continuityCancellation;

    /// <summary>Righe in attesa di raggiungere il minimo di parole prima di chiudersi.</summary>
    private readonly Dictionary<int, WordWindow> _wordWindows = [];

    /// <summary>Oltre questo tempo una riga corta si chiude comunque: niente attese eterne.</summary>
    private static readonly TimeSpan WordWindowLimit = TimeSpan.FromSeconds(10);

    private const string TranslationLaneFast = "rapida";
    private const string TranslationLaneQuality = "qualità";
    private const string TranslationLaneContinuity = "due frasi";

    /// <summary>
    /// Una riga in costruzione: gli enunciati già chiusi più l'ipotesi di quello in corso.
    /// Tenere separati i pezzi permette di sostituire una ri-decodifica senza duplicarla.
    /// </summary>
    private sealed class WordWindow(int cueId, TimeSpan start, int speaker)
    {
        public int CueId { get; } = cueId;
        public TimeSpan Start { get; } = start;
        public int Speaker { get; } = speaker;

        /// <summary>Enunciati chiusi dentro la finestra (identificativo + testo definitivo).</summary>
        public List<(int Utterance, string Text)> Closed { get; } = [];

        /// <summary>Ipotesi dell'enunciato in corso, non ancora definitiva.</summary>
        public string Partial { get; set; } = string.Empty;

        public string Text
        {
            get
            {
                var text = string.Empty;
                foreach (var part in Closed)
                {
                    text = text.Length == 0 ? part.Text : SentenceWindow.Join(text, part.Text);
                }

                if (Partial.Length == 0)
                {
                    return text;
                }

                return text.Length == 0 ? Partial : SentenceWindow.Join(text, Partial);
            }
        }

        /// <summary>Testo definitivo: chiude l'enunciato in corso e sostituisce una ri-decodifica.</summary>
        public void Close(int utterance, string text)
        {
            Partial = string.Empty;
            if (Closed.Count > 0 && Closed[^1].Utterance == utterance)
            {
                Closed[^1] = (utterance, text);
                return;
            }

            Closed.Add((utterance, text));
        }
    }

    private TranslationServer? _translationServer;
    private TranslationService? _translationService;
    private TranslationService? _qualityTranslationService;
    private CancellationTokenSource? _cancellation;
    private Task? _pipeline;
    private bool _disposed;

    /// <summary>Una corsia: VAD e trascrittore indipendenti su una sola sorgente audio.</summary>
    private sealed record Lane(int Index, int Speaker, SpeechSegmenter Segmenter, RealtimeTranscriber Transcriber)
    {
        /// <summary>Il parlante è noto a priori (il microfono è sempre "Tu").</summary>
        public bool SpeakerIsFixed => Speaker != Speakers.Unknown;
    }

    public TranscriptionSession(SessionOptions? options = null, SessionStore? history = null)
    {
        Options = options ?? new SessionOptions();
        _history = history;
        _ownsHistory = history is null;
        var cores = Hardware.HardwareDetector.Detect().PhysicalCores;
        _fastTranslationGate = new SemaphoreSlim(Math.Clamp(cores / 2, 2, 4));
    }

    public SessionOptions Options { get; }

    /// <summary>Battuta creata o aggiornata (parziale o finale).</summary>
    public event Action<Cue>? CueUpdated;

    /// <summary>Messaggi di stato leggibili, per la barra di stato della UI.</summary>
    public event Action<string>? StatusChanged;

    public event Action<Cue>? CueCompleted;

    public bool IsRunning => _pipeline is { IsCompleted: false };

    public string? LastError { get; private set; }

    public long Transcribed { get; private set; }

    /// <summary>Quante rifiniture di qualità sono andate a schermo: diagnostica e test.</summary>
    public long QualityPasses { get; private set; }

    /// <summary>Quante volte la coppia di frasi è stata ritradotta per continuità.</summary>
    public long ContinuityPasses { get; private set; }

    /// <summary>Storico locale: viene creato automaticamente al primo avvio della sessione.</summary>
    public SessionStore? History => _history;

    public long HistorySessionId => _historySessionId;

    public async Task StartAsync(IProgress<string>? status = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
        {
            return;
        }

        void Report(string message)
        {
            status?.Report(message);
            StatusChanged?.Invoke(message);
        }

        Report("Preparo il rilevatore di voce...");
        await ModelStore.EnsureVadModelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var diarizerPath = Options.ConversationMode
            ? await PrepareDiarizerAsync(Report, cancellationToken).ConfigureAwait(false)
            : null;

        Report("Preparo il riconoscitore istantaneo...");
        var partial = await AsrBackendFactory
            .CreateAsync(
                Options.LiveBackend,
                Options.PartialModel,
                Options.SourceLanguage,
                Report,
                diarizerPath,
                diarize: false,
                cancellationToken)
            .ConfigureAwait(false);
        _partialEngine = partial.Engine;
        _ownsPartialEngine = partial.Owned;

        Report("Preparo il riconoscitore accurato...");
        var final = await AsrBackendFactory
            .CreateAsync(
                Options.FinalBackend,
                Options.FinalModel,
                Options.SourceLanguage,
                Report,
                diarizerPath,
                diarize: diarizerPath is not null,
                cancellationToken)
            .ConfigureAwait(false);
        _finalEngine = final.Engine;
        _ownsFinalEngine = final.Owned;

        if (Options.LiveBackend == Hardware.AsrBackend.NeMoSpeech)
        {
            try
            {
                _realtimeServer = await NeMoSpeechHost
                    .EnsureAsync(Report, diarizerPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Report($"Streaming NeMo non disponibile ({exception.Message}): uso i parziali batch.");
                _realtimeServer = null;
            }

            if (_realtimeServer is not null && diarizerPath is not null)
            {
                _speakerTracker = new SpeakerTracker(new NeMoDiarizationClient(_realtimeServer.Port));
            }
        }

        if (Options.ConversationMode && diarizerPath is null)
        {
            Report("Conversazione senza diarizzazione: le battute restano senza nome del parlante.");
        }

        if (Options.Translate)
        {
            Report("Avvio il traduttore locale...");
            await StartTranslationAsync(Report, cancellationToken).ConfigureAwait(false);
        }

        Report("Apro le sorgenti audio...");
        _cueIds.Clear();
        _nextCueId = 0;
        BuildPipeline();

        StartHistory();
        _cancellation = new CancellationTokenSource();
        _pipeline = Task.WhenAll(_lanes.Select(lane =>
            Task.Run(() => lane.Transcriber.RunAsync(_cancellation.Token))));
        Report("In ascolto");
    }

    /// <summary>
    /// Il diarizzatore serve solo in modalità conversazione. Si scarica da solo una volta, ma
    /// non sulle macchine minime: lì resta spento finché non lo si scarica a mano.
    /// </summary>
    private async Task<string?> PrepareDiarizerAsync(Action<string> report, CancellationToken cancellationToken)
    {
        if (Options.FinalBackend != Hardware.AsrBackend.NeMoSpeech)
        {
            report("La diarizzazione richiede il motore definitivo NeMo: la salto.");
            return null;
        }

        if (NeMoModels.DiarizerPathIfInstalled(Options.DiarizerModelId) is { } installed)
        {
            var entry = NeMoModels.DiarizerEntry(Options.DiarizerModelId);
            report($"Diarizzazione attiva: {entry.DisplayName}");
            return installed;
        }

        if (Options.PerformancePreset == Hardware.PerformancePreset.Reattivo)
        {
            report("PC minimo: diarizzazione spenta. Si attiva scaricando il diarizzatore dalle impostazioni.");
            return null;
        }

        try
        {
            var entry = NeMoModels.DiarizerEntry(Options.DiarizerModelId);
            report($"Scarico il diarizzatore {entry.DisplayName} (una volta sola)...");
            return await NeMoModels.EnsureDiarizerAsync(Options.DiarizerModelId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = exception.Message;
            report($"Diarizzatore non disponibile ({exception.Message}): continuo senza nomi dei parlanti.");
            return null;
        }
    }

    private void StartHistory()
    {
        _history ??= new SessionStore(retentionDays: Options.RetentionDays);
        if (_history.RecoveredFromCorruption)
        {
            StatusChanged?.Invoke(
                "Lo storico era danneggiato ed è stato messo da parte: riparto con un archivio nuovo.");
        }

        _history.PurgeExpired();
        _historySessionId = _history.BeginSession(Options.SourceLanguage, Options.TargetLanguage);

        if (Options.DiagnosticLog)
        {
            _diagnosticPath = Options.DiagnosticLogPath
                              ?? Path.Combine(AppPaths.EnsureSubdirectory("logs"), $"sessione-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(_diagnosticPath)!);
        }
    }

    /// <summary>Una riga JSON per aggiornamento: timestamp, tipo, latenza, durata e testo.</summary>
    private void WriteDiagnostic(Cue cue, TimeSpan latency, int textLength)
    {
        if (_diagnosticPath is null)
        {
            return;
        }

        var line = System.Text.Json.JsonSerializer.Serialize(new
        {
            t = DateTime.Now.ToString("HH:mm:ss.fff"),
            utterance = cue.Id,
            final = cue.IsFinal,
            audioStart = Math.Round(cue.AudioStart.TotalSeconds, 2),
            audioDuration = Math.Round(cue.Duration.TotalSeconds, 2),
            latencyMs = (int)latency.TotalMilliseconds,
            chars = textLength,
            speaker = cue.Speaker,
            text = cue.Original,
        });

        lock (_diagnosticGate)
        {
            File.AppendAllText(_diagnosticPath, line + Environment.NewLine);
        }
    }

    private async Task StartTranslationAsync(Action<string> report, CancellationToken cancellationToken)
    {
        try
        {
            var executable = await TranslationServer
                .EnsureExecutableAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            _translationServer = new TranslationServer(executable, port: Options.TranslationPort);
            if (!await _translationServer.EnsureStartedAsync(TimeSpan.FromSeconds(30), cancellationToken)
                    .ConfigureAwait(false))
            {
                report("Traduttore non disponibile: continuo solo con la trascrizione");
                _translationServer.Dispose();
                _translationServer = null;
                return;
            }

            _translationService = new TranslationService(
                new LocalHttpEngine(Options.TranslationPort));

            if (Options.TranslationPipeline == TranslationPipelineMode.FastOnly)
            {
                report("Traduzione veloce attiva: solo Bergamot istantaneo");
                _qualityTranslationService = null;
            }
            else
            {
                _qualityTranslationService = StartQualityEngine(report);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = exception.Message;
            report($"Traduttore non disponibile ({exception.Message}): continuo solo con la trascrizione");
        }
    }

    /// <summary>
    /// Secondo stadio: si attiva solo se i modelli ONNX ci sono e la macchina lo regge. Su una
    /// macchina minima la corsia rapida resta identica, senza messaggi d'errore.
    /// </summary>
    private TranslationService? StartQualityEngine(Action<string> report)
    {
        if (Options.QualityTranslation == QualityTranslationMode.Off)
        {
            return null;
        }

        if (!OnnxTranslationEngine.IsPairInstalled("it", "en")
            || !OnnxTranslationEngine.IsPairInstalled("en", "it"))
        {
            if (Options.QualityTranslation == QualityTranslationMode.Forced)
            {
                report("Rifinitura di qualità non attiva: scarica i modelli Marian dalla scheda Modelli");
            }

            return null;
        }

        if (Options.QualityTranslation == QualityTranslationMode.Auto)
        {
            var profile = Hardware.HardwareDetector.Detect();
            if (profile.PhysicalCores <= 4 || profile.RamMegabytes < 8192)
            {
                report("Rifinitura di qualità spenta: questa macchina resta sulla corsia rapida");
                return null;
            }
        }

        report("Rifinitura di qualità pronta (Marian ONNX)");
        return new TranslationService(new OnnxTranslationEngine(), cacheLimit: 400);
    }

    private void BuildPipeline()
    {
        var sources = new List<(IPcmSource Source, VoiceCapture? Capture, int Speaker)>();

        if (Options.SystemAudio)
        {
            _systemCapture = new VoiceCapture(SourceKind.System, Options.SystemDeviceId);
            _captures.Add(_systemCapture);
            sources.Add((_systemCapture, _systemCapture, Speakers.Unknown));
        }

        if (Options.Microphone)
        {
            _microphoneCapture = new VoiceCapture(SourceKind.Microphone, Options.MicrophoneDeviceId);
            _captures.Add(_microphoneCapture);
            sources.Add((
                _microphoneCapture,
                _microphoneCapture,
                Options.ConversationMode ? Speakers.You : Speakers.Unknown));
        }

        if (sources.Count == 0)
        {
            throw new InvalidOperationException("Nessuna sorgente audio selezionata.");
        }

        if (Options.ConversationMode)
        {
            // Una corsia per sorgente: il VAD di ognuna chiude le frasi per conto proprio.
            foreach (var (source, _, speaker) in sources)
            {
                AddLane(source, speaker);
            }
        }
        else
        {
            _mixer = new AudioMixer();
            foreach (var (_, capture, _) in sources)
            {
                _mixer.Add(capture!);
            }

            AddLane(_mixer, Speakers.Unknown);
        }

        foreach (var capture in _captures)
        {
            capture.Start();
        }
    }

    private void AddLane(IPcmSource source, int speaker)
    {
        var segmenter = new SpeechSegmenter(new SileroVad(AppPaths.VadModelPath));
        var streaming = _realtimeServer is null ? null : new NeMoRealtimeClient(_realtimeServer.Port);
        var lane = new Lane(
            _lanes.Count,
            speaker,
            segmenter,
            new RealtimeTranscriber(source, segmenter, _partialEngine!, null, _finalEngine!, streaming)
            {
                Language = Options.SourceLanguage,
            });

        lane.Transcriber.Update += update => OnTranscriptUpdate(lane, update);
        if (!lane.SpeakerIsFixed && _speakerTracker is not null)
        {
            lane.Transcriber.AudioBlock += (block, _) => _speakerTracker?.Append(block.Span);
        }

        lane.Transcriber.FinalResolved += (utterance, start, samples, result) =>
            _ = ResolveSpeakersAsync(lane, utterance, start, samples, result);
        _lanes.Add(lane);
    }

    /// <summary>
    /// La frase è già a schermo; qui si risolve solo chi l'ha detta. Se la diarizzazione
    /// tarda o fallisce, il testo resta valido e senza etichetta.
    /// </summary>
    private async Task ResolveSpeakersAsync(
        Lane lane,
        int utterance,
        TimeSpan utteranceStart,
        float[] samples,
        AsrResult result)
    {
        if (!Options.ConversationMode
            || lane.SpeakerIsFixed
            || _speakerTracker is null
            || result.Segments.Count == 0)
        {
            return;
        }

        try
        {
            _speakerTracker.TriggerNow();
            var cueId = ResolveCueId(lane.Index, utterance);

            var token = _cancellation?.Token ?? CancellationToken.None;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
            IReadOnlyList<AsrSegment>? tagged = null;
            while (DateTime.UtcNow < deadline)
            {
                if (_speakerTracker.TryApply(result.Segments, utteranceStart, out tagged))
                {
                    break;
                }

                await Task.Delay(250, token).ConfigureAwait(false);
            }

            if (tagged is null)
            {
                return;
            }

            var turns = RealtimeTranscriber.BuildTurns(new AsrResult(
                result.Text,
                result.Language,
                result.AudioDuration,
                result.Elapsed,
                tagged));
            if (turns.Count == 0)
            {
                return;
            }

            if (turns.Count == 1)
            {
                UpdateSpeaker(cueId, turns[0].Speaker);
                return;
            }

            for (var index = 0; index < turns.Count; index++)
            {
                var turn = turns[index];
                var turnCueId = index == 0
                    ? ResolveCueId(lane.Index, utterance)
                    : NextCueId();
                PublishCue(
                    turnCueId,
                    turn.Speaker,
                    turn.Text,
                    isFinal: true,
                    utteranceStart + turn.Start,
                    turn.Duration,
                    provisional: string.Empty,
                    TimeSpan.Zero);
            }
        }
        catch (OperationCanceledException)
        {
            // sessione fermata
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            StatusChanged?.Invoke($"Diarizzazione non riuscita: {exception.Message}");
        }
    }

    /// <summary>Aggiorna solo l'etichetta del parlante: testo e posizione non si muovono.</summary>
    private void UpdateSpeaker(int cueId, int speaker)
    {
        Cue updated;
        lock (_cueGate)
        {
            if (_disposed || !_cues.TryGetValue(cueId, out var cue))
            {
                return;
            }

            updated = cue with { Speaker = speaker };
            _cues[cueId] = updated;
        }

        CueUpdated?.Invoke(updated);
        SaveHistoryCue(updated.Id);
    }

    private void OnTranscriptUpdate(Lane lane, TranscriptUpdate update)
    {
        var turns = !lane.SpeakerIsFixed && update.IsFinal ? update.Turns : null;
        if (turns is { Count: > 1 })
        {
            // Nella stessa frase si sono sentite più voci: la battuta in corso diventa il primo
            // turno e gli altri si accodano, senza far saltare l'elenco.
            for (var i = 0; i < turns.Count; i++)
            {
                var turn = turns[i];
                var cueId = i == 0
                    ? ResolveCueId(lane.Index, update.UtteranceId)
                    : NextCueId();
                PublishCue(
                    cueId,
                    turn.Speaker,
                    turn.Text,
                    isFinal: true,
                    update.Start + turn.Start,
                    turn.Duration,
                    provisional: string.Empty,
                    update.Latency);
            }

            return;
        }

        var speaker = lane.SpeakerIsFixed
            ? lane.Speaker
            : turns is { Count: 1 } single ? single[0].Speaker : Speakers.Unknown;

        if (WordWindowEnabled)
        {
            PublishWithWordWindow(lane, update, speaker);
            return;
        }

        PublishCue(
            ResolveCueId(lane.Index, update.UtteranceId),
            speaker,
            update.Text,
            update.IsFinal,
            update.Start,
            update.Duration,
            update.Provisional,
            update.Latency);
    }

    /// <summary>Vero quando ogni riga deve rispettare il range di parole scelto.</summary>
    private bool WordWindowEnabled =>
        Options.Translate
        && !Options.ConversationMode
        && Options.SentenceMaxWords > 1;

    /// <summary>
    /// Impagina la battuta nel range di parole: sotto il minimo il silenzio non chiude la frase
    /// (il testo resta in attesa della continuazione), sopra il massimo la riga si spezza.
    /// Parziali e definitivi della stessa finestra condividono la riga, quindi il testo cresce
    /// senza salti e un parziale in ritardo non crea una riga fantasma.
    /// </summary>
    private void PublishWithWordWindow(Lane lane, TranscriptUpdate update, int speaker)
    {
        WordWindow? window;
        lock (_cueGate)
        {
            _wordWindows.TryGetValue(lane.Index, out window);
        }

        var cueId = window?.CueId ?? ResolveCueId(lane.Index, update.UtteranceId);
        lock (_cueGate)
        {
            _cueIds[(lane.Index, update.UtteranceId)] = cueId;
        }

        var start = window?.Start ?? update.Start;
        var end = update.Start + update.Duration;

        if (window is null)
        {
            window = new WordWindow(cueId, start, speaker);
        }

        if (!update.IsFinal)
        {
            // L'ipotesi dell'enunciato in corso si sostituisce, non si accoda.
            window.Partial = update.Text.Trim();
            PublishCue(cueId, speaker, window.Text, isFinal: false, start, end - start, window.Text, update.Latency);
            SetWordWindow(lane.Index, window);
            return;
        }

        window.Close(update.UtteranceId, update.Text.Trim());
        var text = window.Text;

        var min = Math.Max(1, Options.SentenceMinWords);
        var max = Math.Max(min, Options.SentenceMaxWords);
        var pending = text;
        var pendingStart = start;
        var pendingCue = cueId;

        while (SentenceWindow.CountWords(pending) > max)
        {
            var (head, tail) = SentenceWindow.Cut(pending, max);
            var headEnd = SplitEnd(
                pendingStart,
                end,
                SentenceWindow.CountWords(head),
                SentenceWindow.CountWords(pending));
            PublishCue(pendingCue, speaker, head, isFinal: true, pendingStart, headEnd - pendingStart,
                provisional: string.Empty, update.Latency);
            pendingCue = NextCueId();
            pendingStart = headEnd;
            pending = tail;
        }

        var forceClose = end - pendingStart >= WordWindowLimit;
        if (SentenceWindow.CountWords(pending) >= min || forceClose)
        {
            PublishCue(pendingCue, speaker, pending, isFinal: true, pendingStart, end - pendingStart,
                provisional: string.Empty, update.Latency);
            ClearWordWindow(lane.Index);
            return;
        }

        // Pausa prima del minimo: la riga resta aperta e aspetta la continuazione.
        PublishCue(pendingCue, speaker, pending, isFinal: false, pendingStart, end - pendingStart, pending,
            update.Latency);
        // Se la riga è stata spezzata, il resto riparte con una finestra nuova; altrimenti la
        // finestra tiene già tutti i pezzi chiusi.
        if (pendingCue != cueId)
        {
            window = new WordWindow(pendingCue, pendingStart, speaker);
            window.Closed.Add((update.UtteranceId, pending));
        }

        SetWordWindow(lane.Index, window);
    }

    /// <summary>Stima il confine di una riga spezzata, proporzionale alle parole.</summary>
    private static TimeSpan SplitEnd(TimeSpan start, TimeSpan end, int headWords, int totalWords)
    {
        if (totalWords <= 0)
        {
            return end;
        }

        var span = end - start;
        return start + TimeSpan.FromTicks((long)(span.Ticks * (double)headWords / totalWords));
    }

    private void SetWordWindow(int lane, WordWindow window)
    {
        lock (_cueGate)
        {
            _wordWindows[lane] = window;
        }
    }

    private void ClearWordWindow(int lane)
    {
        lock (_cueGate)
        {
            _wordWindows.Remove(lane);
        }
    }

    /// <summary>Id stabile per la coppia corsia+enunciato: parziali e finale restano la stessa riga.</summary>
    private int ResolveCueId(int lane, int utterance)
    {
        lock (_cueGate)
        {
            if (_cueIds.TryGetValue((lane, utterance), out var existing))
            {
                return existing;
            }

            var created = NextCueIdLocked();
            _cueIds[(lane, utterance)] = created;
            return created;
        }
    }

    private int NextCueId()
    {
        lock (_cueGate)
        {
            return NextCueIdLocked();
        }
    }

    private int NextCueIdLocked() => ++_nextCueId;

    private void PublishCue(
        int cueId,
        int speaker,
        string text,
        bool isFinal,
        TimeSpan start,
        TimeSpan duration,
        string provisional,
        TimeSpan latency)
    {
        Cue cue;
        lock (_cueGate)
        {
            if (_disposed) return;
            var translation = _cues.TryGetValue(cueId, out var previous) ? previous.Translation : string.Empty;
            // Un parziale arrivato dopo la decodifica finale non può riaprire la battuta.
            if (previous is { IsFinal: true } && !isFinal) return;
            cue = new Cue(
                cueId,
                text,
                translation,
                isFinal,
                start,
                duration,
                previous?.CreatedAt ?? DateTime.Now,
                provisional,
                speaker);
            _cues[cueId] = cue;
            if (isFinal && previous is not { IsFinal: true })
            {
                Transcribed++;
            }
        }

        CueUpdated?.Invoke(cue);
        WriteDiagnostic(cue, latency, cue.Original.Length);
        if (isFinal)
        {
            CueCompleted?.Invoke(cue);
        }

        SaveHistoryCue(cue.Id);

        if (Options.Translate && _translationService is not null && cue.Original.Length > 1)
        {
            ScheduleTranslation(cue, isFinal);
        }

        if (isFinal)
        {
            ScheduleContinuityPass(cue.Id);
        }
    }

    /// <summary>
    /// Le due corsie della battuta. La rapida traduce il parziale che cresce senza essere mai
    /// annullata; la qualità rifinisce a metà enunciato e alla fine. La corsia si archivia da
    /// sola quando il finale è stato tradotto da entrambe.
    /// </summary>
    private void ScheduleTranslation(Cue cue, bool isFinal)
    {
        CueTranslationLane lane;
        lock (_cueGate)
        {
            if (_disposed) return;
            if (!_translationLanes.TryGetValue(cue.Id, out var existing))
            {
                // Con la continuità attiva la rifinitura frase per frase è sostituita dalla
                // passata sulle ultime due frasi: la corsia resta istantanea.
                var perSentenceQuality = ContinuityPassEnabled
                    ? null
                    : _qualityTranslationService;
                existing = new CueTranslationLane(
                    cue.Id,
                    _translationService!,
                    perSentenceQuality,
                    _fastTranslationGate,
                    _qualityTranslationGate,
                    () => (Options.SourceLanguage, Options.TargetLanguage),
                    PublishTranslation,
                    message => StatusChanged?.Invoke(message),
                    _cancellation?.Token ?? CancellationToken.None,
                    mode: Options.TranslationPipeline);
                existing.Finished += OnTranslationLaneFinished;
                _translationLanes[cue.Id] = existing;
            }

            lane = existing;
        }

        lane.Update(cue.Original, isFinal);
    }

    private void OnTranslationLaneFinished(CueTranslationLane lane)
    {
        lock (_cueGate)
        {
            if (_translationLanes.TryGetValue(lane.CueId, out var current) && ReferenceEquals(current, lane))
            {
                _translationLanes.Remove(lane.CueId);
            }
        }

        lane.Dispose();
    }

    /// <summary>Vero quando la passata sulle due frasi può lavorare.</summary>
    private bool ContinuityPassEnabled =>
        Options.Translate
        && !Options.ConversationMode
        && Options.ContinuousTranslation
        && Options.TranslationPipeline == TranslationPipelineMode.DualPass
        && _qualityTranslationService is not null;

    /// <summary>
    /// Passata di continuità: appena una frase si chiude, le ultime due si traducono **come una
    /// sola** (con il motore di qualità). Una frase nuova annulla la coppia precedente: vince
    /// sempre la più recente, così non si accodano traduzioni vecchie.
    /// </summary>
    private void ScheduleContinuityPass(int cueId)
    {
        if (!ContinuityPassEnabled)
        {
            return;
        }

        int firstId, lastId;
        CancellationToken token;
        lock (_continuityGate)
        {
            if (!_recentFinalCues.Contains(cueId))
            {
                _recentFinalCues.Add(cueId);
            }

            while (_recentFinalCues.Count > 2)
            {
                _recentFinalCues.RemoveAt(0);
            }

            lastId = _recentFinalCues[^1];
            // Con una sola frase (inizio sessione) si rifinisce quella da sola.
            firstId = _recentFinalCues.Count >= 2 ? _recentFinalCues[^2] : lastId;
            _continuityCancellation?.Cancel();
            _continuityCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _cancellation?.Token ?? CancellationToken.None);
            token = _continuityCancellation.Token;
        }

        _ = Task.Run(() => RunContinuityAsync(firstId, lastId, token), token);
    }

    private async Task RunContinuityAsync(int firstId, int lastId, CancellationToken cancellationToken)
    {
        try
        {
            string first, last;
            lock (_cueGate)
            {
                if (_disposed
                    || !_cues.TryGetValue(firstId, out var firstCue)
                    || !_cues.TryGetValue(lastId, out var lastCue))
                {
                    return;
                }

                first = firstCue.Original;
                last = lastCue.Original;
            }

            var text = firstId == lastId ? last : SentenceWindow.Join(first, last);
            if (SentenceWindow.CountWords(text) == 0)
            {
                return;
            }

            await _qualityTranslationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await _qualityTranslationService!
                    .TranslateAsync(text, Options.SourceLanguage, Options.TargetLanguage, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Text.Length > 0)
                {
                    PublishTranslation(lastId, result.Text, TranslationLaneContinuity);
                }
            }
            finally
            {
                _qualityTranslationGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // è arrivata una frase nuova: vince quella
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"Continuità non disponibile: {exception.Message}");
        }
    }

    /// <summary>La traduzione appena pronta aggiorna la battuta senza riordinare l'elenco.</summary>
    private void PublishTranslation(int cueId, string translation, bool quality) =>
        PublishTranslation(cueId, translation, quality ? TranslationLaneQuality : TranslationLaneFast);

    private void PublishTranslation(int cueId, string translation, string lane)
    {
        Cue updated;
        lock (_cueGate)
        {
            if (_disposed || !_cues.TryGetValue(cueId, out var existing)) return;
            if (existing.Translation == translation) return;
            updated = existing with { Translation = translation };
            _cues[cueId] = updated;
        }

        if (lane == TranslationLaneQuality)
        {
            QualityPasses++;
        }
        else if (lane == TranslationLaneContinuity)
        {
            ContinuityPasses++;
        }

        CueUpdated?.Invoke(updated);
        SaveHistoryCue(updated.Id);
        WriteTranslationDiagnostic(updated, lane);
    }

    /// <summary>
    /// Una riga JSON per traduzione pubblicata: serve a misurare sul campo quanto ci mette la
    /// corsia rapida e quando arriva la rifinitura di qualità.
    /// </summary>
    private void WriteTranslationDiagnostic(Cue cue, string lane)
    {
        if (_diagnosticPath is null)
        {
            return;
        }

        var line = System.Text.Json.JsonSerializer.Serialize(new
        {
            t = DateTime.Now.ToString("HH:mm:ss.fff"),
            utterance = cue.Id,
            lane,
            final = cue.IsFinal,
            chars = cue.Original.Length,
            original = cue.Original,
            translation = cue.Translation,
        });

        lock (_diagnosticGate)
        {
            try
            {
                File.AppendAllText(_diagnosticPath, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // il log non deve mai fermare la traduzione
            }
        }
    }

    private void SaveHistoryCue(int cueId)
    {
        lock (_cueGate)
        {
            if (_disposed || _history is null || _historySessionId <= 0 || !_cues.TryGetValue(cueId, out var current)) return;
            try { _history.SaveCue(_historySessionId, current); }
            catch (Exception exception) { LastError = exception.Message; }
        }
    }

    public float CurrentLevel => Math.Max(
        _systemCapture is { Enabled: true } system ? system.Level : 0,
        _microphoneCapture is { Enabled: true } microphone ? microphone.Level : 0);

    public void SetSystemAudio(bool enabled)
    {
        Options.SystemAudio = enabled;
        if (_systemCapture is not null)
        {
            _systemCapture.Enabled = enabled;
        }
    }

    /// <summary>Livelli audio correnti delle sorgenti: la UI li usa come spia di funzionamento.</summary>
    public IReadOnlyList<(string Name, float Level, bool Enabled)> Levels()
    {
        return
        [
            .. _captures
                .Select(capture => (capture.Name, capture.Level, capture.Enabled)),
        ];
    }

    public void SetMicrophone(bool enabled)
    {
        Options.Microphone = enabled;
        if (_microphoneCapture is not null)
        {
            _microphoneCapture.Enabled = enabled;
        }
    }

    public void SetDirection(string sourceLanguage, string targetLanguage)
    {
        Options.SourceLanguage = sourceLanguage;
        Options.TargetLanguage = targetLanguage;
        foreach (var lane in _lanes)
        {
            lane.Transcriber.Language = sourceLanguage;
        }

        if (_translationService is not null)
        {
            _translationService.ClearCache();
        }

        if (_qualityTranslationService is not null)
        {
            _qualityTranslationService.ClearCache();
        }
    }

    public async Task StopAsync()
    {
        if (_cancellation is not null)
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
        }

        foreach (var capture in _captures) Safe(capture.Stop);

        if (_pipeline is not null)
        {
            try
            {
                // Tetto di sicurezza: la decodifica nativa non deve poter bloccare la UI.
                await _pipeline.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                LastError ??= "Arresto oltre il tempo massimo: la decodifica in corso è stata abbandonata.";
            }
            catch (Exception)
            {
                // arresto richiesto
            }
        }

        _pipeline = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);

        Task translations;
        lock (_cueGate)
        {
            foreach (var lane in _translationLanes.Values)
            {
                lane.Dispose();
            }

            translations = Task.WhenAll(_translationLanes.Values.Select(lane => lane.Completion));
            _translationLanes.Clear();
        }

        // Lo stop può essere già tornato per timeout; la memoria dei decoder e i servizi
        // si liberano solo dopo che il lavoro effettivo è terminato.
        try
        {
            await Task.WhenAll(_lanes.Select(lane => lane.Transcriber.WorkersCompletion).Append(translations))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        Safe(() =>
        {
            foreach (var lane in _lanes)
            {
                lane.Transcriber.Dispose();
                lane.Segmenter.Dispose();
            }

            _lanes.Clear();
        });

        // Whisper appartiene al pool e resta caldo; Vosk/NeMo sono nostri e si liberano qui.
        Safe(() =>
        {
            if (_ownsPartialEngine)
            {
                _partialEngine?.Dispose();
            }
        });
        Safe(() =>
        {
            if (_ownsFinalEngine)
            {
                _finalEngine?.Dispose();
            }
        });

        // Le sorgenti del mixer appartengono al mixer; in modalità conversazione no.
        Safe(() =>
        {
            foreach (var capture in _captures.Where(capture => _mixer is null || !_mixer.Sources.Contains(capture)))
            {
                capture.Dispose();
            }

            _captures.Clear();
        });
        Safe(() => _mixer?.Dispose());
        Safe(() => _translationService?.Dispose());
        Safe(() => _qualityTranslationService?.Dispose());
        Safe(() => _translationServer?.Dispose());
        Safe(() => _speakerTracker?.Dispose());
        Safe(() => _fastTranslationGate.Dispose());
        Safe(() => _qualityTranslationGate.Dispose());
        Safe(() => _cancellation?.Dispose());

        if (_history is not null && _historySessionId > 0)
        {
            Safe(() => _history.EndSession(_historySessionId));
        }

        if (_ownsHistory)
        {
            Safe(() => _history?.Dispose());
        }
    }

    /// <summary>La pulizia non deve mai trasformare uno stop in un errore fatale.</summary>
    private void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
        }
    }
}
