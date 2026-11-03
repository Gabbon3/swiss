using System.IO.Enumeration;
using System.Threading.Channels;
using lib.io;
using lib.utils;
using lib.console;
using lib.io.stack;
using Spectre.Console;
using System.Diagnostics;
using lib.console.fastprinter;

namespace plugins.eliminator
{
    class EliminatorPlugin : Plugin
    {
        public override string Name => "eliminator";
        public override string Description => "Tool avanzato per la cancellazione e l'archiviazione massiva dei file";

        // Maschera per il flush dei contatori
        private const int FlushMask = 63;
        private const double WorkerChannelLength = 8192.0;
        private EliminationState State = new();

        // # Stato interno
        /// <summary>
        /// Contiene lo stato completo dell'operazione di eliminazione.
        /// Include configurazioni, canali di comunicazione e contatori di progresso.
        /// </summary>
        private class EliminationState
        {
            public string TargetPath { get; set; } = "";

            public bool IsWhatIf { get; set; }
            public EliminationStateDebug DebugInfo { get; set; } = new();
            public bool IsRecursive { get; set; }
            public bool DirsOnly { get; set; }
            public FileAttributes AttributesToSkip { get; set; }
            public bool DropInstant { get; set; }
            public int ThreadNumber { get; set; }
            public FileFilterFactory.FilterOptions? FileFilterOptions { get; set; }
            public FileSystemFilter? FileFilter { get; set; }

            public Channel<StackFileInfo>? WorkChannel { get; set; }
            public long TotalDeletedFilesCount = 0;
            public long TotalBytesSaved = 0;
            public long TotalElapsedMilliseconds = 0;
            public bool IsProcessing { get; set; } = true;
            public bool DropTargetPathAtEnd { get; set; } = false;

            public double ElapsedSeconds { get; set; } = 0.0;

            // Gestione errori
            public bool IgnoreErrors { get; set; } = false;
            public bool LogErrors { get; set; } = false;
            public long TotalErrors = 0;
            public string ErrorLogPath { get; set; } = "";
            public FastPrinter? ErrorLogger { get; set; } = null;
        }

        private class EliminationStateDebug
        {
            public long DroppedFilesCount { get; set; }
            public long BytesSaved { get; set; }
            public DateTime? OldestFileDate { get; set; }
            public DateTime? NewestFileDate { get; set; }
        }

        #region RunAsync
        // # Esecuzione Principale
        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            ConsolePlus.Write($"[DarkGreen]# Avvio Eliminator[/]\n");
            var settings = ParseSettings<EliminatorSettings>(args);

            if (args.Contains("--help") || string.IsNullOrEmpty(settings.TargetPath))
            {
                Help();
                return;
            }

            State = new EliminationState();

            // 1. parsing e validazione delle settings
            if (!ParseAndValidateSettings(settings))
            {
                return;
            }

