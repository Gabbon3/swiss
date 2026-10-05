using System.Buffers;
using System.Diagnostics;
using System.IO.Enumeration;
using lib.console;
using lib.io;
using lib.io.stack;
using lib.utils;
using lib.utils.span;

namespace plugins.move;

class MovePlugin : Plugin
{
    #region metadata

    public override string Name => "move";
    public override string Description => "Tool per lo spostamento massivo di file e cartelle";

    private MoveState State = new();
    private readonly List<MoveException> Errors = [];

    #endregion

    #region state

    private struct MoveException(string section, Exception ex)
    {
        public string Section { get; set; } = section;
        public string Message { get; set; } = $"{ex.Message} in {ex.Source}";
    }

    private class MoveState
    {
        public string SourcePath { get; set; } = string.Empty;
        public string DestinationPath { get; set; } = string.Empty;

        public bool IsWhatIf { get; set; }
        public bool IsRecursive { get; set; }
        public bool Overwrite { get; set; }
        public bool IgnoreErrors { get; set; }
        public bool IsSilent { get; set; }

        public FileAttributes AttributesToSkip { get; set; }
        public FileSystemFilter? FileFilter { get; set; }

        public long MovedFilesCount { get; set; }
        public long BytesMoved { get; set; }
        public int DirectoryCreated { get; set; }
        public double DirectoryCreationMs { get; set; }

        public long LastProgressTickMs { get; set; }
        public bool ProgressPrinted { get; set; }
        public long LastRateTickMs { get; set; }
        public long LastRateFilesCount { get; set; }
        public double CurrentFilesPerSecond { get; set; }

        public int SourceRootLength { get; set; }
    }

    #endregion

    #region run

    /// <summary>
    /// avvio il comando: leggo gli argomenti, preparo lo stato, eseguo lo spostamento e stampo il riepilogo finale.
    /// </summary>
    /// <param name="args">ricevo gli argomenti cli passati dall'utente.</param>
    /// <param name="ct">uso il token per annullare l'operazione in modo cooperativo.</param>
    public override async Task RunAsync(string[] args, CancellationToken ct)
    {
        var settings = ParseSettings<MoveSettings>(args);

        if (args.Contains("--help") || string.IsNullOrEmpty(settings.SourcePath) || string.IsNullOrEmpty(settings.DestinationPath))
        {
            Help();
            return;
        }

        State = new MoveState();
        Errors.Clear();

        if (!ParseAndValidateSettings(settings)) return;

        long nowMs = Environment.TickCount64;
        State.LastProgressTickMs = nowMs;
        State.LastRateTickMs = nowMs;
        State.LastRateFilesCount = 0;

        ConsolePlus.Write($"[Cyan]#[/] Avvio spostamento verso [Yellow]{State.DestinationPath}[/] ... {(State.IsWhatIf ? "(DEBUG)" : "")}");

        await RunMoveSingleThread(ct);

        if (!State.IsSilent && State.ProgressPrinted)
        {
            Console.WriteLine();
        }

        PrintFinalStatistics();
    }

    #endregion

    #region settings

