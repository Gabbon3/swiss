using System.IO.Enumeration;
using System.Diagnostics;
using lib.io;
using lib.io.stack;
using lib.console;
using lib.utils;
using lib.generator;
using lib.console.fastprinter;

namespace plugins.find;

#region Find DTO
/// <summary>
/// Dati estratti da StackFileInfo, pronti per essere serializzati.
/// Essendo una ref struct, garantisce zero allocazioni in heap.
/// </summary>
[FastSerializable]
public readonly ref partial struct FindResultData
{
    public ReadOnlySpan<char> Directory { get; }
    public ReadOnlySpan<char> Name { get; }
    public bool IsDirectory { get; }
    public long SizeBytes { get; }
    public long LastWriteUnixTimeMs { get; }

    public FindResultData(
        ReadOnlySpan<char> directory,
        ReadOnlySpan<char> name,
        bool isDirectory,
        long sizeBytes,
        long lastWriteUnixTimeMs)
    {
        Directory = directory;
        Name = name;
        IsDirectory = isDirectory;
        SizeBytes = sizeBytes;
        LastWriteUnixTimeMs = lastWriteUnixTimeMs;
    }
}

#endregion
#region Plugin

class FindPlugin : Plugin
{
    public override string Name => "find";
    public override string Description => "Ricerca di file tramite regex o stringhe fisse, con supporto classifiche (ranking)";

    // # Stato condiviso tra i metodi
    private FindState State = new();

    // # Stato interno
    private class FindState
    {
        public string Root = string.Empty;
        public string? Pattern;
        public bool Recurse = false;
        public bool IsRanking = false;
        public int MatchCount = 0;
        public FastWalkerCounters Counters = new();
        public Stopwatch ScanTimer = new();
        public OutputFormat Format = OutputFormat.Console;
        public FastPrinter Printer = default!;
        public PriorityQueue<StackFileInfo, long>? PriorityQueue;
        public Action<StackFileInfo>? ProcessItemStrategy;
        public Func<StackFileInfo, long>? PrioritySelector;
        public FinderOptions Config = new();
        public FileSystemFilter? FileFilter;
        public FileSystemFilter? DirectoryExcludeFilter;
    }

    private struct FinderOptions
    {
        public bool Oldest { get; set; }
        public bool Newest { get; set; }
        public bool Biggest { get; set; }
        public bool Smallest { get; set; }
        public int Limit { get; set; }
    }

    #region RunAsync

    // # ---------------------------------- #
    // RunAsync - diagramma di flusso
    // # ---------------------------------- #
    public override async Task RunAsync(string[] args, CancellationToken ct)
    {
        // 1. ottengo i valori di settings
        var settings = ParseSettings<FindSettings>(args);
        if (args.Contains("--help") || string.IsNullOrEmpty(settings.TargetPath))
        {
            Help();
            return;
        }

        // print di avvio
        if (!settings.MinimalOutput) {
            ConsolePlus.Write($"[Cyan]#[/] Avvio ricerca...\n");
        }
            
        State = new FindState();
        // 2. valido e parsifico le settings
        if (!ParseAndValidateSettings(settings, ct)) return;
        // 3. configuro le opzioni per il ranking (se richiesto)
        try { ConfigureRankingMode(settings); }
        catch (ArgumentException ex) { PrintError($"Errore di configurazione: {ex.Message}"); return; }
        catch (Exception) { throw; }
        // 4. costruisco il filtro per cercare i file (il motore vero del plugin)
        if (!BuildFilters(settings)) return;
        // 5. creo la configurazione del FastWalker
        var walkerOptions = CreateWalkerOptions(settings);
        // 6. avvio il processo principale, inizio la ricerca
        State.ScanTimer.Restart();
        await ProcessFilesAsync(walkerOptions, ct);
        State.ScanTimer.Stop();
        // 7. stampo la top n (se richiesta)
        if (State.IsRanking) PrintRankingResults();
        // 8. statistiche finali
        await State.Printer.Complete();
        if (!settings.MinimalOutput) PrintFinalSummary();
    }

