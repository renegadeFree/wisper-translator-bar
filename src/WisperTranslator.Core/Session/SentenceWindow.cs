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

    private static string[] Tokenize(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
