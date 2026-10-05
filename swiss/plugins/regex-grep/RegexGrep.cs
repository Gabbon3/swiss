using System.IO.Enumeration;
using System.Threading.Channels;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;
using lib.console;
using lib.console.fastprinter;
using lib.utils;
using System.Buffers;
using lib.io;
using System.Diagnostics;

namespace plugins.regexgrep
{
    class RegexGrepPlugin : Plugin
    {
        public override string Name => "grep";
        public override string Description => "Ricerca con espressioni regolari .NET (NonBacktracking, zero-alloc)";

        // # stato condiviso tra i metodi
        private RegexGrepState State = new();
        private RegexTelemetry Telemetry = new();

        // # attributi
        private bool IgnoreCase = false;
        private StringComparison StringComparisonType = StringComparison.Ordinal;
        private bool FixedPattern = false;
        private bool CountOnly = false;
        private int MinMatchCount = 0;
        private int MaxMatchCount = -1;
        private Regex? RegexEngine;
        private FastPrinter? _fastPrinter;
        private const int MaxBoundContextSize = 50;
        private const int OutputBufferSize = 16384; // 16 KB per l'output formattato
        private const int MaxMatchSize = 400; // Limite massimo di caratteri stampabili per il singolo match
        private static readonly int FilesChannelBound = 8192;
        private const int ByteBufferSize = 65536;
        private const int CharBufferSize = 65536;
        private const int ByteOverlapSize = 4096; // circa 1365 char circa nel peggiore dei casi in UTF-8 (1 char max 3 byte)

        #region Structs

        private static readonly HashSet<string> DefaultExcludeDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", ".git", ".svn", ".hg",
            "bin", "obj",
            ".vs", ".idea", ".vscode",
            "__pycache__", ".pytest_cache",
            "dist", "build", ".next", ".nuxt",
            "vendor",
            ".cargo",
        };

        /// <summary>
        /// Struttura usata per l'enumerazione dei file
        /// </summary>
        /// <param name="entry"></param>
        private readonly struct GrepFileEntry(ref FileSystemEntry entry)
        {
            public readonly string Path { get; } = entry.ToSpecifiedFullPath();
            public readonly long Size { get; } = entry.Length;
        }

        /// <summary>
        /// classe di stato condiviso tra le classi
        /// </summary>
        private class RegexGrepState
        {
            public string Root = string.Empty;
            public string Pattern = string.Empty;
            public OutputFormat Format = OutputFormat.Console;
            public HashSet<string> ExcludeDirs = new(StringComparer.OrdinalIgnoreCase);
            public Channel<GrepFileEntry> FilesChannel = Channel.CreateBounded<GrepFileEntry>(1);
        }

        /// <summary>
        /// classe di telemetria per il comando regex
        /// </summary>
        private class RegexTelemetry
        {
            public long TotalMatchCount = 0;
            public long TotalSizeVisited = 0;
            public long TotalFileFounded = 0;
            public long TotalFileProcessed = 0;
            public long ChannelWriteSync = 0;
            public long ChannelWriteAsync = 0;
        }

        #endregion
        #region RunAsync

        /// <summary>
        /// Esecuzione comando principale
        /// </summary>
        /// <returns></returns>
        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            // 1. parsing degli argomenti da linea di comando
            var settings = ParseSettings<RegexGrepSettings>(args);
            if (args.Contains("--help") || string.IsNullOrEmpty(settings.TargetPath) || string.IsNullOrEmpty(settings.Pattern))
            {
                Help();
                return;
            }