    /// <summary>
    /// valido sorgente/destinazione, preparo i filtri e inizializzo i flag di runtime.
    /// blocco anche il caso in cui la destinazione ricade dentro la sorgente.
    /// </summary>
    /// <param name="settings">ricevo le impostazioni parseate dalla cli.</param>
    /// <returns>restituisco true se la configurazione è valida e pronta all'esecuzione.</returns>
    private bool ParseAndValidateSettings(MoveSettings settings)
    {
        string? sourcePath = ParsePath(settings.SourcePath, checkPath: true);
        if (string.IsNullOrEmpty(sourcePath)) return false;

        string? destPath = ParsePath(settings.DestinationPath, checkPath: false);
        if (string.IsNullOrEmpty(destPath)) return false;

        string normalizedSourcePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        string normalizedDestPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destPath));

        bool samePath = normalizedDestPath.Equals(normalizedSourcePath, StringComparison.OrdinalIgnoreCase);
        bool isSubDirectory = normalizedDestPath.StartsWith(normalizedSourcePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || normalizedDestPath.StartsWith(normalizedSourcePath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        if (samePath || isSubDirectory)
        {
            PrintError("La destinazione non può essere una sottocartella della cartella di origine.");
            return false;
        }

        State.SourcePath = normalizedSourcePath;
        State.DestinationPath = normalizedDestPath;
        State.IsWhatIf = settings.WhatIf;
        State.IsRecursive = settings.Recursive;
        State.Overwrite = settings.Overwrite;
        State.IgnoreErrors = settings.IgnoreErrors;
        State.IsSilent = settings.Silence;

        State.AttributesToSkip = FileAttributes.System;
        if (!settings.IncludeHidden) State.AttributesToSkip |= FileAttributes.Hidden;

        var filterOpts = new FileFilterFactory.FilterOptions(
            Pattern: ParseMatchPattern(settings.Pattern),
            MatchType: settings.PatternMatchType,
            IgnoreCase: settings.IgnoreCase,
            DateBefore: settings.DateBefore,
            DateAfter: settings.DateAfter
        );

        try
        {
            State.FileFilter = FileFilterFactory.CreateFilter(filterOpts);
        }
        catch (ArgumentException ex)
        {
            PrintError($"Il pattern fornito non è valido: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            PrintError($"Errore durante la creazione dei filtri: {ex.Message}");
            return false;
        }

        if (!State.IsWhatIf && !Directory.Exists(State.DestinationPath))
        {
            try
            {
                Directory.CreateDirectory(State.DestinationPath);
            }
            catch (Exception ex)
            {
                PrintError($"Impossibile creare la directory di destinazione: {ex.Message}");
                return false;
            }
        }

        State.SourceRootLength = State.SourcePath.Length;
        if (!State.SourcePath.EndsWith(Path.DirectorySeparatorChar) && !State.SourcePath.EndsWith(Path.AltDirectorySeparatorChar))
        {
            State.SourceRootLength++;
        }

        return true;
    }

    #endregion

    #region execution

    /// <summary>
    /// eseguo il move in single-thread usando filesystemenumerable come producer diretto.
    /// per ogni file applico filtri, preparo directory di destinazione in lazy creation e poi sposto l'elemento.
    /// </summary>
    /// <param name="ct">uso il token per interrompere rapidamente enumerazione e move.</param>
    private Task RunMoveSingleThread(CancellationToken ct)
    {
        string? lastProcessedSourceDir = null;
        char[] directoryBuffer = ArrayPool<char>.Shared.Rent(4096);
        char[] destinationBuffer = ArrayPool<char>.Shared.Rent(4096);

        try
        {
            #region producer
            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = State.IsRecursive,
                BufferSize = 64 * 1024,
                AttributesToSkip = State.AttributesToSkip
            };

            var entries = new FileSystemEnumerable<StackFileInfo>(
                State.SourcePath,
                (ref FileSystemEntry entry) => new StackFileInfo(ref entry),
                enumOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    ct.ThrowIfCancellationRequested();

                    if (entry.IsDirectory) return false;
                    if (State.FileFilter != null && !State.FileFilter(ref entry)) return false;

                    if (!State.IsWhatIf)
                    {
                        ReadOnlySpan<char> currentSourceDir = entry.Directory;

                        // confronto la directory corrente con l'ultima processata:
                        // quando cambia, creo una sola volta la destinazione relativa e la riuso per i file successivi.
                        if (lastProcessedSourceDir == null ||
                            currentSourceDir.Length != lastProcessedSourceDir.Length ||
                            !currentSourceDir.SequenceEqual(lastProcessedSourceDir.AsSpan()))
                        {
                            if (State.IsRecursive && currentSourceDir.Length > State.SourceRootLength)
                            {
                                EnsureDestinationDirectoryLazy(currentSourceDir, directoryBuffer);
                            }

                            lastProcessedSourceDir = currentSourceDir.ToString();
                        }
                    }

                    return true;
                }
            };

            #endregion
            #region consumer

            foreach (var item in entries)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (State.IsWhatIf)
                    {
                        ConsolePlus.Write($"[DarkGray]{item.AsDirectorySpan()}[Cyan]{item.AsNameSpan()}[/]");
                    }
                    else
                    {
                        MoveSingleItem(item, destinationBuffer);
                    }
                }
                catch (Exception ex)
                {
                    if (!HandleMoveError(ex)) throw;
                }
                finally
                {
                    item.Dispose();
                }

                PrintProgressIfNeeded();
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(directoryBuffer);
            ArrayPool<char>.Shared.Return(destinationBuffer);
        }

        #endregion

        return Task.CompletedTask;
    }

    /// <summary>
    /// creo la cartella di destinazione solo quando mi serve davvero.
    /// compongo il path con span per ridurre allocazioni durante i passaggi più caldi.
    /// </summary>
    /// <param name="currentSourceDir">ricevo la directory sorgente del file corrente.</param>
    /// <param name="directoryBuffer">riuso questo buffer per costruire il path di destinazione.</param>
    private void EnsureDestinationDirectoryLazy(ReadOnlySpan<char> currentSourceDir, char[] directoryBuffer)
    {
        ReadOnlySpan<char> relativeSpan = currentSourceDir[State.SourceRootLength..];

        Span<char> remaining = directoryBuffer.AsSpan();
        remaining = remaining.PathCombine(State.DestinationPath.AsSpan(), endWithSeparator: true);
        remaining = remaining.PathCombine(relativeSpan, endWithSeparator: false);

        int writtenChars = directoryBuffer.Length - remaining.Length;
        string newDirectory = directoryBuffer.AsSpan(0, writtenChars).ToString();

        long startTimestamp = Stopwatch.GetTimestamp();
        Directory.CreateDirectory(newDirectory);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);

        State.DirectoryCreated++;
        State.DirectoryCreationMs += elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// costruisco il path di destinazione del file corrente e invoco nativeio.move.
    /// </summary>
    /// <param name="item">ricevo il file da spostare.</param>
    /// <param name="destinationBuffer">riuso questo buffer per evitare nuove stringhe nel path di successo.</param>
    private void MoveSingleItem(StackFileInfo item, char[] destinationBuffer)
    {
        Span<char> destinationSpan = destinationBuffer.AsSpan();
        Span<char> remaining = destinationSpan;

        ReadOnlySpan<char> sourceDirSpan = item.AsDirectorySpan();
        ReadOnlySpan<char> nameSpan = item.AsNameSpan();

        if (State.IsRecursive)
        {
            remaining = remaining.PathCombine(State.DestinationPath.AsSpan(), endWithSeparator: true);

            if (sourceDirSpan.Length > State.SourceRootLength)
            {
                ReadOnlySpan<char> relativeSpan = sourceDirSpan[State.SourceRootLength..];
                remaining = remaining.PathCombine(relativeSpan, endWithSeparator: true);
            }

            remaining = remaining.AppendNext(nameSpan);
        }
        else
        {
            remaining = remaining.PathCombine(State.DestinationPath.AsSpan(), endWithSeparator: true);
            remaining = remaining.AppendNext(nameSpan);
        }

        int actualPathSize = destinationSpan.Length - remaining.Length;
        ReadOnlySpan<char> destinationPath = destinationSpan[..actualPathSize];

        NativeIO.Move(item.AsPathSpan(), destinationPath, State.Overwrite);

        State.MovedFilesCount++;
        State.BytesMoved += item.Length;
    }

    #endregion

    #region errors_and_progress

    /// <summary>
    /// raccolgo l'errore e decido se continuare in base al flag ignore-errors.
    /// </summary>
    /// <param name="ex">ricevo l'eccezione nata durante lo spostamento di un file.</param>
    /// <returns>restituisco true se posso continuare, false se devo interrompere il flusso.</returns>
    private bool HandleMoveError(Exception ex)
    {
        Errors.Add(new MoveException("Move", ex));
        return State.IgnoreErrors;
    }

    /// <summary>
    /// stampo un progresso minimale ogni circa 250ms per non rallentare il ciclo principale con output eccessivo.
    /// </summary>
    private void PrintProgressIfNeeded()
    {
        if (State.IsSilent || State.IsWhatIf) return;

        long nowMs = Environment.TickCount64;
        if (nowMs - State.LastProgressTickMs < 250) return;

        long deltaMs = nowMs - State.LastRateTickMs;
        if (deltaMs > 0)
        {
            long movedDelta = State.MovedFilesCount - State.LastRateFilesCount;
            State.CurrentFilesPerSecond = movedDelta / (deltaMs / 1000d);
            State.LastRateFilesCount = State.MovedFilesCount;
            State.LastRateTickMs = nowMs;
        }

        State.LastProgressTickMs = nowMs;
        State.ProgressPrinted = true;

        Console.Write($"\rFile spostati: {State.MovedFilesCount:N0} | Velocita: {State.CurrentFilesPerSecond:N1} file/s | Dati: {Formatter.Bytes(State.BytesMoved)}     ");
    }

    #endregion

    #region output

    /// <summary>
    /// stampo il riepilogo finale con i contatori principali e l'eventuale lista errori accumulata.
    /// </summary>
    private void PrintFinalStatistics()
    {
        ConsolePlus.WriteHr(25);
        ConsolePlus.Write("[Cyan]#[/] Operazione Conclusa.");
        ConsolePlus.Write($"[Cyan]*[/] File spostati   : [Cyan]{State.MovedFilesCount:N0}[/]");
        ConsolePlus.Write($"[Cyan]*[/] Dati trasferiti : [Green]{Formatter.Bytes(State.BytesMoved)}[/]");
        ConsolePlus.Write($"[Cyan]*[/] Cartelle create : [Cyan]{State.DirectoryCreated:N0}[/]");
        ConsolePlus.Write($"[Cyan]*[/] Tempo cartelle  : [Green]{State.DirectoryCreationMs:F2} ms[/]");
        ConsolePlus.WriteHr(25);

        if (Errors.Count > 0)
        {
            ConsolePlus.Write("[Red]#[/] sono state riscontrate le seguenti eccezioni:");
            int i = 0;
            foreach (var ex in Errors)
            {
                i++;
                ConsolePlus.Write($"[Red]#[/] {i}. [Red]{ex.Section}[/]: {ex.Message}\n[Red]#[/]");
            }
        }
    }

    /// <summary>
    /// mostro l'help standard del comando move.
    /// </summary>
    public override void Help()
    {
        PrintHelp<MoveSettings>();
    }

    #endregion
}