    // # ---------------------------------- #
    // Metodi estratti
    // # ---------------------------------- #
    #endregion
    #region Settings
    /// <summary>
    /// Valida le impostazioni e popola root, pattern e recurse in State.
    /// </summary>
    private bool ParseAndValidateSettings(FindSettings settings, CancellationToken ct)
    {
        string? root = ParsePath(settings.TargetPath, true);
        if (root is null)
        {
            PrintError("Il percorso specificato non è valido.");
            return false;
        }

        State.Root = root;
        State.Pattern = ParseMatchPattern(settings.Pattern);
        State.Recurse = settings.RecurseSubdirectories;
        State.Config.Limit = settings.Limit;

        // # Fast Printer
        State.Format = FastPrinter.GetOutputFormat(settings.Format);

        IFastOutput printerOutput = FastPrinter.GenerateFastOutput(State.Format, settings.Silence, settings.OutputFile);

        var fastPrinterOptions = new FastPrinter.FastPrinterOptions(
            output: printerOutput,
            capacity: 10_000);

        State.Printer = new FastPrinter(fastPrinterOptions);

        State.Printer.Run(ct);

        if (State.Format == OutputFormat.Csv)
        {
            State.Printer.TryPost("Directory;Name;IsDirectory;SizeBytes;LastWriteUnixTimeMs\n");
        }

        // ---

        if (settings.Threads < 1 || settings.Threads > 128) {
            PrintError($"Il numero di threads inserito non è valido: {settings.Threads}");
            return false;
        }

        return true;
    }

    #endregion
    #region Config Ranking
    /// <summary>
    /// Configura la modalità ranking se richiesta, inizializzando PriorityQueue e i delegate.
    /// </summary>
    private void ConfigureRankingMode(FindSettings settings)
    {
        State.IsRanking = settings.Oldest || settings.Newest || settings.Biggest || settings.Smallest;

        if (State.IsRanking)
        {
            // controllo se è stato impostato un limite valido
            if (State.Config.Limit == 0)
            {
                State.Config.Limit = 10; // Imposto un limite di default se non specificato per la modalità ranking
            }
            else if (State.Config.Limit < 1) throw new ArgumentException("Il limite deve essere maggiore di 0.");

            State.PriorityQueue = new PriorityQueue<StackFileInfo, long>();
            State.Config.Oldest = settings.Oldest;
            State.Config.Newest = settings.Newest;
            State.Config.Biggest = settings.Biggest;
            State.Config.Smallest = settings.Smallest;

            if (settings.Biggest)
                State.PrioritySelector = item => item.Length;
            else if (settings.Smallest)
                State.PrioritySelector = item => -item.Length;
            else if (settings.Oldest)
                State.PrioritySelector = item => -item.LastWriteTime.Ticks;
            else if (settings.Newest)
                State.PrioritySelector = item => item.LastWriteTime.Ticks;

            State.ProcessItemStrategy = RankItem;
        }
        else
        {
            State.ProcessItemStrategy = PrintSimpleMatch;
        }
    }

