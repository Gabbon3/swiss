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

        [Option(CliMeta.WhatIfFlag, CliMeta.WhatIfDescription, "Comando")]
        public bool WhatIf { get; set; }

        [Option("recursive|r", "Scansiona anche le sottocartelle e ricrea l'albero nella destinazione", "Comando")]
        public bool Recursive { get; set; }

        [Option("overwrite|ow", "Sovrascrive i file nella destinazione se esistono", "Comando")]
        public bool Overwrite { get; set; }

        [Option(CliMeta.SilenceFlag, CliMeta.SilenceDesc, "Comando")]
        public bool Silence { get; set; } = false;

        [Option("ignore-errors|ie", "Se attivo ignora gli errori di spostamento dei file", "Comando")]
        public bool IgnoreErrors { get; set; } = false;

        // --- opzioni di filtraggio ---

        [Option(CliMeta.PatternMatchTypeFlag, CliMeta.PatternMatchTypeDescription, "Filtri")]
        public PatternMatchType PatternMatchType { get; set; } = PatternMatchType.Auto;

        [Option("ignore-case|i", "Rende la ricerca case-insensitive", "Filtri")]
        public bool IgnoreCase { get; set; }

        [Option(CliMeta.HiddenFlag, CliMeta.HiddenDesc, "Filtri")]
        public bool IncludeHidden { get; set; } = false;

        [Option("pattern|p", CliMeta.FilePatternDesc, "Filtri")]
        public string? Pattern { get; set; }

        [Option(CliMeta.DateAfterFlag, CliMeta.DateAfterDesc, "Filtri")]
        public RelativeDateTime? DateAfter { get; set; }

        [Option(CliMeta.DateBeforeFlag, CliMeta.DateBeforeDesc, "Filtri")]
        public RelativeDateTime? DateBefore { get; set; }
    }
}