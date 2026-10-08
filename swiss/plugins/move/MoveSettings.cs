using lib.io;

namespace plugins.move
{
    public class MoveSettings
    {
        // --- ARGOMENTI FISSI ---
        
        [Fixed(0, "sorgente", "Il percorso di origine da cui avviare lo spostamento (usa '.' per la cartella corrente)")]
        public string SourcePath { get; set; } = string.Empty;

        [Fixed(1, "destinazione", "Il percorso di destinazione dove spostare i file (usa '.' per la cartella corrente)")]
        public string DestinationPath { get; set; } = string.Empty;

        // --- opzioni del comando ---

        [Option(CliMeta.WhatIfFlag, CliMeta.WhatIfDescription, "Configurazione")]
        public bool WhatIf { get; set; }

        [Option("recursive|r", "Scansiona anche le sottocartelle e ricrea l'albero nella destinazione", "Configurazione")]
        public bool Recursive { get; set; }

        [Option("overwrite|ow", "Sovrascrive i file nella destinazione se esistono", "Configurazione")]
        public bool Overwrite { get; set; }

        [Option(CliMeta.SilenceFlag, CliMeta.SilenceDesc, "Configurazione")]
        public bool Silence { get; set; } = false;

        [Option("ignore-errors|ie", "Se attivo ignora gli errori di spostamento dei file", "Configurazione")]
        public bool IgnoreErrors { get; set; } = false;

        // --- opzioni di filtraggio ---

        [Option("ignore-case|i", "Rende la ricerca case-insensitive", "Filtri")]
        public bool IgnoreCase { get; set; }

        [Option(CliMeta.HiddenFlag, CliMeta.HiddenDesc, "Filtri")]
        public bool IncludeHidden { get; set; } = false;

        [Option(CliMeta.PatternMatchTypeFlag, CliMeta.PatternMatchTypeDescription, "Filtri")]
        public PatternMatchType PatternMatchType { get; set; } = PatternMatchType.Auto;

        [Option(CliMeta.FilePatternFlag, CliMeta.FilePatternDesc, "Filtri")]
        public string? Pattern { get; set; }

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