    #endregion
    #region File Filter
    /// <summary>
    /// Crea il FileSystemFilter in base alle opzioni fornite per file e cartelle da escludere
    /// </summary>
    private bool BuildFilters(FindSettings settings)
    {
        // FILES
        var filterOpts = new FileFilterFactory.FilterOptions(
            Pattern: ParseMatchPattern(settings.Pattern),
            MatchType: settings.PatternMatchType,
            IgnoreCase: settings.IgnoreCase,
            DateBefore: settings.DateBefore,
            DateAfter: settings.DateAfter,
            MinSize: settings.MinSize,
            MaxSize: settings.MaxSize
        );

        try
        {
            State.FileFilter = FileFilterFactory.CreateFilter(filterOpts);
        }
        catch (ArgumentException ex)
        {
            PrintError("Il pattern fornito non è valido: " + ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            PrintError("Errore durante la creazione dei filtri per i file: " + ex.Message);
            return false;
        }

        // DIRECTORY (EXCLUDE)
        // se il pattern è vuoto non perdo tempo a provare a crearlo
        if (string.IsNullOrEmpty(settings.ExcludeDirsPattern)) return true;
        
        // filtro molto semplice fatto solo sul nome, da espandere in futuro con altri filtri magari
        var directoryFilter = new FileFilterFactory.FilterOptions(
            Pattern: settings.ExcludeDirsPattern,
            MatchType: PatternMatchType.Regex,
            MatchFullPath: true // filtro su tutto il percorso per le cartelle
        );

        try
        {
            State.DirectoryExcludeFilter = FileFilterFactory.CreateFilter(directoryFilter);
        }
        catch (ArgumentException ex)
        {
            PrintError("Il pattern di esclusione delle cartelle fornito non è valido: " + ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            PrintError("Errore durante la creazione dei filtri per l'esclusione delle cartelle: " + ex.Message);
            return false;
        }

        return true;
    }

    #endregion
    #region Config Walker
    /// <summary>
    /// Crea le opzioni per FastWalker in base alle configurazioni in State.
    /// </summary>
    private FastWalkerOptions CreateWalkerOptions(FindSettings settings)
    {
        // attributi file e cartelle
        FileAttributes attributesToSkip = FileAttributes.System;
        if (!settings.IncludeHidden) attributesToSkip |= FileAttributes.Hidden;
        // genero il filtro
        return new FastWalkerOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = attributesToSkip,
            RecurseSubdirectories = State.Recurse,
            Filter = State.FileFilter,
            DirectoryExcludeFilter = State.DirectoryExcludeFilter,
            SingleReader = true,
            ReturnDirectoriesInOutput = settings.Dirs,
            MaxDegreeOfParallelism = settings.Threads
        };
    }

    #endregion
    #region Process
    /// <summary>
    /// Avvia il walker, legge i file e li processa tramite la strategy configurata.
    /// </summary>
    /// <param name="walkerOptions">Opzioni per il walker.</param>
    /// <param name="ct">Token di cancellazione globale.</param>
    /// <returns>Task che rappresenta l'operazione asincrona.</returns>
    private async Task ProcessFilesAsync(FastWalkerOptions walkerOptions, CancellationToken ct)
    {
        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var scanToken = scanCts.Token;

        var walkerReader = FastWalker.Walk<StackFileInfo>(
            State.Root,
            (ref FileSystemEntry entry) => new StackFileInfo(ref entry),
            State.Counters,
            walkerOptions,
            scanToken
        );

        try
        {
            await foreach (var item in walkerReader.ReadAllAsync(scanToken))
            {
                State.MatchCount++;
                State.ProcessItemStrategy!(item);
                if (!State.IsRanking && State.Config.Limit > 0 && State.MatchCount >= State.Config.Limit)
                {
                    scanCts.Cancel();
                    break;
                }
                ct.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) { /* Operazione fermata manualmente */ }
        catch (Exception)
        {
            throw;
        }
    }

    #endregion
    #region Results

    /// <summary>
    /// Stampa i risultati della classifica estraendo gli elementi dalla PriorityQueue.
    /// Accede a: State.PriorityQueue, State.Config, State.MatchCount
    /// </summary>
    private void PrintRankingResults()
    {
        // Stampiamo l'intestazione solo se siamo in modalità Console
        if (State.Format == OutputFormat.Console)
        {
            ConsolePlus.Write($"\n[Yellow]Risultati classifica (Top {Math.Min(State.Config.Limit, State.MatchCount)}):[/]");
        }

        while (State.PriorityQueue!.Count > 0)
        {
            var item = State.PriorityQueue.Dequeue();
            try
            {
                if (State.Format == OutputFormat.Console)
                {
                    // Logica console, user-friendly
                    string info = State.Config.Biggest || State.Config.Smallest
                        ? $" ({Formatter.Bytes(item.Length)})"
                        : $" ({item.LastWriteTime.ToLocalTime():yyyy-MM-dd HH:mm:ss})";

                    ConsolePlus.Write($"[DarkGray]{item.AsDirectorySpan()}[Cyan]{item.AsNameSpan()}[/][Yellow]{info}[/]");
                }
                else
                {
                    // Logica strutturata (JSON/CSV) per i risultati della classifica
                    var data = new FindResultData(
                        item.AsDirectorySpan(),
                        item.AsNameSpan(),
                        item.IsDirectory,
                        item.Length,
                        new DateTimeOffset(item.LastWriteTime).ToUnixTimeMilliseconds()
                    );

                    var (owner, length) = State.Format == OutputFormat.Json
                        ? data.ToJson()
                        : data.ToCsv();

                    // Invia al Channel
                    State.Printer.Post(owner, length);
                }
            }
            finally
            {
                item.Dispose();
            }
        }
    }

    /// <summary>
    /// Stampa il riepilogo finale con il conteggio degli elementi trovati.
    /// </summary>
    private void PrintFinalSummary()
    {
        double elapsedSeconds = Math.Max(State.ScanTimer.Elapsed.TotalSeconds, 0.001);
        double filesPerSecond = State.Counters.FilesProcessed / elapsedSeconds;

        Console.WriteLine();
        ConsolePlus.WriteBoxHeader($"Ricerca completata", 40, ConsoleColor.Green);
        ConsolePlus.WriteList([
            $"Match totali: [Cyan]{State.MatchCount:N0}[/]",
            $"File processati: [Yellow]{State.Counters.FilesProcessed:N0}[/]",
            $"Cartelle processate: [Magenta]{State.Counters.DirsProcessed:N0}[/]",
            $"Throughput: [DarkGray]{filesPerSecond:N0} file/s[/]"
        ]);
        ConsolePlus.WriteHr(40);
    }

    // # ---------------------------------- #
    // Metodi di supporto (invariati nella logica)
    // # ---------------------------------- #

    /// <summary>
    /// Stampa un match semplice (non-ranking) e dispose del StackFileInfo.
    /// </summary>
    private void PrintSimpleMatch(StackFileInfo item)
    {
        using (item)
        {
            if (State.Format == OutputFormat.Console)
            {
                // Logica console originale, user-friendly e colorata
                if (item.IsDirectory)
                    ConsolePlus.Write($"[Magenta]{item.AsPathSpan()}[/]");
                else
                    ConsolePlus.Write($"[DarkGray]{item.AsDirectorySpan()}[Cyan]{item.AsNameSpan()}[/]");
            }
            else
            {
                // Logica strutturata (JSON/CSV)
                var data = new FindResultData(
                    item.AsDirectorySpan(),
                    item.AsNameSpan(),
                    item.IsDirectory,
                    item.Length,
                    new DateTimeOffset(item.LastWriteTime).ToUnixTimeMilliseconds()
                );

                // Il Source Generator ha creato questi metodi autonomamente
                var (owner, length) = State.Format == OutputFormat.Json
                    ? data.ToJson()
                    : data.ToCsv();

                // Invia al Channel
                State.Printer.Post(owner, length);
            }
        }
    }

    #endregion
    #region Rank business

    /// <summary>
    /// Inserisce un item nella PriorityQueue mantenendo solo i top N elementi.
    /// Accede a: State.PriorityQueue, State.PrioritySelector, State.Config.Limit
    /// </summary>
    private void RankItem(StackFileInfo item)
    {
        State.PriorityQueue!.Enqueue(item, State.PrioritySelector!(item));
        if (State.PriorityQueue.Count > State.Config.Limit)
        {
            State.PriorityQueue.Dequeue().Dispose();
        }
    }

    #endregion

    public override void Help() => PrintHelp<FindSettings>();
}
#endregion