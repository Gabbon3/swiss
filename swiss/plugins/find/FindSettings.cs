using lib.io;

namespace plugins.find
{
    public class FindSettings
    {
        // --- ARGOMENTI FISSI ---
        [Fixed(0, "percorso", "La directory di partenza (usa '.' per la cartella corrente)")]
        public string TargetPath { get; set; } = string.Empty;

        // Pattern è opzionale, ma essendo Fixed, la sua presenza dipende dalla lunghezza degli argomenti. 
        // Se non fornito, il nostro ParseSettings lo lascerà a string.Empty o null
        [Fixed(1, "pattern", "Pattern per filtrare i nomi (in auto: testo semplice cerca una sottostringa, * o ? attivano il glob; usa * per tutti) cambia modalità con --match-type")]
        public string? Pattern { get; set; }

        #region Configurazione

        [Option("dirs|d", "Includi le cartelle nella ricerca", "Configurazione")]
        public bool Dirs { get; set; }

        [Option(CliMeta.HiddenFlag, CliMeta.HiddenDesc, "Configurazione")]
        public bool IncludeHidden { get; set; } = false;

        [Option("ignore-case|i", "Rende case insensitive la ricerca", "Configurazione")]
        public bool IgnoreCase { get; set; }

        [Option(CliMeta.MinimalOutputFlag, CliMeta.MinimalOutputDesc, "Configurazione")]
        public bool MinimalOutput { get; set; } = false;

        [Option("recurse|r", "Se attivo ricerca anche nelle sottocartelle", "Configurazione")]
        public bool RecurseSubdirectories { get; set; } = false;

        [Option(CliMeta.SilenceFlag, CliMeta.SilenceDesc, "Configurazione")]
        public bool Silence { get; set; } = false;

        [Option(CliMeta.ThreadsFlag, "Numero di thread usati nella ricerca (1 se sei su HDD)", "Configurazione")]
        public int Threads { get; set; } = Environment.ProcessorCount;

        [Option(CliMeta.DirsExcludePatternFlag, CliMeta.DirsExcludePatternDesc, "Configurazione")]
        public string? ExcludeDirsPattern { get; set; } = null;

        [Option("limit|l", "Limita il numero di risultati", "Configurazione")]
        public int Limit { get; set; } = 0;

        #endregion
        #region Filtri

        [Option(CliMeta.PatternMatchTypeFlag, CliMeta.PatternMatchTypeDescription, "Filtri")]
        public PatternMatchType PatternMatchType { get; set; } = PatternMatchType.Auto;

        [Option(CliMeta.DateAfterFlag, CliMeta.DateAfterDesc, "Filtri")]
        public RelativeDateTime? DateAfter { get; set; }

        [Option(CliMeta.DateBeforeFlag, CliMeta.DateBeforeDesc, "Filtri")]
        public RelativeDateTime? DateBefore { get; set; }

        [Option(CliMeta.MinSizeFlag, CliMeta.MinSizeDesc, "Filtri")]
        public RelativeSize? MinSize { get; set; }
        
        [Option(CliMeta.MaxSizeFlag, CliMeta.MaxSizeDesc, "Filtri")]
        public RelativeSize? MaxSize { get; set; }

        #endregion
        #region Classifica

        // --- OPZIONI CLASSIFICA ---
        [Option("biggest|B", "Restituisce i file più grandi", "Classifica")]
        public bool Biggest { get; set; }

        [Option("smallest|S", "Restituisce i file più piccoli", "Classifica")]
        public bool Smallest { get; set; }

        [Option("newest|N", "Restituisce i file più recenti", "Classifica")]
        public bool Newest { get; set; }

        [Option("oldest|O", "Restituisce i file più vecchi", "Classifica")]
        public bool Oldest { get; set; }

        #endregion
        #region Output

        [Option(CliMeta.FormatFlag, CliMeta.FormatDesc, "Output")]
        public string? Format { get; set; }

        [Option(CliMeta.OutputFileFlag, CliMeta.OutputFileDesc, "Output")]
        public string? OutputFile { get; set; }
        
        #endregion
    }
}