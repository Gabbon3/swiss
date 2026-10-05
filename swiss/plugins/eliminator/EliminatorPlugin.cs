using System.IO.Enumeration;
using System.Threading.Channels;
using lib.io;
using lib.utils;
using lib.utils.span;
using lib.console;
using lib.io.stack;
using Spectre.Console;
using System.Buffers;
using lib.io.collections;

namespace plugins.eliminator
{
    class EliminatorPlugin : Plugin
    {
        public override string Name => "eliminator";
        public override string Description => "Tool avanzato per la cancellazione e l'archiviazione massiva dei file";

        // dimensione padding per prevenire false-sharing su BytesSavedList e DroppedFilesCountList
        private const int CounterStride = 8;
        // 
        private int FlushMask = 127;
        private EliminationState State = new();
        private bool _throwErrorOnDelete = true;

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
            public FileAttributes AttributesToSkip { get; set; }
            public bool DropInstant { get; set; }
            public int ThreadNumber { get; set; }
            public FileFilterFactory.FilterOptions? FileFilterOptions { get; set; }
            public FileSystemFilter? FileFilter { get; set; }

            public Channel<StackFileInfo>? WorkChannel { get; set; }
            public SpanArena<char>? DirectoryToRemove { get; set; }
            public long[] DroppedFilesCountList { get; set; } = [];
            public long[] BytesSavedList { get; set; } = [];
            public bool IsProcessing { get; set; } = true;
            public bool DropTargetPathAtEnd { get; set; } = false;
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

            ConsolePlus.Write($"[Cyan]#[/] Avvio cancellazione ... {(State.IsWhatIf ? "(DEBUG)" : "")}");

            // 3. inizializzo il task di producer
            var producerTask = CreateProducerTask(settings, ct);

            // 4. inizializzo i task dei consumer (i workers veri e propri)
            var workers = CreateWorkerTasks(ct);

            // 5. inizializzo il task per il monitor UI
            Task? monitorTask = null;
            if (!settings.Silence) monitorTask = CreateMonitorTask(ct);

            // avvio e attendo tutti i workers
            await Task.WhenAll(workers);
            State.IsProcessing = false;
            if (!settings.Silence) await monitorTask!;
            await producerTask;

            // pulizia finale
            Cleanup(settings);

            // 6. stampa statistiche finali
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

            State.TargetPath = targetPath;
            State.IsWhatIf = settings.WhatIf;
            State.IsRecursive = settings.Recursive;
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

            // Inizializza gli array per i contatori
            State.DroppedFilesCountList = new long[State.ThreadNumber * CounterStride];
            State.BytesSavedList = new long[State.ThreadNumber * CounterStride];

            _throwErrorOnDelete = !settings.IgnoreErrors;

            return true;
        }

        #endregion
        #region producer
        // # ---------------------------------- #
        // Creazione Task del Producer
        // # ---------------------------------- #

        /// <summary>
        /// Crea il task che enumera i file dal file system e li invia al canale di lavoro.
        /// Accede a: State.TargetPath, State.IsDebug, State.IsRecursive, State.FileFilter, State.WorkChannel
        /// </summary>
        private Task CreateProducerTask(EliminatorSettings settings, CancellationToken ct)
        {
            State.WorkChannel = Channel.CreateBounded<StackFileInfo>(new BoundedChannelOptions(8192)
            {
                SingleWriter = true,
                SingleReader = false
            });

            State.DirectoryToRemove = new SpanArena<char>();

            // calcolo la lunghezza della root per tagliare i percorsi relativi in sicurezza
            int rootLength = State.TargetPath.Length;
            if (!State.TargetPath.EndsWith(Path.DirectorySeparatorChar) && !State.TargetPath.EndsWith(Path.AltDirectorySeparatorChar))
            {
                rootLength++;
            }

            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = State.IsRecursive,
                BufferSize = 64 * 1024,
                AttributesToSkip = State.AttributesToSkip
            };

