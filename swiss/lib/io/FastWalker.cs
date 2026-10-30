using System.IO.Enumeration;
using System.Threading.Channels;

namespace lib.io
{
    public delegate T TransformFileSystemEntry<T>(ref FileSystemEntry entry);

    public sealed class FastWalkerCounters
    {
        private long _filesProcessed;
        private long _dirsProcessed;
        private long _bytesProcessed;

        public long FilesProcessed => Volatile.Read(ref _filesProcessed);
        public long DirsProcessed => Volatile.Read(ref _dirsProcessed);
        public long BytesProcessed => Volatile.Read(ref _bytesProcessed);

        internal void Add(long files, long dirs, long bytes = 0)
        {
            Interlocked.Add(ref _filesProcessed, files);
            Interlocked.Add(ref _dirsProcessed, dirs);
            Interlocked.Add(ref _bytesProcessed, bytes); // Nuovo
        }
    }

    public static class FastWalker
    {
        #region Walker
        /// <summary>
        /// Overload comodo di Walk quando non servono i contatori.
        /// </summary>
        public static ChannelReader<T> Walk<T>(
            string rootPath,
            TransformFileSystemEntry<T> transform,
            FastWalkerOptions? options = null,
            CancellationToken ct = default)
        {
            return WalkCore(rootPath, transform, null, options, ct);
        }

        /// <summary>
        /// Overload di Walk quando si vuole tracciare i contatori in tempo reale.
        /// </summary>
        public static ChannelReader<T> Walk<T>(
            string rootPath,
            TransformFileSystemEntry<T> transform,
            FastWalkerCounters counters,
            FastWalkerOptions? options = null,
            CancellationToken ct = default)
        {
            return WalkCore(rootPath, transform, counters, options, ct);
        }

