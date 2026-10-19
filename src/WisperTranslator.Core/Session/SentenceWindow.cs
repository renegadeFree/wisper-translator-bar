namespace WisperTranslator.Core.Session;

/// <summary>
/// Regole di impaginazione delle battute: quante parole deve avere una riga e come si uniscono
/// due frasi per la passata di continuità. Sono funzioni pure, così si possono verificare senza
/// audio né modelli.
/// </summary>
public static class SentenceWindow
{
    /// <summary>Parole della riga: gli spazi multipli non contano.</summary>
    public static int CountWords(string text) => Tokenize(text).Length;

    /// <summary>
    /// Unisce due frasi in una sola: toglie la punteggiatura finale della prima, così
    /// "Il gatto sul tavolo." + "e la penna sul tavolo." diventa una frase sola.
    /// </summary>
    public static string Join(string first, string second)
    {
        var left = first.Trim().TrimEnd('.', '!', '?', '…', ',', ';', ':');
        var right = second.Trim();
        if (left.Length == 0) return right;
        if (right.Length == 0) return left;
        return left + " " + right;
    }

    /// <summary>
    /// Spezza il testo in righe da al massimo <paramref name="maxWords"/> parole. La coda resta
    /// aperta finché non arriva a <paramref name="minWords"/>: è così che un silenzio breve non
    /// interrompe la frase.
    /// </summary>
    public static (IReadOnlyList<string> Rows, string Leftover) Split(string text, int minWords, int maxWords)
    {
        var words = Tokenize(text);
        var rows = new List<string>();
        var min = Math.Max(1, minWords);
        var max = Math.Max(min, maxWords);
        var index = 0;
        while (words.Length - index > max)
        {
            rows.Add(string.Join(" ", words, index, max));
            index += max;
        }

        var rest = index < words.Length ? string.Join(" ", words, index, words.Length - index) : string.Empty;
        if (rest.Length == 0)
        {
            return (rows, string.Empty);
        }

        if (words.Length - index >= min)
        {
            rows.Add(rest);
            return (rows, string.Empty);
        }

        return (rows, rest);
    }

    /// <summary>Divide il testo in testa di N parole e coda rimasta (per il taglio delle righe lunghe).</summary>
    public static (string Head, string Tail) Cut(string text, int words)
    {
        var tokens = Tokenize(text);
        var count = Math.Max(1, words);
        if (tokens.Length <= count)
        {
            return (text.Trim(), string.Empty);
        }

        return (string.Join(" ", tokens, 0, count), string.Join(" ", tokens, count, tokens.Length - count));
    }

    private static string[] Tokenize(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
