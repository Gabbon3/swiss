using lib.io;

namespace plugins.count
{
    public class CountSettings
    {
        // --- ARGOMENTI FISSI ---
        [Fixed(0, "percorso", "La directory di partenza (usa '.' per la cartella corrente)")]
        public string TargetPath { get; set; } = string.Empty;

        // --- CONFIGURAZIONE ---
        [Option("directory|d", "Include anche le cartelle nel conteggio finale", "Configurazione")]
        public bool IncludeDirectory { get; set; }

        [Option(CliMeta.HiddenFlag, CliMeta.HiddenDesc, "Configurazione")]
        public bool IncludeHidden { get; set; } = false;

        [Option("recursive|r", "Scansiona e conta anche nelle sottocartelle", "Configurazione")]
        public bool Recursive { get; set; }

        [Option("ignore-case|i", "Rende la ricerca del pattern case-insensitive", "Configurazione")]
        public bool IgnoreCase { get; set; }

        [Option(CliMeta.SilenceFlag, CliMeta.SilenceDesc, "Configurazione")]
        public bool Silence { get; set; } = false;

        // --- FILTRI ---

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

        [Option(CliMeta.DirsExcludePatternFlag, CliMeta.DirsExcludePatternDesc, "Filtri")]
        public string? ExcludeDirsPattern { get; set; } = null;
    }
}