            IEnumerable<StackFileInfo> itemsToScan = new FileSystemEnumerable<StackFileInfo>(
                State.TargetPath,
                (ref FileSystemEntry entry) => new StackFileInfo(ref entry, true), // null char nel path alla fine con true
                enumOptions
            )
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    if (entry.IsDirectory)
                    {
                        // pushamo la cartella nello stack subito
                        ReadOnlySpan<char> dirSpan = entry.Directory;
                        ReadOnlySpan<char> nameSpan = entry.FileName;

                        // calcolo il percorso relativo
                        if (dirSpan.Length >= rootLength || (dirSpan.Length == rootLength - 1))
                        {
                            ReadOnlySpan<char> relParent = dirSpan.Length > rootLength ? dirSpan[rootLength..] : [];

                            // siamo nella root
                            if (relParent.IsEmpty)
                            {
                                State.DirectoryToRemove.Push(nameSpan);
                            }
                            else
                            {
                                // unisco genitore relativo e nome corrente usando la memoria dello stack
                                Span<char> tempPath = stackalloc char[relParent.Length + 1 + nameSpan.Length];
                                relParent.CopyTo(tempPath);
                                tempPath[relParent.Length] = Path.DirectorySeparatorChar;
                                nameSpan.CopyTo(tempPath[(relParent.Length + 1)..]);

                                State.DirectoryToRemove.Push(tempPath);
                            }
                        }
                        return false; // non pushamo mai nel channel le directory
                    }

                    // Filtraggio File
                    if (State.FileFilter != null && !State.FileFilter(ref entry))
                    {
                        return false;
                    }

