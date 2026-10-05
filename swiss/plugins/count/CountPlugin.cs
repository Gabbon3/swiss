using System.IO.Enumeration;
using lib.io;
using lib.utils;
using lib.console;

namespace plugins.count
{
    internal class CountPlugin : Plugin
    {
        private readonly struct CountEntry(bool isDirectory, long size)
        {
            public readonly bool IsDirectory { get; } = isDirectory;
            public readonly long Size { get; } = size;
        }
        public override string Name => "count";
        public override string Description => "Conta il numero di file e/o cartelle";

        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            var settings = ParseSettings<CountSettings>(args);

            // gestione Help o argomenti mancanti
            if (args.Length < 1 || args.Contains("--help") || string.IsNullOrEmpty(settings.TargetPath))
            {
                Help();
                return;
            }

            // setup del percorso root
            string root = settings.TargetPath == "." ? Directory.GetCurrentDirectory() : settings.TargetPath;

            if (!Directory.Exists(root))
            {
                PrintError($"Il percorso \"{root}\" non esiste");
                return;
            }

            // FILTRO FILES

            var filterOpts = new FileFilterFactory.FilterOptions(
                Pattern: ParseMatchPattern(settings.Pattern),
                MatchType: settings.PatternMatchType,
                IgnoreCase: settings.IgnoreCase,
                DateBefore: settings.DateBefore,
                DateAfter: settings.DateAfter,
                MinSize: settings.MinSize,
                MaxSize: settings.MaxSize
            );

            FileSystemFilter? fileFilter;
            try
            {
                fileFilter = FileFilterFactory.CreateFilter(filterOpts);
            }
            catch (Exception ex)
            {
                PrintError("Errore filtro: " + ex.Message);
                return;
            }

            // FILTRO DIRECTORY (EXCLUDE)
            // se il pattern è vuoto non perdo tempo a provare a crearlo
            FileSystemFilter? directoryFilter = null;
            if (!string.IsNullOrEmpty(settings.ExcludeDirsPattern))
            {
                // filtro molto semplice fatto solo sul nome, da espandere in futuro con altri filtri magari
                var directoryFilterOptions = new FileFilterFactory.FilterOptions(
                    Pattern: settings.ExcludeDirsPattern,
                    MatchType: PatternMatchType.Regex,
                    MatchFullPath: true // filtro su tutto il percorso per le cartelle
                );
                try
                {
                    directoryFilter = FileFilterFactory.CreateFilter(directoryFilterOptions);
                }
                catch (Exception ex)
                {
                    PrintError("Errore filtro directory: " + ex.Message);
                }
            }

            // CONFIGURAZIONE FASTWALKER

            FileAttributes attributesToSkip = FileAttributes.None;
            if (!settings.IncludeHidden) attributesToSkip = FileAttributes.Hidden;

            var fastWalkerOptions = new FastWalkerOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = settings.Recursive,
                ReturnDirectoriesInOutput = settings.IncludeDirectory,
                Filter = fileFilter,
                DirectoryExcludeFilter = directoryFilter,
                SingleReader = true,
                AttributesToSkip = attributesToSkip,
            };

            // dichiaro un oggetto di conteggio per avere traccia UI a pulsazioni del progressivo del conteggio
            var counters = new FastWalkerCounters();
            using var ctsCounterUI = new CancellationTokenSource();

            // ESECUZIONE MONITOR PROGRESSIVO UI
            Task? monitorTask = null;
            if (!settings.Silence)
            {
                monitorTask = Task.Run(async () =>
                {
                    while (!ctsCounterUI.IsCancellationRequested)
                    {
                        ConsolePlus.WriteOverwrite($"File: [Yellow]{counters.FilesProcessed:N0}[/] | Dirs: [Blue]{counters.DirsProcessed:N0}[/] | Size: [Green]{Formatter.Bytes(counters.BytesProcessed)} [DarkGray]...[/]");
                        await Task.Delay(1000, ctsCounterUI.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    }
                }, ct);
            }

            // ESECUZIONE PRINCIPALE

            FastWalker.CountResult result = await FastWalker.CountAsync(
                rootPath: root,
                options: fastWalkerOptions,
                counters: counters,
                ct: ct
            );

            if (!settings.Silence)
            {
                ctsCounterUI.Cancel();
                if (monitorTask != null) await monitorTask;
                ConsolePlus.ClearCurrentLine();
            }

            // RISULTATI FINALI

            if (ct.IsCancellationRequested)
            {
                ConsolePlus.Write($"\n[Cyan]#[/] Conteggio parziale completato:");
            }
            else
            {
                ConsolePlus.Write($"\n[Cyan]#[/] Conteggio completato:");
            }
            ConsolePlus.Write($"[Cyan]*[/] Files: [Yellow]{result.Files:N0}[/]");
            ConsolePlus.Write($"[Cyan]*[/] Dimensione: [Green]{Formatter.Bytes(result.Bytes)}[/]");
            if (settings.IncludeDirectory)
            {
                ConsolePlus.Write($"[Cyan]*[/] Cartelle: [Blue]{result.Directories:N0}[/]");
            }
            ConsolePlus.Write($"[Cyan]=[/] Totale elementi: [Magenta]{result.Files + result.Directories:N0}[/]");
        }

        public override void Help()
        {
            PrintHelp<CountSettings>();
        }
    }
}