            // 2. stampo i filtri in uso e chiedo conferma all'utente prima di procedere mostrando i filtri
            if (State.FileFilterOptions != null)
            {
                ConsolePlus.Write(State.FileFilterOptions.ToString());
            }
            ConsolePlus.Write($"\n[Red]# Confermi di voler procedere?[/]");
            if (!settings.Force)
            {
                try
                {
                    string confirm = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                    .PageSize(3)
                    .AddChoices(["No", "Si"]));
                    // controllo
                    if (string.IsNullOrEmpty(confirm) || confirm != "Si")
                    {
                        ConsolePlus.Write($"[Green]#[/] Operazione annullata.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    PrintError($"Errore durante la conferma: {ex.Message}");
                    PrintWarning("Se ti trovi su un terminale remoto prova a usare l'opzione --force|-f per bypassare la conferma.");
                    return;
                }
            }
            else
            {
                PrintWarning($"Conferma bypassata con --force|-f.");
            }

            ConsolePlus.Write($"[Cyan]#[/] Avvio cancellazione ... {(State.IsWhatIf ? "(WHAT IF)" : "")}");

            // Entro qui per fare cancellazione solo dei file
            if (!State.DirsOnly)
            {
                // traccio il tempo impiegato
                var stopwatch = Stopwatch.StartNew();
                var producerTask = CreateProducerTask(settings, ct);
                var workers = CreateWorkerTasks(ct);
                Task? monitorTask = null;
                if (!settings.Silence) monitorTask = CreateMonitorTask(ct);

                try
                {
                    await Task.WhenAll(workers);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                }
                finally
                {
                    State.IsProcessing = false;
                    if (State.ErrorLogger != null)
                    {
                        await State.ErrorLogger.Complete();
                    }
                }

                if (monitorTask != null)
                {
                    try
                    {
                        await monitorTask;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                    }
                }

                await producerTask;
                // fermo il cronometro
                stopwatch.Stop();
                State.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            // Entro qui solo per la pulizia delle cartelle vuote invece
            bool targetWouldBeEmpty;
            try
            {
                targetWouldBeEmpty = CleanupSubDirectories(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            // Cancello la cartella root se richiesto (flag --drop-source)
            CleanUpRootFolder(targetWouldBeEmpty, ct);

            // stampa statistiche finali
            PrintFinalStatistics();
        }

        #endregion
        #region parsing
        // # ---------------------------------- #
        // Parsing e validazione Settings
        // # ---------------------------------- #

        /// <summary>
        /// Analizza e valida i parametri di input.
        /// Accede a: State (per popolarlo)
        /// </summary>
        private bool ParseAndValidateSettings(EliminatorSettings settings)
        {
            string? targetPath = ParsePath(settings.TargetPath);
            if (string.IsNullOrEmpty(targetPath))
                return false;

            FileAttributes targetAttributes;
            try
            {
                targetAttributes = File.GetAttributes(targetPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                PrintError($"Impossibile accedere al percorso target '{targetPath}': {ex.Message}");
                return false;
            }

            if ((targetAttributes & FileAttributes.ReparsePoint) != 0)
            {
                PrintError($"Il percorso target non può essere un collegamento: {targetPath}");
                return false;
            }

            State.TargetPath = targetPath;
            State.IsWhatIf = settings.WhatIf;
            State.IsRecursive = settings.Recursive;
            State.DirsOnly = settings.DirsOnly;
            State.ThreadNumber = settings.Threads ?? Environment.ProcessorCount;

            State.AttributesToSkip = FileAttributes.System;
            if (!settings.IncludeHidden) State.AttributesToSkip |= FileAttributes.Hidden;

            State.FileFilterOptions = new FileFilterFactory.FilterOptions(
                Pattern: ParseMatchPattern(settings.Pattern),
                MatchType: settings.PatternMatchType,
                IgnoreCase: settings.IgnoreCase,
                DateBefore: settings.DateBefore,
                DateAfter: settings.DateAfter,
                MinSize: settings.MinSize,
                MaxSize: settings.MaxSize
            );

            State.FileFilter = FileFilterFactory.CreateFilter(State.FileFilterOptions);

            State.DropTargetPathAtEnd = settings.DropSource;

            // Inizializza i contatori
            State.TotalDeletedFilesCount = 0;
            State.TotalBytesSaved = 0;

            // gestione degli errori
            State.IgnoreErrors = settings.IgnoreErrors;
            State.LogErrors = settings.LogErrors;
            // se voglio tracciare gli errori allora inizializzo l'ErrorLogger
            // ignore errors deve essere disattivato per poter tracciare gli errori
            if (State.LogErrors && !State.IgnoreErrors)
            {
                State.ErrorLogPath = Path.Combine(Path.GetTempPath(), "swiss", "eliminator", $"eliminator_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                var output = FastPrinter.GenerateFastOutput(OutputFormat.Json, true, State.ErrorLogPath);
                var options = new FastPrinter.FastPrinterOptions(
                    output: output,
                    singleWriter: false,
                    capacity: 1000
                );
                State.ErrorLogger = new FastPrinter(options);
            }

            return true;
        }

        #endregion
        #region producer
        // # ---------------------------------- #
        // Creazione Task del Producer
        // # ---------------------------------- #

        /// <summary>
        /// Crea il task che enumera i file dal file system e li invia al canale di lavoro.
        /// Applica due logiche di scansione: FastWalker per scansioni multi-thread ad alte prestazioni, e FileSystemEnumerable per scansioni singolo thread.
        /// </summary>
        private Task CreateProducerTask(EliminatorSettings settings, CancellationToken ct)
        {
            State.WorkChannel = Channel.CreateBounded<StackFileInfo>(new BoundedChannelOptions((int)WorkerChannelLength)
            {
                SingleWriter = true,
                SingleReader = false
            });

            return Task.Run(async () =>
            {
                try
                {
                    // Selezione della strategia di scansione
                    bool isNetworkPath = IsNetworkPath(State.TargetPath);
                    // ROOT: uso FastWalker per scansione multi-thread se mi trovo in un path di rete
                    //       calcolando il numero di threads per la scansione come 1/4 del totale dei threads di cancellazione per ottenere equilibrio
                    if (State.IsRecursive && isNetworkPath)
                    {
                        int scanThreads = State.ThreadNumber / 4;
                        var walkerOptions = new FastWalkerOptions
                        {
                            MaxDegreeOfParallelism = scanThreads,
                            BufferSize = 64 * 1024,
                            AttributesToSkip = State.AttributesToSkip,
                            RecurseSubdirectories = State.IsRecursive,
                            Filter = State.FileFilter,
                            ReturnDirectoriesInOutput = false
                        };

                        var itemsToScan = FastWalker.Walk<StackFileInfo>(
                            State.TargetPath,
                            (ref FileSystemEntry entry) => new StackFileInfo(ref entry, true),
                            walkerOptions,
                            ct);

                        await foreach (var item in itemsToScan.ReadAllAsync(ct))
                        {
                            if (State.IsWhatIf)
                            {
                                State.DebugInfo.DroppedFilesCount++;
                                State.DebugInfo.BytesSaved += item.Length;

                                DateTime fileDate = item.LastWriteTime;

                                if (!State.DebugInfo.OldestFileDate.HasValue || fileDate < State.DebugInfo.OldestFileDate.Value)
                                {
                                    State.DebugInfo.OldestFileDate = fileDate;
                                }

                                if (!State.DebugInfo.NewestFileDate.HasValue || fileDate > State.DebugInfo.NewestFileDate.Value)
                                {
                                    State.DebugInfo.NewestFileDate = fileDate;
                                }

                                item.Dispose();
                            }
                            else
                            {
                                await State.WorkChannel.Writer.WriteAsync(item, ct);
                            }
                        }
                    }
                    else // FALLBACK: uso FileSystemEnumerable per scansione singolo thread se il numero di threads di cancellazione è basso o se non è richiesta la ricorsione
                    {
                        var enumOptions = new EnumerationOptions
                        {
                            IgnoreInaccessible = true,
                            RecurseSubdirectories = State.IsRecursive,
                            BufferSize = 64 * 1024,
                            AttributesToSkip = State.AttributesToSkip
                        };

                        IEnumerable<StackFileInfo> itemsToScan = new FileSystemEnumerable<StackFileInfo>(
                            State.TargetPath,
                            (ref FileSystemEntry entry) => new StackFileInfo(ref entry, true),
                            enumOptions)
                        {
                            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                            {
                                if (entry.IsDirectory) return false;
                                return State.FileFilter == null || State.FileFilter(ref entry);
                            },
                            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                                (entry.Attributes & FileAttributes.ReparsePoint) == 0
                        };

                        foreach (var item in itemsToScan)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (State.IsWhatIf)
                            {
                                State.DebugInfo.DroppedFilesCount++;
                                State.DebugInfo.BytesSaved += item.Length;

                                DateTime fileDate = item.LastWriteTime;

                                if (!State.DebugInfo.OldestFileDate.HasValue || fileDate < State.DebugInfo.OldestFileDate.Value)
                                {
                                    State.DebugInfo.OldestFileDate = fileDate;
                                }

                                if (!State.DebugInfo.NewestFileDate.HasValue || fileDate > State.DebugInfo.NewestFileDate.Value)
                                {
                                    State.DebugInfo.NewestFileDate = fileDate;
                                }

                                item.Dispose();
                            }
                            else
                            {
                                await State.WorkChannel.Writer.WriteAsync(item, ct);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { PrintError($"\n[Errore I/O]: {ex.Message}"); }
                finally
                {
                    State.WorkChannel.Writer.Complete();
                }
            });
        }

        #endregion
        #region worker
        // # ---------------------------------- #
        // Creazione dei Task[] dei Workers
        // # ---------------------------------- #

        /// <summary>
        /// Crea i task worker che consumano i file dal canale e li eliminano/archiviano.
        /// Accede a: State.ThreadNumber, State.DropInstant, State.DroppedFilesCountList, 
        ///           State.BytesSavedList, State.WorkChannel, GlobalTrashPath
        /// </summary>
        private Task[] CreateWorkerTasks(CancellationToken ct)
        {
            var workers = new Task[State.ThreadNumber];

            for (int i = 0; i < State.ThreadNumber; i++)
            {
                int workerId = i;
                workers[i] = Task.Run(async () =>
                {

                    long localFlushDropped = 0;
                    long localFlushBytes = 0;
                    long totalDropped = 0;

                    try
                    {
                        await foreach (var item in State.WorkChannel!.Reader.ReadAllAsync(ct))
                        {
                            using (item)
                            {
                                try
                                {
                                    NativeIO.DeleteFile(item.AsPathSpan());
                                    totalDropped++;
                                    localFlushDropped++;
                                    localFlushBytes += item.Length;
                                    // flush dei contatori
                                    if ((localFlushDropped & FlushMask) == 0)
                                    {
                                        // aggiorno i contatori globali
                                        Interlocked.Add(ref State.TotalDeletedFilesCount, localFlushDropped);
                                        Interlocked.Add(ref State.TotalBytesSaved, localFlushBytes);

                                        localFlushDropped = 0;
                                        localFlushBytes = 0;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    // conteggio sempre il numero di errori e traccio sul file di log temporaneo solo se richiesto
                                    Interlocked.Increment(ref State.TotalErrors);
                                    State.ErrorLogger?.Post($"{item.AsPathSpan()} - {ex.Message}");
                                    // se non voglio ignorare gli errori, rilancio l'eccezione
                                    if (!State.IgnoreErrors) throw;
                                }

                                ct.ThrowIfCancellationRequested();
                            }
                        }
                    }
                    finally
                    {
                        // invio gli ultimi dati rimasti appesi
                        Interlocked.Add(ref State.TotalDeletedFilesCount, localFlushDropped);
                        Interlocked.Add(ref State.TotalBytesSaved, localFlushBytes);
                    }
                }, ct);
            }

            return workers;
        }

        #endregion
        #region monitor
        // # ---------------------------------- #
        // Creazione del Monitor UI
        // # ---------------------------------- #

        /// <summary>
        /// Crea il task che monitora e visualizza il progresso in tempo reale usando Spectre.Console.
        /// Accede a: State.ThreadNumber, State.IsDebug, State.DroppedFilesCountList, 
        ///           State.IsProcessing
        /// </summary>
        private Task CreateMonitorTask(CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                if (State.IsWhatIf)
                {
                    // Modalità DEBUG: schermata semplice con DroppedFilesCountDebug e BytesSavedDebug
                    try
                    {
                        await AnsiConsole.Live(new Text(""))
                            .Cropping(VerticalOverflowCropping.Bottom)
                            .StartAsync(async ctx =>
                            {
                                while (State.IsProcessing && !ct.IsCancellationRequested)
                                {
                                    var debugGrid = new Grid().AddColumn(new GridColumn().NoWrap());
                                    debugGrid.AddEmptyRow();
                                    debugGrid.AddRow($"File da Eliminare        : [cyan]{State.DebugInfo.DroppedFilesCount:N0}[/]");
                                    debugGrid.AddRow($"Spazio Salvato           : [magenta]{Formatter.Bytes(State.DebugInfo.BytesSaved)}[/]");
                                    debugGrid.AddRow($"File piu vecchio         : [yellow]{State.DebugInfo.OldestFileDate:dd.MM.yyyy hh:mm}[/]");
                                    debugGrid.AddRow($"File piu recente         : [yellow]{State.DebugInfo.NewestFileDate:dd.MM.yyyy hh:mm}[/]");

                                    ctx.UpdateTarget(debugGrid);
                                    await Task.Delay(250, ct);
                                }
                            });
                    }
                    catch (TaskCanceledException) { }
                    return;
                }

                long lastTotalDropped = 0;
                long lastTotalBytesSaved = 0;
                var stopwatch = Stopwatch.StartNew();

                try
                {
                    await AnsiConsole.Live(new Text(""))
                        .Cropping(VerticalOverflowCropping.Bottom)
                        .StartAsync(async ctx =>
                        {
                            while (State.IsProcessing && !ct.IsCancellationRequested)
                            {
                                // uso volatile read per evitare di stazionare i valori in cache e non vederli piu aggiornarsi
                                long currentTotalFilesDeleted = Volatile.Read(ref State.TotalDeletedFilesCount);
                                long currentTotalBytesSaved = Volatile.Read(ref State.TotalBytesSaved);
                                long errors = Volatile.Read(ref State.TotalErrors);

                                // -- Calcolo i file al secondo
                                double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                                double filesPerSecond = elapsedSeconds > 0
                                    ? (currentTotalFilesDeleted - lastTotalDropped) / elapsedSeconds
                                    : 0;
                                // -- Calcolo i ms medi per file cancellato
                                // -- Calcolo i ms medi per file (Derivato direttamente dalle f/s!)
                                // Se filesPerSecond > 0 calcoliamo 1000 / f/s, altrimenti 0 ms
                                double avgMsDeletedFile = filesPerSecond > 0
                                    ? 1000.0 / filesPerSecond
                                    : 0.0;
                                // --- Aggiorno i totali precedenti per fare i successivi calcoli
                                lastTotalDropped = currentTotalFilesDeleted;
                                lastTotalBytesSaved = currentTotalBytesSaved;
                                stopwatch.Restart();
                                // ---
                                // Griglia di sommario
                                var uiGrid = new Grid().AddColumn(new GridColumn().NoWrap());
                                uiGrid.AddEmptyRow();

                                uiGrid.AddRow($"[magenta]>[/] [white]Totale Eliminati  :[/] [cyan]{currentTotalFilesDeleted:N0}[/]");
                                uiGrid.AddRow($"[magenta]>[/] [white]Spazio Liberato   :[/] [magenta]{Formatter.Bytes(currentTotalBytesSaved)}[/]");

                                string fpsStr = $"{(int)filesPerSecond:N0} f/s";
                                uiGrid.AddRow($"[magenta]>[/] [white]Velocità Attuale  :[/] [green]{fpsStr}[/]");
                                uiGrid.AddRow($"[magenta]>[/] [white]File in errore    :[/] [red]{errors:N0}[/]");

                                // -- Dati Tecnici (allo user non frega nulla ma a me si quindi li metto)
                                int currentQueueLength = State.WorkChannel?.Reader.Count ?? 0;
                                double queuePercentage = currentQueueLength / WorkerChannelLength * 100;
                                uiGrid.AddRow($"[magenta]>[/] [white]Carico canale     :[/] [gray]{queuePercentage:F1}%[/]");
                                uiGrid.AddRow($"[magenta]>[/] [white]Rate cancellazione:[/] [gray]{avgMsDeletedFile:N2} ms/file[/]");

                                // -- Rendering della griglia
                                ctx.UpdateTarget(new Rows(uiGrid));

                                // -- attendo 250ms tra un rendering e l'altro
                                await Task.Delay(250, ct);
                            }
                        });
                }
                catch (TaskCanceledException) { }
            }, ct);
        }

        #endregion
        #region stats
        // # ---------------------------------- #
        // Stampa delle statistiche finali
        // # ---------------------------------- #

        /// <summary>
        /// Calcola e stampa le statistiche finali dell'operazione.
        /// Accede a: State.ThreadNumber, State.DroppedFilesCountList, State.BytesSavedList
        /// </summary>
        private void PrintFinalStatistics()
        {
            ConsolePlus.WriteHr(32);
            if (State.IsWhatIf)
            {
                ConsolePlus.Write($"[Cyan]#[/] Operazione Conclusa.");
                ConsolePlus.Write($"[Cyan]*[/] File che verrebbero cancellati  : [Cyan]{State.DebugInfo.DroppedFilesCount}[/]");
                ConsolePlus.Write($"[Cyan]*[/] Spazio che verrebbe liberato    : [Magenta]{Formatter.Bytes(State.DebugInfo.BytesSaved)}[/]");
            }
            else
            {
                ConsolePlus.Write($"[Cyan]#[/] Operazione Conclusa.");
                ConsolePlus.Write($"[Cyan]*[/] File cancellati  : [Cyan]{State.TotalDeletedFilesCount}[/]");
                ConsolePlus.Write($"[Cyan]*[/] Spazio coinvolto : [Magenta]{Formatter.Bytes(State.TotalBytesSaved)}[/]");
                // Stampo la media dei file cancellati al secondo solo se sto cancellando i files
                // quindi non sto cancellando solo le cartelle
                if (!State.DirsOnly)
                {
                    ConsolePlus.Write($"[Cyan]*[/] Media files/sec  : [DarkGray]{State.TotalDeletedFilesCount / Math.Max(1, State.ElapsedSeconds):N2} f/s[/]");
                }
                // stampo percorso del file di log degli errori se presente
                if (State.ErrorLogger != null)
                {
                    ConsolePlus.Write($"[Cyan]*[/] File di log errori : [Gray]{State.ErrorLogPath}[/]");
                }
            }
            ConsolePlus.WriteHr(32);
        }

        #endregion
        #region cleanup
        // # ---------------------------------- #
        // Metodi secondari di Help
        // # ---------------------------------- #

        private readonly record struct DirectoryEntryToPrune(
            string Path,
            bool IsDirectory,
            bool MatchesFilter,
            bool IsReparsePoint);

        /// <summary>
        /// Rimuove ricorsivamente le sottocartelle vuote dopo la fase di elaborazione dei file.
        /// In modalità WhatIf simula la rimozione e stampa i percorsi che verrebbero cancellati.
        /// </summary>
        /// <returns>Indica se la directory target risulterebbe vuota dopo la pulizia.</returns>
        private bool CleanupSubDirectories(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (!State.DirsOnly)
            {
                return false;
            }

            if (!State.IsWhatIf)
                ConsolePlus.Write($"[Green]#[/] Pulizia finale...");

            return PruneEmptyDirectories(State.TargetPath, isRoot: true, matchesFilter: true, ct);
        }

        /// <summary>
        /// Visita l'albero in post-order e rimuove, o in WhatIf segnala, le directory vuote filtrate.
        /// </summary>
        /// <param name="currentDir">Directory da visitare.</param>
        /// <param name="isRoot">Indica se la directory corrente è la radice target, non rimossa da questo metodo.</param>
        /// <param name="matchesFilter">Indica se la directory corrente supera il filtro.</param>
        /// <param name="ct">Token che interrompe la pulizia e la preview.</param>
        /// <returns>
        /// Per la radice indica se risulterebbe vuota; per le sottocartelle indica se è stata o verrebbe rimossa.
        /// </returns>
        private bool PruneEmptyDirectories(
            string currentDir,
            bool isRoot,
            bool matchesFilter,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                BufferSize = 64 * 1024,
                AttributesToSkip = State.AttributesToSkip
            };

            var directories = new FileSystemEnumerable<DirectoryEntryToPrune>(
                currentDir,
                (ref FileSystemEntry entry) =>
                {
                    bool matchesFilter = entry.IsDirectory
                        ? State.FileFilter == null || State.FileFilter(ref entry)
                        : State.IsWhatIf && !State.DirsOnly &&
                          (State.FileFilter == null || State.FileFilter(ref entry));
                    string path = Path.Combine(entry.Directory.ToString(), entry.FileName.ToString());
                    return new DirectoryEntryToPrune(
                        path,
                        entry.IsDirectory,
                        matchesFilter,
                        (entry.Attributes & FileAttributes.ReparsePoint) != 0);
                },
                enumOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => true
            };

            bool hasRemainingEntries = false;
            foreach (var directory in directories)
            {
                ct.ThrowIfCancellationRequested();

                if (!directory.IsDirectory)
                {
                    // In WhatIf i file selezionati saranno eliminati, mentre in DirsOnly o se filtrati resteranno.
                    if (!State.IsWhatIf || State.DirsOnly || !directory.MatchesFilter)
                        hasRemainingEntries = true;
                }
                else if (directory.IsReparsePoint)
                    hasRemainingEntries = true;
                else
                {
                    bool removed = PruneEmptyDirectories(
                        directory.Path,
                        isRoot: false,
                        matchesFilter: directory.MatchesFilter,
                        ct);
                    if (!removed)
                        hasRemainingEntries = true;
                }
            }

            ct.ThrowIfCancellationRequested();

            if (isRoot)
                return !hasRemainingEntries;

            if (hasRemainingEntries || !matchesFilter)
                return false;

            if (State.IsWhatIf)
            {
                ConsolePlus.Write($"[Yellow]{currentDir}[/]");
                return true;
            }

            return NativeIO.RemoveDirectory(currentDir, throwOnError: false);
        }

        /// <summary>
        /// Rimuove la directory target alla fine dell'operazione, oppure ne mostra la preview in WhatIf.
        /// </summary>
        /// <param name="targetWouldBeEmpty">Indica se la radice risulterebbe vuota al termine della pulizia.</param>
        private void CleanUpRootFolder(bool targetWouldBeEmpty, CancellationToken ct)
        {
            if (ct.IsCancellationRequested || !State.DirsOnly || !State.DropTargetPathAtEnd)
            {
                return;
            }

            if (State.IsWhatIf)
            {
                if (targetWouldBeEmpty)
                    ConsolePlus.Write($"[Green]#[/] Verrebbe cancellata la cartella [Yellow]{State.TargetPath}[/]");
                return;
            }

            ConsolePlus.Write($"[Red]#[/] Rimuovo cartella sorgente [Yellow]{State.TargetPath}[/]...");
            NativeIO.RemoveDirectory(State.TargetPath, throwOnError: false);
        }

        public override void Help()
        {
            PrintHelp<EliminatorSettings>();
        }
        #endregion
        #region Helpers
        /// <summary>
        /// Verifica se un percorso è di rete
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        private static bool IsNetworkPath(string path)
        {
            // check veloce
            if (path.StartsWith(@"\\")) return true;

            // controllo disco se è di rete
            string? root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root))
            {
                try
                {
                    var driveInfo = new DriveInfo(root);
                    return driveInfo.DriveType == DriveType.Network;
                }
                catch
                { // se fallisce sticazzi presuppongo che non sia un path di rete
                }
            }
            return false;
        }
        #endregion
    }
}