        /// <summary>
        /// Questo metodo cammina il file system in parallelo in maniera ricorsiva
        /// </summary>
        /// <typeparam name="T">di default i dati vengono scritti sul channel in uscita come FileSystemEntry, qui puoi inserire un'altro oggetto a scelta dove memorizzare le informazioni di ogni file</typeparam>
        /// <param name="rootPath">percorso su cui iniziare il cammino</param>
        /// <param name="transform">metodo di trasformazione da FileSystemEntry a T (oggetto scelto per il channel in uscita)</param>
        /// <param name="options">opzioni di configurazione del FastWalker</param>
        /// <param name="counters">struttura per tenere traccia dei file e delle cartelle processate</param>
        /// <param name="ct">token di cancellazione dell'operazione</param>
        /// <returns>channel dove verranno lanciati tutti i risultati trovati nel cammino</returns>
        private static ChannelReader<T> WalkCore<T>(
            string rootPath,
            TransformFileSystemEntry<T> transform,
            FastWalkerCounters? counters,
            FastWalkerOptions? options = null,
            CancellationToken ct = default)
        {
            EnsureRootIsNotReparsePoint(rootPath);
            options ??= new FastWalkerOptions();

            // # THREADS
            int threads = 0;
            threads = options.MaxDegreeOfParallelism > 0 ? options.MaxDegreeOfParallelism : Environment.ProcessorCount;
            // se non si vuole la ricorsione (dunque si analizza solo la cartella root)
            // utilizzo un solo thread
            if (options.RecurseSubdirectories == false)
            {
                threads = 1;
            }
            else
            {
                // se è definito il numero di threads uso quello se no uso il numero di processori del pc disponibili
                threads = options.MaxDegreeOfParallelism > 0
                    ? options.MaxDegreeOfParallelism
                    : Environment.ProcessorCount;
            }

            // # CANALE DIRECTORY
            // dirChannel rappresenta la coda delle cartelle "da esaminare"
            var dirChannel = Channel.CreateUnbounded<string>();
            // outputChannel è il canale dove coinfluiranno tutti i risultati "in uscita" pronti da far usare all'esterno
            var outputChannel = Channel.CreateBounded<T>(new BoundedChannelOptions(50000)
            {
                SingleWriter = false,
                SingleReader = options.SingleReader
            });
            // pending work rappresenta il WaitGroup che quando arriverà a 0 farà chiudere il canale
            int pendingWork = 1;
            // metto la root nel canale
            dirChannel.Writer.TryWrite(rootPath);

            // # ENUMOPTIONS LOCALI (del thread)
            // opzioni locali per l'enumerazione dei file delle singole cartelle
            var localOptions = new EnumerationOptions
            {
                IgnoreInaccessible = options.IgnoreInaccessible,
                RecurseSubdirectories = false, // ricorsione manuale gestita gia a livello superiore, sempre false per i thread
                BufferSize = options.BufferSize,
                AttributesToSkip = options.AttributesToSkip,
                MatchCasing = options.MatchCasing,
                MatchType = options.MatchType,
                ReturnSpecialDirectories = false
            };
            // avvio degli operai
            for (int i = 0; i < threads; i++)
            {
                Task.Run(async () =>
                {
                    // Contatori locali (sullo stack del thread)
                    long localFiles = 0;
                    long localDirs = 0;

                    try
                    {
                        // pesco all'infinito finche il canale non viene chiuso
                        await foreach (var currentDir in dirChannel.Reader.ReadAllAsync(ct))
                        {
                            try
                            {
                                var enumerable = new FileSystemEnumerable<T>(
                                    currentDir,
                                    (ref FileSystemEntry entry) => transform(ref entry),
                                    localOptions
                                )
                                {
                                    ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                                    {
                                        if (entry.IsDirectory)
                                        {
                                            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                                            {
                                                return false;
                                            }

                                            // filtro di esclusione sulle cartelle per l'esplorazione (accodamento sul dirChannel)
                                            // se match true allora ignoro completamente questo percorso
                                            if (options.DirectoryExcludeFilter != null && options.DirectoryExcludeFilter(ref entry))
                                            {
                                                return false;
                                            }

                                            localDirs++;

                                            if (options.RecurseSubdirectories)
                                            {
                                                Interlocked.Increment(ref pendingWork);
                                                // garantisco coerenza
                                                if (!dirChannel.Writer.TryWrite(entry.ToFullPath()))
                                                {
                                                    Interlocked.Decrement(ref pendingWork);
                                                }
                                            }
                                            // non voglio le cartelle in output
                                            if (!options.ReturnDirectoriesInOutput)
                                            {
                                                return false;
                                            }
                                            // filtro sulla cartella per l'output
                                            if (options.Filter != null)
                                            {
                                                return options.Filter(ref entry);
                                            }

                                            return true;
                                        }

                                        localFiles++;

                                        // è un file quindi verifico solo se ci sono filtri
                                        if (options.Filter != null)
                                        {
                                            return options.Filter(ref entry);
                                        }
                                        return true;
                                    }
                                };

                                // implemento a mano l'enumerazione per lavorare sulle scritture asincrone
                                using var enumerator = enumerable.GetEnumerator();
                                while (enumerator.MoveNext())
                                {
                                    ct.ThrowIfCancellationRequested();
                                    // scriviamo tutti i file nel channel output
                                    // FAST PATH: provo a scrivere in maniera sincrona il file
                                    if (!outputChannel.Writer.TryWrite(enumerator.Current))
                                    {
                                        // SLOW PATH: devo attendere poiche il channel è pieno
                                        await outputChannel.Writer.WriteAsync(enumerator.Current);
                                    }
                                }
                            }
                            catch (UnauthorizedAccessException) { /* Ignoriamo cartelle senza permessi */ }
                            catch (DirectoryNotFoundException) { /* Cartella sparita nel mentre */ }
                            catch (Exception) { /* Gesti altri errori di I/O senza crashare */ }
                            finally
                            {
                                // l'operaio ha derminato e decrementa waitgroup, se = 0 chiude i channel
                                if (Interlocked.Decrement(ref pendingWork) == 0)
                                {
                                    dirChannel.Writer.TryComplete();
                                    outputChannel.Writer.TryComplete();
                                }
                                // scarico i contatori locali nel contatore condiviso (se richiesto)
                                if (counters != null && (localFiles != 0 || localDirs != 0))
                                {
                                    counters.Add(localFiles, localDirs);
                                    localFiles = 0;
                                    localDirs = 0;
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                }, ct);
            }
            // restituisco appena avvio il channel
            return outputChannel.Reader;
        }

        /// <summary>
        /// Cammina il file system in modo sequenziale (Single-Thread, DFS nativo).
        /// Ideale per gli HDD meccanici o per operazioni dove il multithreading fa da collo di bottiglia.
        /// </summary>
        public static IEnumerable<T> WalkSequential<T>(
            string rootPath,
            TransformFileSystemEntry<T> transform,
            FastWalkerOptions? options = null)
        {
            EnsureRootIsNotReparsePoint(rootPath);
            options ??= new FastWalkerOptions();

            var localOptions = new EnumerationOptions
            {
                IgnoreInaccessible = options.IgnoreInaccessible,
                RecurseSubdirectories = options.RecurseSubdirectories,
                BufferSize = options.BufferSize,
                AttributesToSkip = options.AttributesToSkip,
                MatchCasing = options.MatchCasing,
                MatchType = options.MatchType,
                ReturnSpecialDirectories = false
            };

            return new FileSystemEnumerable<T>(
                rootPath,
                (ref FileSystemEntry entry) => transform(ref entry),
                localOptions
            )
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    // Gestione cartelle
                    if (entry.IsDirectory)
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            return false;
                        }

                        return options.ReturnDirectoriesInOutput;
                    }
                    // Factory di filtri
                    if (options.Filter != null)
                    {
                        return options.Filter(ref entry);
                    }

                    return true;
                },
                ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                    (entry.Attributes & FileAttributes.ReparsePoint) == 0
            };
        }