                    return true;
                }
            };

            return Task.Run(async () =>
            {
                try
                {
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

                            item.Dispose(); // in debug restituisco subito all'arraypool
                        }
                        else
                        {
                            await State.WorkChannel.Writer.WriteAsync(item, ct);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { PrintError($"\n[Errore I/O]: {ex.Message}"); }
                finally
                {
                    State.WorkChannel.Writer.Complete();
                }
            }, ct);
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
                    int slot = workerId * CounterStride;

                    try
                    {
                        await foreach (var item in State.WorkChannel!.Reader.ReadAllAsync())
                        {
                            using (item)
                            {
                                totalDropped++;
                                localFlushDropped++;
                                localFlushBytes += item.Length;

                                NativeIO.DeleteFile(item.AsPathSpan(), _throwErrorOnDelete);

                                // flush dell'array
                                if ((localFlushDropped & FlushMask) == 0)
                                {
                                    long currentDropped = State.DroppedFilesCountList[slot];
                                    long currentBytes = State.BytesSavedList[slot];

                                    Volatile.Write(ref State.DroppedFilesCountList[slot], currentDropped + localFlushDropped);
                                    Volatile.Write(ref State.BytesSavedList[slot], currentBytes + localFlushBytes);

                                    localFlushDropped = 0;
                                    localFlushBytes = 0;
                                }

                                ct.ThrowIfCancellationRequested();
                            }
                        }
                    }
                    finally
                    {
                        // invio gli ultimi dati rimasti appesi
                        long currentDropped = State.DroppedFilesCountList[slot];
                        long currentBytes = State.BytesSavedList[slot];

                        Volatile.Write(ref State.DroppedFilesCountList[slot], currentDropped + localFlushDropped);
                        Volatile.Write(ref State.BytesSavedList[slot], currentBytes + localFlushBytes);
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
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    // Partiamo con un oggetto vuoto, aggiorneremo con Rows
                    await AnsiConsole.Live(new Text(""))
                        .Cropping(VerticalOverflowCropping.Bottom)
                        .StartAsync(async ctx =>
                        {
                            while (State.IsProcessing && !ct.IsCancellationRequested)
                            {
                                long currentTotalDropped = 0;
                                long currentTotalBytesSaved = 0;

                                // 1. GRID DEI WORKER (Colonne fisse per allineamento perfetto)
                                var workerGrid = new Grid()
                                    .AddColumn(new GridColumn().Width(4).NoWrap()) // T-XX
                                    .AddColumn(new GridColumn().Width(11).RightAligned().NoWrap()) // File eliminati max 999.999.999
                                    .AddColumn(new GridColumn().NoWrap().LeftAligned()); // Barra

                                for (int i = 0; i < State.ThreadNumber; i++)
                                {
                                    long totalDropped = Volatile.Read(ref State.DroppedFilesCountList[i * CounterStride]);
                                    long totalBytes = Volatile.Read(ref State.BytesSavedList[i * CounterStride]);

                                    currentTotalDropped += totalDropped;
                                    currentTotalBytesSaved += totalBytes;

                                    long currentProgress = totalDropped % 4096;
                                    int dashesCount = (int)(currentProgress / 102);
                                    string bar = new string('-', dashesCount).PadRight(40, ' ');

                                    workerGrid.AddRow(
                                        $"[yellow]T-{i:D2}[/]",
                                        $"[cyan]{totalDropped:N0}[/]",
                                        $"[grey]|[/][green]{bar}[/][grey]|[/]"
                                    );
                                }

                                // Calcoli velocità
                                double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                                double filesPerSecond = elapsedSeconds > 0
                                    ? (currentTotalDropped - lastTotalDropped) / elapsedSeconds
                                    : 0;

                                lastTotalDropped = currentTotalDropped;
                                lastTotalBytesSaved = currentTotalBytesSaved;
                                stopwatch.Restart();

                                // 2. GRID DEL SOMMARIO (Colonna singola per evitare wrap)
                                var summaryGrid = new Grid().AddColumn(new GridColumn().NoWrap());
                                summaryGrid.AddEmptyRow();

                                summaryGrid.AddRow($"[magenta]>[/] [white]Totale Eliminati :[/] [cyan]{currentTotalDropped:N0}[/]");
                                summaryGrid.AddRow($"[magenta]>[/] [white]Spazio Liberato  :[/] [magenta]{Formatter.Bytes(currentTotalBytesSaved)}[/]");

                                string fpsStr = $"{(int)filesPerSecond:N0} f/s";
                                summaryGrid.AddRow($"[magenta]>[/] [white]Velocità Attuale :[/] [green]{fpsStr}[/]");

                                // 3. UPDATE TARGET con l'unione delle due griglie
                                ctx.UpdateTarget(new Rows(workerGrid, summaryGrid));

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
            long totalDropped = 0;
            long totalBytesSaved = 0;

            for (int i = 0; i < State.ThreadNumber; i++)
            {
                totalDropped += State.DroppedFilesCountList[i * CounterStride];
                totalBytesSaved += State.BytesSavedList[i * CounterStride];
            }

            ConsolePlus.WriteHr(25);
            ConsolePlus.Write($"[Cyan]#[/] Operazione Conclusa.");
            ConsolePlus.Write($"[Cyan]*[/] File cancellati  : [Magenta]{totalDropped}[/]");
            ConsolePlus.Write($"[Cyan]*[/] Spazio coinvolto : [Cyan]{Formatter.Bytes(totalBytesSaved)}[/]");
            ConsolePlus.WriteHr(25);
        }

        #endregion
        #region cleanup
        // # ---------------------------------- #
        // Metodi secondari di Help
        // # ---------------------------------- #

        /// <summary>
        /// Elimina le cartelle presenti sullo stack e se richiesto anche la cartella sorgente
        /// </summary>
        private void Cleanup(EliminatorSettings settings)
        {
            // 0. Se non è richiesta la pulizia delle cartelle skippo
            if (settings.DropDirs == false)
            {
                State.DirectoryToRemove!.Dispose();
                return;
            }
            ConsolePlus.Write($"[Green]#[/] Pulizia finale...");
            // 1. pulizia ricorsiva dell'albero
            try
            {
                if (State.DirectoryToRemove != null && State.DirectoryToRemove.Count > 0)
                {
                    // affitto un buffer sufficientemente grande per i path
                    char[] cleanBuffer = ArrayPool<char>.Shared.Rent(4096);
                    try
                    {
                        while (State.DirectoryToRemove.TryPop(out ReadOnlySpan<char> relativeDir))
                        {
                            Span<char> workSpan = cleanBuffer.AsSpan();

                            // ricostruisco il path: Root + Relativo
                            Span<char> remaining = workSpan.PathCombine(State.TargetPath.AsSpan(), endWithSeparator: true);
                            remaining = remaining.PathCombine(relativeDir, endWithSeparator: false);

                            int finalLength = cleanBuffer.Length - remaining.Length;
                            // aggiungo direttamente il nullchar per alleggerire NativeIO
                            cleanBuffer[finalLength] = '\0';

                            // provo a cancellare, se fallisce non importa vado oltre (sono rimasti file appesi che non sono passati al filtro).
                            NativeIO.RemoveDirectory(cleanBuffer.AsSpan(0, finalLength + 1), throwOnError: false);
                        }
                    }
                    finally
                    {
                        ArrayPool<char>.Shared.Return(cleanBuffer);
                    }
                }
            }
            finally
            {
                // 2. restituisco il buffer dell'arena
                State.DirectoryToRemove!.Dispose();
            }
            // 3. rimozione cartella radice
            if (State.DropTargetPathAtEnd)
            {
                ConsolePlus.Write($"[Red]#[/] Rimuovo cartella sorgente [Yellow]{State.TargetPath}[/]...");
                NativeIO.RemoveDirectory(State.TargetPath, throwOnError: false);
            }
        }

        public override void Help()
        {
            PrintHelp<EliminatorSettings>();
        }
        #endregion
    }
}