using lib.io;

namespace plugins.eliminator
{
    public class EliminatorSettings
    {
        // --- ARGOMENTI FISSI ---
        [Fixed(0, "percorso", "Il percorso target da cui avviare la cancellazione (usa '.' per la cartella corrente)")]
        public string TargetPath { get; set; } = string.Empty;

        // --- opzioni del comando ---

        [Option(CliMeta.WhatIfFlag, CliMeta.WhatIfDescription, "Configurazione")]
        public bool WhatIf { get; set; }

        [Option("recursive|r", "Scansiona anche le sottocartelle", "Configurazione")]
        public bool Recursive { get; set; }

        [Option("drop-source|ds", "Cancella al termine anche la cartella target", "Configurazione")]
        public bool DropSource { get; set; } = false;

        [Option("drop-dirs|dd", "Cancella al termine anche tutte le cartelle vuote", "Configurazione")]
        public bool DropDirs { get; set; } = false;

        [Option(CliMeta.SilenceFlag, CliMeta.SilenceDesc, "Configurazione")]
        public bool Silence { get; set; } = false;

        [Option("ignore-errors|ie", "Se attivo ignora gli errori di cancellazione dei file", "Configurazione")]
        public bool IgnoreErrors { get; set; } = false;

        [Option("threads|t", "Specifica il numero massimo di thread (default: numero di core della CPU)", "Configurazione")]
        public int? Threads { get; set; }

        [Option("force", "Forza l'operazione senza chiedere conferma", "Configurazione")]
        public bool Force { get; set; } = false;

        // --- opzioni di filtraggio ---

        [Option("pattern|p", CliMeta.FilePatternDesc, "Filtri")]
        public string? Pattern { get; set; }

        [Option(CliMeta.PatternMatchTypeFlag, CliMeta.PatternMatchTypeDescription, "Filtri")]
        public PatternMatchType PatternMatchType { get; set; } = PatternMatchType.Auto;

        [Option("ignore-case|i", "Rende la ricerca case-insensitive", "Filtri")]
        public bool IgnoreCase { get; set; }

        [Option(CliMeta.HiddenFlag, CliMeta.HiddenDesc, "Filtri")]
        public bool IncludeHidden { get; set; } = false;
        

        [Option(CliMeta.DateAfterFlag, CliMeta.DateAfterDesc, "Filtri")]
        public RelativeDateTime? DateAfter { get; set; }

        [Option(CliMeta.DateBeforeFlag, CliMeta.DateBeforeDesc, "Filtri")]
        public RelativeDateTime? DateBefore { get; set; }

        [Option(CliMeta.MinSizeFlag, CliMeta.MinSizeDesc, "Filtri")]
        public RelativeSize? MinSize { get; set; }
        
        [Option(CliMeta.MaxSizeFlag, CliMeta.MaxSizeDesc, "Filtri")]
        public RelativeSize? MaxSize { get; set; }
    }
}