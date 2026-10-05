namespace plugins;

public static class CliMeta
{
    // --- FLAG E DESCRIZIONI: FILTRI ---
    public const string DateAfterFlag = "date-after|da";
    public const string DateAfterDesc = "Considera i file piu' recenti della data indicata (es: 60d, 2024-01-15, 12h:a, 30d:c). Campo: m modifica (default), c creazione, a accesso";

    public const string DateBeforeFlag = "date-before|db";
    public const string DateBeforeDesc = "Considera i file piu' vecchi della data indicata (es: 60d, 2024-01-15, 12h:a, 30d:c). Campo: m modifica (default), c creazione, a accesso";

    public const string MinSizeFlag = "min-size|mins";
    public const string MinSizeDesc = "Considera i file con dimensione >= di quella indicata (es: 5m - 5MB, 500 - 500 byte). Unita: b byte, k KB, m MB, g GB, t TB";
    public const string MaxSizeFlag = "max-size|maxs";
    public const string MaxSizeDesc = "Considera i file con dimensione <= di quella indicata (es: 5m - 5MB, 500 - 500 byte). Unita: b byte, k KB, m MB, g GB, t TB";
    

    public const string FilePatternFlag = "pattern|p";
    public const string FilePatternDesc = "Pattern per filtrare i nomi (in auto: testo semplice cerca una sottostringa, * o ? attivano il glob; usa * per tutti)";

    public const string PatternMatchTypeFlag = "match-type|mt";
    public const string PatternMatchTypeDescription = "Modalita' di match: auto (default: testo senza wildcard = sottostringa, * o ? = glob), glob (corrispondenza dell'intero nome con * e ?), fixed (ricerca per sottostringa), regex";

    public const string DirsExcludePatternFlag = "dir-exclude|de";
    public const string DirsExcludePatternDesc = "Pattern regex per le cartelle da non esplorare";


    // --- FLAG E DESCRIZIONI: CONFIGURAZIONE ---
    public const string ThreadsFlag = "threads|t";
    public const string ThreadsDesc = "Numero di thread da usare durante l'esecuzione (default: numero di core)";
    public const string SilenceFlag = "silence|s";
    public const string SilenceDesc = "Se attivo non mostra risultati di progessione a console";
    public const string HiddenFlag = "hidden|H";
    public const string HiddenDesc = "Se attivo include i file nascosti nell'enumerazione";
    public const string MinimalOutputFlag = "minimal|m";
    public const string MinimalOutputDesc = "Se attivo mostra il minimo indispensabile di output a console";
    public const string JustEnoughOutputFlag = "just-enough-output|jeo";
    public const string JustEnoughOutputDesc = "Se attivo mostra il minimo indispensabile di output a console";
    public const string WhatIfFlag = "what-if|wi";
    public const string WhatIfDescription = "Se attivo simula le azioni che verrebbero compiute";

    // --- FLAG E DESCRIZIONI: OUTPUT --- 
    public const string FormatFlag = "format|F";
    public const string FormatDesc = "Formato di output: console (default), csv, json";
    public const string OutputFileFlag = "output-file|o";
    public const string OutputFileDesc = "Indica il percorso del file dove scrivere i risultati";
}