            State = new RegexGrepState();
            Telemetry = new RegexTelemetry();
            // 2. valido i configuro stati e attributi della classe
            if (!ParseAndValidateSettings(settings)) return;
            // 3. valido e verifico il pattern di ricerca
            ValidateAndTestPattern(settings.Pattern, settings.FixedPattern);
            // 4. configuro i filtri delle directory
            ConfigureDirectoryFilters(settings);
            // 5. inizializzo il motore regex e il channel
            InitializeEngine();
            // 6. inizializzo cronometro per tracciare il tempo di esecuzione effettivo
            long startTimestamp = Stopwatch.GetTimestamp();
            // ---
            if (!settings.MinimalOutput) ConsolePlus.Write($"[Cyan]#[/] Inizio la ricerca con regex...\n[DarkGray]*\n*[/]");
            // ---
            try
            {
                // 7. avvio il fastprinter per la stampa a console concorrente
                _fastPrinter!.Run(ct);
                // 8. avvio il produttore e i consumatori
                var producerTask = RunProducerAsync(settings, ct);
                var workerTasks = StartWorkers(settings, ct);
                // 9. attendo l'esecuzione di entrambi
                await producerTask;
                await Task.WhenAll(workerTasks);
            }
            catch (OperationCanceledException) { }
            finally
            {
                // chiudo il channel del fastprinter
                await _fastPrinter!.Complete();
            }
            // 10. termine esecuzione, calcolo statistiche finali
            TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            // ---
            if (!settings.MinimalOutput)
            {
                if (CountOnly) ConsolePlus.Write("[DarkGray]*\n*[/]");
                ConsolePlus.WriteBoxHeader($"Ricerca completata", 40, ConsoleColor.Green);
                ConsolePlus.WriteList([
                    $"Match totali: [Cyan]{Telemetry.TotalMatchCount:N0}[/]",
                    $"File totali trovati: [Yellow]{Telemetry.TotalFileFounded:N0}[/]",
                    $"File totali processati: [Magenta]{Telemetry.TotalFileProcessed:N0}[/]",
                    $"Spazio totale controllato: [DarkGray]{Formatter.Bytes(Telemetry.TotalSizeVisited)}[/]",
                    $"Throughput: [Cyan]{Formatter.Throughput(Telemetry.TotalSizeVisited, elapsed.TotalSeconds)}[/]",
                    $"Scritture Channel: [DarkGray]sync {Telemetry.ChannelWriteSync} - async {Telemetry.ChannelWriteAsync}[/]"
                ]);
                ConsolePlus.WriteHr(40);
            }
        }

        #endregion
        #region Settings

        private bool ParseAndValidateSettings(RegexGrepSettings settings)
        {
            string? root = ParsePath(settings.TargetPath, true);
            if (root is null)
            {
                PrintError("Il percorso specificato non è valido.");
                return false;
            }

            if (settings.Pattern.Length == 0)
            {
                PrintError("Il pattern di ricerca non può essere vuoto.");
                return false;
            }

            if (settings.Threads < 1 || settings.Threads > 50)
            {
                PrintError("Numero di thread non valido (1 <= threads <= 50)");
                return false;
            }

            // # Configuro FastPrinter

            State.Format = FastPrinter.GetOutputFormat(settings.Format);

            IFastOutput printerOutput = FastPrinter.GenerateFastOutput(State.Format, settings.Silence, settings.OutputFile);

            var fastPrinterOptions = new FastPrinter.FastPrinterOptions(
                output: printerOutput,
                capacity: 10_000);

            _fastPrinter = new FastPrinter(fastPrinterOptions);

            // ---

            State.Root = root;
            MinMatchCount = settings.MinCount > 0 ? settings.MinCount : 1;
            MaxMatchCount = settings.MaxCount > 0 ? settings.MaxCount : -1;

            if (MaxMatchCount > 0 && MaxMatchCount < MinMatchCount)
            {
                PrintError("Parametri non validi, --max-count non puo essere minore di --min-count");
                return false;
            }

            CountOnly = settings.Count;
            IgnoreCase = settings.IgnoreCase;
            if (IgnoreCase)
            {
                StringComparisonType = StringComparison.OrdinalIgnoreCase;
            }
            FixedPattern = settings.FixedPattern;

            // stampo l'header solo per il caso del CSV
            if (State.Format == OutputFormat.Csv)
            {
                string header = CountOnly
                    ? "Path;Count\n"
                    : "Path;Line;Column;Length;PreMatch;Match;PostMatch\n";

                _fastPrinter.TryPost(header);
            }

            return true;
        }

        #endregion
        #region Pattern & Regex

        private void ValidateAndTestPattern(string pattern, bool isFixed)
        {
            // verifico che il pattern non sia vuoto
            if (string.IsNullOrEmpty(pattern))
            {
                throw new ArgumentException($"Il pattern di ricerca non puo essere vuoto o nullo: '{pattern}'");
            }
            // fixed o meno assegno il pattern
            State.Pattern = pattern;
            // se fixed non devo fare altri controlli ed esco
            if (isFixed) return;

            // Controlli pattern Regex
            try
            {
                // Test compilation con timeout per evitare blocchi
                var testOptions = RegexOptions.None;
                if (IgnoreCase) testOptions |= RegexOptions.IgnoreCase;

                // Test del pattern prima di compilare
                _ = Regex.IsMatch("test", pattern, testOptions, TimeSpan.FromMilliseconds(100));
            }
            catch (RegexParseException ex)
            {
                throw new InvalidOperationException($"Pattern regex non valido: {ex.Message}");
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"Errore nel pattern: {ex.Message}");
            }
        }

        #endregion
        #region Filters

        private void ConfigureDirectoryFilters(RegexGrepSettings settings)
        {
            State.ExcludeDirs = new HashSet<string>(DefaultExcludeDirs, StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(settings.ExcludeDirs))
            {
                foreach (var dir in settings.ExcludeDirs.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    State.ExcludeDirs.Add(dir.Trim());
                }
            }

            if (!string.IsNullOrEmpty(settings.IncludeDirs))
            {
                foreach (var dir in settings.IncludeDirs.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    State.ExcludeDirs.Remove(dir.Trim());
                }
            }
        }

        #endregion
        #region Engine

        private void InitializeEngine()
        {
            // se non cerco tramite un pattern fixed allora uso la regex, quindi la inizializzo
            if (!FixedPattern)
            {
                // configurazione regex ottimizzata:
                // - Compiled: la regex viene compilata in codice macchina
                // - NonBacktracking: la regex utilizza un DFA lineare
                var options = RegexOptions.Compiled | RegexOptions.NonBacktracking;
                if (IgnoreCase) options |= RegexOptions.IgnoreCase;

                RegexEngine = new Regex(State.Pattern, options);
            }

            // configuro il channel
            State.FilesChannel = Channel.CreateBounded<GrepFileEntry>(new BoundedChannelOptions(FilesChannelBound)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        #endregion
        #region Producer

        private async Task RunProducerAsync(RegexGrepSettings settings, CancellationToken ct)
        {
            // costruisco gli attributi dei file da skippare
            FileAttributes attributesToSkip = FileAttributes.System;
            // se NON devo controllare nei file nascosti, lo aggiungo alle caratteristiche
            if (!settings.IncludeHidden) attributesToSkip |= FileAttributes.Hidden;

            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = settings.RecurseSubdirectories,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = attributesToSkip
            };

            // # FILE FILTER

            var filterOpts = new FileFilterFactory.FilterOptions(
                // forzo il glob per semplicita
                Pattern: settings.PatternFileFilter,
                MatchType: PatternMatchType.Regex,
                DateBefore: settings.DateBefore,
                DateAfter: settings.DateAfter
            );

            FileSystemFilter? fileFilter = FileFilterFactory.CreateFilter(filterOpts);

            // # INIZIO ENUMERAZIONE

            var enumerable = new FileSystemEnumerable<GrepFileEntry>(
                State.Root,
                (ref FileSystemEntry entry) => new GrepFileEntry(ref entry),
                enumerationOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    if (entry.IsDirectory) return false;
                    if (fileFilter != null)
                    {
                        return fileFilter(ref entry);
                    }
                    return true;
                },
                ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                {
                    ReadOnlySpan<char> dirName = entry.FileName;
                    foreach (var excluded in State.ExcludeDirs)
                        if (dirName.Equals(excluded, StringComparison.OrdinalIgnoreCase))
                            return false;
                    return true;
                }
            };

            try
            {
                foreach (var grepFileEntry in enumerable)
                {
                    if (ct.IsCancellationRequested) break;
                    Telemetry.TotalFileFounded++;
                    Telemetry.ChannelWriteSync++;
                    if (!State.FilesChannel.Writer.TryWrite(grepFileEntry))
                    {
                        await State.FilesChannel.Writer.WriteAsync(grepFileEntry, ct);
                        Telemetry.ChannelWriteSync--;
                        Telemetry.ChannelWriteAsync++;
                    }
                }
            }
            finally
            {
                State.FilesChannel.Writer.Complete();
            }
        }

        #endregion
        #region Workers

        private Task[] StartWorkers(RegexGrepSettings settings, CancellationToken ct)
        {
            var workers = new Task[settings.Threads];

            for (int i = 0; i < settings.Threads; i++)
            {
                workers[i] = Task.Run(async () =>
                {
                    byte[] byteBuffer = new byte[ByteBufferSize];
                    char[] charBuffer = new char[CharBufferSize];
                    // buffer per la formattazione di output
                    char[] outputBuffer = new char[OutputBufferSize];
                    long threadMatchCount = 0;
                    long localTotalFileProcessed = 0;
                    long localTotalByteSizeVisited = 0;

                    try
                    {
                        await foreach (var entry in State.FilesChannel.Reader.ReadAllAsync(ct))
                        {
                            threadMatchCount += ProcessFile(
                                entry.Path,
                                entry.Size,
                                byteBuffer,
                                charBuffer,
                                outputBuffer,
                                ref localTotalByteSizeVisited,
                                ref localTotalFileProcessed
                            );
                        }
                    }
                    finally
                    {
                        Interlocked.Add(ref Telemetry.TotalMatchCount, threadMatchCount);
                        Interlocked.Add(ref Telemetry.TotalFileProcessed, localTotalFileProcessed);
                        Interlocked.Add(ref Telemetry.TotalSizeVisited, localTotalByteSizeVisited);
                    }
                }, ct);
            }

            return workers;
        }

        #endregion
        #region ProcessFile

        /// <summary>
        /// Processa un file convertendo UTF-8 -> UTF-16 e usando Regex.EnumerateMatches (zero-alloc).
        /// </summary>
        private long ProcessFile(
            string path,
            long fileLength,
            byte[] byteBuffer,
            char[] charBuffer,
            char[] outputBuffer,
            ref long localTotalByteSizeVisited,
            ref long localTotalFileProcessed
        )
        {
            SafeFileHandle? handle = null;
            long matchCount = 0;

            try
            {
                // apro l'handle a basso livello per bypassare l'overhead dell'I/O standard di .NET
                handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.SequentialScan);

                // stato del lettore a chunk
                long fileOffset = 0;
                int leftoverBytes = 0; // byte residui (orfani + overlap) da iniettare nel ciclo successivo
                int totalLines = 1;    // tracciamento globale delle righe '\n'
                bool isFirstChunk = true;
                int overlapChars = 0;  // char da ignorare a fine chunk per evitare match tagliati a metà

                while (fileOffset < fileLength)
                {
                    // calcolo quanti byte pescare dal disco, tenendo conto del leftover già presente in testa al buffer
                    int bytesToRead = (int)Math.Min(byteBuffer.Length - leftoverBytes, fileLength - fileOffset);
                    int bytesRead = RandomAccess.Read(handle, byteBuffer.AsSpan(leftoverBytes, bytesToRead), fileOffset);
                    if (bytesRead == 0) break;

                    int currentByteLength = bytesRead + leftoverBytes;
                    ReadOnlySpan<byte> byteSpan = byteBuffer.AsSpan(0, currentByteLength);
                    localTotalByteSizeVisited += bytesRead; // telemetria: sommo solo i byte freschi appena estratti dal disco

                    // euristica sui file binari: se rilevo un byte nullo (0x00) nel primo blocco, scarto l'intero file
                    if (isFirstChunk)
                    {
                        if (byteSpan.Contains((byte)0)) return 0;
                        isFirstChunk = false;
                        // se ho superato il primo chunk e non contiene byte nulli allora processero l'intero file
                        localTotalFileProcessed++;
                    }

                    // conversione vettorializzata UTF-8 -> UTF-16 (SIMD, zero-alloc)
                    OperationStatus status = Utf8.ToUtf16(byteSpan, charBuffer, out int bytesConsumed, out int charsWritten);

                    if (status == OperationStatus.InvalidData)
                    {
                        // il file contiene byte non conformi allo standard UTF-8 (es binari/compressi anomali), annullo
                        return 0;
                    }

                    ReadOnlySpan<char> searchSpan = charBuffer.AsSpan(0, charsWritten);

                    // definisco la "safe-zone" per la regex: escludo la coda del buffer che verrà ri-analizzata nel chunk successivo
                    int searchEndIndex = isFirstChunk ? charsWritten : charsWritten - overlapChars;
                    if (searchEndIndex < 0) searchEndIndex = charsWritten;

                    // avvio lo scan sul testo normalizzato in memoria
                    if (CountOnly)
                    {
                        matchCount += CountMatches(searchSpan, searchEndIndex);
                    }
                    else
                    {
                        matchCount += ProcessMatches(searchSpan, searchEndIndex, path, totalLines, outputBuffer);
                    }

                    // recupero i byte "orfani" (codifiche UTF-8 multi-byte spezzate dal taglio del chunk)
                    int unconsumedBytes = currentByteLength - bytesConsumed;

                    // estrazione dell'overlap: mantengo un margine di sicurezza (ByteOverlapSize) per evitare di frammentare i match
                    int overlapBytes = Math.Min(ByteOverlapSize, bytesConsumed);
                    leftoverBytes = unconsumedBytes + overlapBytes;

                    // memory shift: sposto il blocco di byte da conservare all'inizio del buffer per il prossimo giro
                    byteSpan[(currentByteLength - leftoverBytes)..].CopyTo(byteBuffer);

                    // stima conservativa dell'overlap in char per calcolare la safe-zone del prossimo chunk.
                    // nel worst-case UTF-8 un carattere occupa 3 byte
                    overlapChars = Math.Min(1024, overlapBytes / 3 + 1);

                    // aggiorno il contatore delle righe calcolando i '\n' esclusivamente sulla porzione di testo consumata
                    int charsProcessed = Math.Min(searchEndIndex, charsWritten);
                    totalLines += CountLines(searchSpan[..charsProcessed]);

                    fileOffset += bytesRead;
                }
            }
            catch (UnauthorizedAccessException) { /* ignoro silenziosamente i file di sistema bloccati */ }
            catch (IOException) { /* ignoro i file aperti in uso esclusivo da altri processi */ }
            finally
            {
                handle?.Dispose();
            }

            // validazione dei constraint numerici per la modalità count-only
            if (CountOnly)
            {
                bool satisfiesMin = matchCount >= MinMatchCount;
                bool satisfiesMax = MaxMatchCount == -1 || matchCount <= MaxMatchCount;

                if (satisfiesMin && satisfiesMax)
                {
                    PrintCountResult(path, matchCount, outputBuffer);
                }
                else
                {
                    matchCount = 0; // i limiti non sono rispettati, invalido il conteggio per questo file
                }
            }

            return matchCount;
        }

        #endregion
        #region Matching

        /// <summary>
        /// Conta i match usando EnumerateMatches (zero-alloc).
        /// Oppure usando indexof simd se --fixed attivo
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int CountMatches(ReadOnlySpan<char> span, int maxIndex)
        {
            int count = 0;
            // # Regex Classica
            if (!FixedPattern)
            {
                foreach (var match in RegexEngine!.EnumerateMatches(span))
                {
                    if (match.Index >= maxIndex) break;
                    count++;
                }
            }
            else // # IndexOf ottimizzato
            {
                int offset = 0;
                ReadOnlySpan<char> patternSpan = State.Pattern.AsSpan();

                while (true)
                {
                    // Cerca la stringa usando l'offset corrente
                    int idx = span[offset..].IndexOf(patternSpan, StringComparisonType);
                    // se non trova una mazza chiudo subito
                    if (idx < 0) break;

                    int matchIndex = offset + idx;
                    if (matchIndex >= maxIndex) break;

                    count++;
                    offset = matchIndex + patternSpan.Length;
                }
            }

            return count;
        }

        /// <summary>
        /// Processa i match ed estrae il contesto per la stampa.
        /// </summary>
        private int ProcessMatches(
            ReadOnlySpan<char> span,
            int maxIndex,
            string path,
            int chunkStartLine,
            char[] outputBuffer
        )
        {
            int count = 0;

            if (!FixedPattern)
            {
                foreach (var match in RegexEngine!.EnumerateMatches(span))
                {
                    // metodo inlinato per massime performance
                    if (!ProcessSingleMatch(span, match.Index, match.Length, maxIndex, path, chunkStartLine, outputBuffer))
                        break;
                    count++;
                }
            }
            else
            {
                int offset = 0;
                ReadOnlySpan<char> patternSpan = State.Pattern.AsSpan();

                while (true)
                {
                    int idx = span[offset..].IndexOf(patternSpan, StringComparisonType);
                    // stessa logica del count
                    if (idx < 0) break;

                    int matchIndex = offset + idx;
                    // metodo inlinato per massime performance
                    if (!ProcessSingleMatch(span, matchIndex, patternSpan.Length, maxIndex, path, chunkStartLine, outputBuffer))
                        break;

                    count++;
                    offset = matchIndex + patternSpan.Length;
                }
            }

            return count;
        }

        #endregion
        #region Process Match

        /// <summary>
        /// Wrap per il processo di un singolo match, in questo modo gestisco in maniera pulita i due cicli (con regex o meno)
        /// </summary>
        /// <param name="span"></param>
        /// <param name="matchIndex"></param>
        /// <param name="matchLength"></param>
        /// <param name="maxIndex"></param>
        /// <param name="path"></param>
        /// <param name="chunkStartLine"></param>
        /// <param name="outputBuffer"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool ProcessSingleMatch(
            ReadOnlySpan<char> span,
            int matchIndex,
            int matchLength,
            int maxIndex,
            string path,
            int chunkStartLine,
            char[] outputBuffer
        )
        {
            if (matchIndex >= maxIndex) return false;

            // 1. Calcolo Riga e Colonna
            var spanBeforeMatch = span[..matchIndex];
            int lineNumber = chunkStartLine + CountLines(spanBeforeMatch);

            // La colonna è la distanza dall'ultimo \n all'inizio del match
            int lastNewLineBeforeMatch = spanBeforeMatch.LastIndexOf('\n');
            // Se non c'è \n, siamo all'inizio del chunk. Aggiungiamo 1 per avere la colonna 1-based (umana)
            int column = matchIndex - (lastNewLineBeforeMatch != -1 ? lastNewLineBeforeMatch : -1);

            ReadOnlySpan<char> exactLeft = default;
            ReadOnlySpan<char> exactMatch = default;
            ReadOnlySpan<char> exactRight = default;

            // 2. estraggo il contesto SOLO se il match è gestibile
            if (matchLength <= MaxMatchSize)
            {
                int start = Math.Max(0, matchIndex - MaxBoundContextSize);
                int endMatch = matchIndex + matchLength;
                int end = Math.Min(endMatch + MaxBoundContextSize, span.Length);

                int preNewLine = span[start..matchIndex].LastIndexOf('\n');
                int actualStart = preNewLine != -1 ? start + preNewLine + 1 : start;

                int postNewLine = span[endMatch..end].IndexOf('\n');
                int actualEnd = postNewLine != -1 ? endMatch + postNewLine : end;
                if (actualEnd > 0 && span[actualEnd - 1] == '\r') actualEnd--;

                exactLeft = span[actualStart..matchIndex];
                exactMatch = span[matchIndex..endMatch];
                exactRight = span[endMatch..actualEnd];
            }

            // 3. Stampa o Generazione Dati
            if (State.Format == OutputFormat.Console)
            {
                var matchData = new GrepMatchData(path, exactLeft, exactMatch, exactRight, lineNumber, column, matchLength);

                // Chiamata statica diretta
                int writtenChars = GrepOutputFormatter.FormatMatchConsole(ref matchData, outputBuffer);

                if (writtenChars > 0)
                {
                    var memoryOwner = MemoryPool<char>.Shared.Rent(writtenChars);
                    outputBuffer.AsSpan(0, writtenChars).CopyTo(memoryOwner.Memory.Span);
                    _fastPrinter!.Post(memoryOwner, writtenChars);
                }
            }
            else
            {
                int pos = 0;

                int leftStart = pos; exactLeft.CopyTo(outputBuffer.AsSpan(pos)); pos += exactLeft.Length;
                var safeLeft = outputBuffer.AsSpan(leftStart, exactLeft.Length);
                TextSanitizer.SanitizeForDataFormat(safeLeft);

                int matchStart = pos; exactMatch.CopyTo(outputBuffer.AsSpan(pos)); pos += exactMatch.Length;
                var safeMatch = outputBuffer.AsSpan(matchStart, exactMatch.Length);
                TextSanitizer.SanitizeForDataFormat(safeMatch);

                int rightStart = pos; exactRight.CopyTo(outputBuffer.AsSpan(pos)); pos += exactRight.Length;
                var safeRight = outputBuffer.AsSpan(rightStart, exactRight.Length);
                TextSanitizer.SanitizeForDataFormat(safeRight);

                var matchData = new GrepMatchData(path, safeLeft, safeMatch, safeRight, lineNumber, column, matchLength);

                var (owner, length) = State.Format == OutputFormat.Json ? matchData.ToJson() : matchData.ToCsv();
                _fastPrinter!.Post(owner, length);
            }

            return true;
        }

        #endregion
        #region Process Count

        /// <summary>
        /// Stampa il conteggio match per file (modalità count-only).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PrintCountResult(string path, long fileMatchCount, char[] outputBuffer)
        {
            var countData = new GrepCountData(path, fileMatchCount);

            if (State.Format == OutputFormat.Console)
            {
                // Chiamata statica diretta
                int writtenChars = GrepOutputFormatter.FormatCountConsole(ref countData, outputBuffer);

                if (writtenChars > 0)
                {
                    var memoryOwner = MemoryPool<char>.Shared.Rent(writtenChars);
                    outputBuffer.AsSpan(0, writtenChars).CopyTo(memoryOwner.Memory.Span);
                    _fastPrinter!.Post(memoryOwner, writtenChars);
                }
            }
            else
            {
                var (owner, length) = State.Format == OutputFormat.Json
                    ? countData.ToJson()
                    : countData.ToCsv();

                _fastPrinter!.Post(owner, length);
            }
        }

        #endregion
        #region Utilities

        /// <summary>
        /// Conta le newline nello span di char (SIMD)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CountLines(ReadOnlySpan<char> span)
        {
            return span.Count('\n');
        }

        public override void Help() => PrintHelp<RegexGrepSettings>();

        #endregion
    }
}