        /// <summary>
        /// Verifica che il percorso radice non sia un reparse point (collegamento simbolico o junction).
        /// Se lo è, lancia un'eccezione IOException. In questo modo evitiamo disastri
        /// finendo in percorsi che non ci aspettiamo e che potrebbero portare a cancellazioni o modifiche indesiderate.
        /// </summary>
        /// <param name="rootPath"></param>
        /// <exception cref="IOException"></exception>
        private static void EnsureRootIsNotReparsePoint(string rootPath)
        {
            if ((File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Il percorso radice non può essere un collegamento: {rootPath}");
            }
        }

        #endregion
        #region Count

        /// <summary>
        /// Struttura di ritorno per il conteggio veloce
        /// </summary>
        public readonly struct CountResult(long files, long dirs, long bytes)
        {
            public readonly long Files = files;
            public readonly long Directories = dirs;
            public readonly long Bytes = bytes;
        }

        /// <summary>
        /// Attraversa il file system in parallelo ma invece di restituire i file,
        /// accumula i totali localmente nei thread e restituisce solo il risultato finale.
        /// Bypassando i channel in uscita, le performance sono estreme.
        /// </summary>
        /// <summary>
        /// Attraversa il file system in parallelo e restituisce il risultato finale.
        /// </summary>
        public static async Task<CountResult> CountAsync(
            string rootPath,
            FastWalkerCounters? counters = null,
            FastWalkerOptions? options = null,
            CancellationToken ct = default)
        {
            EnsureRootIsNotReparsePoint(rootPath);
            options ??= new FastWalkerOptions();
            // Se non viene passato dall'esterno, lo creiamo internamente
            counters ??= new FastWalkerCounters();

            int threads = (!options.RecurseSubdirectories) ? 1 :
                (options.MaxDegreeOfParallelism > 0 ? options.MaxDegreeOfParallelism : Environment.ProcessorCount);

            var dirChannel = Channel.CreateUnbounded<string>();
            int pendingWork = 1;
            dirChannel.Writer.TryWrite(rootPath);

            var localOptions = new EnumerationOptions
            {
                IgnoreInaccessible = options.IgnoreInaccessible,
                RecurseSubdirectories = false,
                BufferSize = options.BufferSize,
                AttributesToSkip = options.AttributesToSkip,
                MatchCasing = options.MatchCasing,
                MatchType = options.MatchType,
                ReturnSpecialDirectories = false
            };

            var workers = new Task[threads];

            for (int i = 0; i < threads; i++)
            {
                workers[i] = Task.Run(async () =>
                {
                    // Contatori locali (sullo stack del thread)
                    long localFiles = 0;
                    long localDirs = 0;
                    long localBytes = 0;

                    try
                    {
                        await foreach (var currentDir in dirChannel.Reader.ReadAllAsync(ct))
                        {
                            try
                            {
                                var enumerable = new FileSystemEnumerable<byte>(
                                    currentDir,
                                    (ref FileSystemEntry entry) => 0,
                                    localOptions
                                )
                                {
                                    ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                                    {
                                        if (entry.IsDirectory)
                                        {
                                            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                                            {
                                                return false;
                                            }

                                            if (options.RecurseSubdirectories)
                                            {
                                                Interlocked.Increment(ref pendingWork);
                                                if (!dirChannel.Writer.TryWrite(entry.ToFullPath()))
                                                {
                                                    Interlocked.Decrement(ref pendingWork);
                                                }
                                            }

                                            if (options.ReturnDirectoriesInOutput && (options.Filter == null || options.Filter(ref entry)))
                                            {
                                                localDirs++;
                                            }

                                            return false;
                                        }

                                        if (options.Filter != null && !options.Filter(ref entry))
                                        {
                                            return false;
                                        }

                                        localFiles++;
                                        localBytes += entry.Length;
                                        // flush ogni 1024 file
                                        if (localFiles >= 1024)
                                        {
                                            counters.Add(localFiles, localDirs, localBytes);
                                            localFiles = 0;
                                            localDirs = 0;
                                            localBytes = 0;
                                            ct.ThrowIfCancellationRequested();
                                        }
                                        return false;
                                    }
                                };

                                using var enumerator = enumerable.GetEnumerator();
                                while (enumerator.MoveNext()) { }
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (UnauthorizedAccessException) { }
                            catch (DirectoryNotFoundException) { }
                            catch (Exception) { }
                            finally
                            {
                                if (Interlocked.Decrement(ref pendingWork) == 0)
                                {
                                    dirChannel.Writer.TryComplete();
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    finally
                    {
                        // in caso di stop improvviso oppure a termine esecuzione
                        // scarichiamo in ogni caso gli ultimi dati
                        if (localFiles > 0 || localDirs > 0 || localBytes > 0)
                        {
                            counters.Add(localFiles, localDirs, localBytes);
                        }
                    }
                }, ct);
            }

            await Task.WhenAll(workers);

            // Restituiamo il risultato leggendo direttamente i contatori globali finali
            return new CountResult(counters.FilesProcessed, counters.DirsProcessed, counters.BytesProcessed);
        }
        #endregion
    }
}