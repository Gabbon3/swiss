using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace lib.console.fastprinter;

#region Output Format

public enum OutputFormat { Console, Csv, Json }

#endregion

#region Interface
/// <summary>
/// Interfaccia per qualsiasi destinazione di output supportata da FastPrinter.
/// Espone solo le operazioni strettamente necessarie per mantenere zero overhead.
/// </summary>
public interface IFastOutput : IAsyncDisposable
{
    /// <summary>Scrive il blocco di memoria sulla destinazione.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<char> memory, CancellationToken ct = default);

    /// <summary>
    /// Svuota i buffer interni sulla destinazione fisica.
    /// Chiamato dal printer solo quando il channel è momentaneamente vuoto
    /// per massimizzare il batching senza sacrificare la latenza.
    /// </summary>
    ValueTask FlushAsync(CancellationToken ct = default);
}

#endregion
#region Null Output

/// <summary>
/// Output nullo ("/dev/null"). Consuma i messaggi scartandoli immediatamente.
/// Implementa il Null Object Pattern per evitare controlli di nullità (if output != null) 
/// nei percorsi ad alte prestazioni.
/// </summary>
public sealed class NullOutput : IFastOutput
{
    // Singleton allocato una sola volta all'avvio dell'app
    public static readonly NullOutput Instance = new();

    // Costruttore privato per forzare l'uso di Instance ed evitare allocazioni inutili
    private NullOutput() { }

    // Ritorna ValueTask.CompletedTask che struct-based, zero allocazioni in heap
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask WriteAsync(ReadOnlyMemory<char> memory, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask FlushAsync(CancellationToken ct = default)
        => ValueTask.CompletedTask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;
}
#endregion
#region Console
/// <summary>
/// Output su console tramite ConsolePlus (sincrono, nessun buffer da flushare).
/// </summary>
public sealed class ConsoleOutput : IFastOutput
{
    private bool ConsoleWriteLine { get; set; } = true;

    // # Singleton per WriteLine (con ritorno a capo)
    public static readonly ConsoleOutput InstanceWriteLine = new(true);

    // # Singleton per Write (senza ritorno a capo)
    public static readonly ConsoleOutput InstanceWrite = new(false);

    private ConsoleOutput(bool consoleWriteLine = true)
    {
        ConsoleWriteLine = consoleWriteLine;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<char> memory, CancellationToken ct = default)
    {
        ConsolePlus.Write(memory, ConsoleWriteLine);
        return ValueTask.CompletedTask;
    }

    // La console non ha buffer interni da flushare esplicitamente
    public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

#endregion
#region File
/// <summary>
/// Output su file ad alte prestazioni.
/// Usa FileStream con I/O asincrono e uno StreamWriter con buffer grande;
/// AutoFlush è disabilitato — il flush avviene solo su richiesta esplicita
/// (cioè quando il channel è idle) per massimizzare il throughput.
/// </summary>
public sealed class FileOutput : IFastOutput
{
    private readonly StreamWriter _writer;

    /// <param name="path">Percorso del file di destinazione.</param>
    /// <param name="append">Se true aggiunge in coda al file esistente, altrimenti lo sovrascrive.</param>
    /// <param name="fileBufferSize">
    /// Dimensione del buffer di StreamWriter in caratteri (default 64 KB).
    /// Buffer più grandi riducono le syscall ma aumentano la latenza prima del flush.
    /// </param>
    public FileOutput(
        string path,
        bool append = false,
        int fileBufferSize = 64 * 1024)
    {
        var fs = new FileStream(
            path,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            // perche 1 se il buffer è da 64KB? cosi bypassiamo il buffer del filestream e scriviamo direttamente sul disco
            // in noi gia ottimizziamo il flush manualmente (channel a 0) e ogni 64KB bytes scritti nello streamwriter
            bufferSize: 1, // buffer del FileStream (I/O kernel)
            useAsync: true); // abilita I/O asincrono a livello OS

        _writer = new StreamWriter(
            fs,
            encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: fileBufferSize,
            leaveOpen: false)
        {
            AutoFlush = false // flush manuale per performance
        };
    }

    public ValueTask WriteAsync(ReadOnlyMemory<char> memory, CancellationToken ct = default)
        => new(_writer.WriteAsync(memory, ct));

    public ValueTask FlushAsync(CancellationToken ct = default)
        => new(_writer.FlushAsync(ct));

    public async ValueTask DisposeAsync()
    {
        // Flush finale prima di chiudere per non perdere dati nel buffer
        await _writer.FlushAsync().ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
    }
}
#endregion
#region Composite
/// <summary>
/// Output composito: broadcast verso più destinazioni senza allocazioni aggiuntive
/// sul percorso caldo. Le scritture avvengono in sequenza per evitare
/// l'allocazione di array di Task nel loop principale.
/// </summary>
public sealed class CompositeOutput : IFastOutput
{
    private readonly IFastOutput[] _outputs;

    public CompositeOutput(params IFastOutput[] outputs)
    {
        if (outputs.Length == 0)
            throw new ArgumentException("Almeno una destinazione richiesta.", nameof(outputs));

        _outputs = outputs;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<char> memory, CancellationToken ct = default)
    {
        // Scrittura sequenziale: zero allocazioni, nessun Task.WhenAll,
        // sufficiente per 2-3 output (caso d'uso tipico: console + file)
        foreach (var output in _outputs)
            await output.WriteAsync(memory, ct).ConfigureAwait(false);
    }

    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        foreach (var output in _outputs)
            await output.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var output in _outputs)
            await output.DisposeAsync().ConfigureAwait(false);
    }
}
#endregion
#region FastPrinter
/// <summary>
/// Stampante ad alte prestazioni basata su Channel.
/// Supporta qualsiasi destinazione tramite <see cref="IFastOutput"/>:
/// console, file, o combinazioni multiple tramite <see cref="CompositeOutput"/>.
/// </summary>
public class FastPrinter
{
    /// <summary>
    /// Restituisce l'enum corrispettivo in base al formato richiesto:
    /// - console (default)
    /// - csv
    /// - json
    /// </summary>
    /// <param name="format"></param>
    /// <returns></returns>
    public static OutputFormat GetOutputFormat(string? format)
    {
        return format switch
        {
            "csv" => OutputFormat.Csv,
            "json" => OutputFormat.Json,
            _ => OutputFormat.Console
        };
    }

    /// <summary>
    /// Restituisce l'oggetto che definisce al FastPrinter come e dove stampare
    /// </summary>
    /// <param name="format">enum del formato di output</param>
    /// <param name="silence">se true non verrà considerata la stampa a console</param>
    /// <param name="outputFilePath">percorso del file di output</param>
    /// <returns></returns>
    public static IFastOutput GenerateFastOutput(OutputFormat format, bool silence, string? outputFilePath)
    {
        bool outputIsFormatted = format != OutputFormat.Console;
        bool hasOutputFile = !string.IsNullOrWhiteSpace(outputFilePath);

        ConsoleOutput consoleOutput = outputIsFormatted ? ConsoleOutput.InstanceWrite : ConsoleOutput.InstanceWriteLine;
        IFastOutput printerOutput = consoleOutput;
        
        if (hasOutputFile)
        {
            var fileOutput = new FileOutput(outputFilePath!);

            if (silence)
            {
                printerOutput = fileOutput;
            }
            else
            {
                printerOutput = new CompositeOutput(consoleOutput, fileOutput);
            }
        }
        else if (silence)
        {
            printerOutput = NullOutput.Instance;
        }

        return printerOutput;
    }
    private readonly struct PrintPayload : IDisposable
    {
        private readonly string? _text;
        private readonly IMemoryOwner<char>? _memoryOwner;
        private readonly int _length;

        public ReadOnlyMemory<char> Memory => _text != null
            ? _text.AsMemory()
            : _memoryOwner!.Memory[.._length];

        public PrintPayload(string text)
        {
            _text = text;
            _memoryOwner = null;
            _length = text.Length;
        }

        // Percorso zero-allocazioni
        public PrintPayload(IMemoryOwner<char> memoryOwner, int length)
        {
            _text = null;
            _memoryOwner = memoryOwner;
            _length = length;
        }

        public void Dispose() => _memoryOwner?.Dispose();
    }

    // # stato

    private readonly Channel<PrintPayload> _channel;
    private readonly IFastOutput _output;
    private Task? _fastPrinterTask;

    // # opzioni

    /// <summary>Opzioni di configurazione per FastPrinter.</summary>
    /// <param name="output">
    /// Destinazione di output. Usa <see cref="ConsoleOutput.Instance"/> per la console,
    /// <see cref="FileOutput"/> per file, o <see cref="CompositeOutput"/> per entrambi.
    /// Se null, viene usata la console.
    /// </param>
    /// <param name="singleWriter">
    /// Se true indica che il Channel verrà scritto da un solo thread (ottimizzazione interna).
    /// </param>
    /// <param name="capacity">Dimensione massima del Channel (default 10 000).</param>
    public readonly struct FastPrinterOptions(
        IFastOutput? output = null,
        bool singleWriter = false,
        int capacity = 10_000)
    {
        public IFastOutput Output { get; } = output ?? ConsoleOutput.InstanceWriteLine;
        public bool SingleWriter { get; } = singleWriter;
        public int Capacity { get; } = capacity;
    }

    // # costruttori

    /// <summary>Costruttore con opzioni personalizzate.</summary>
    public FastPrinter(FastPrinterOptions options)
    {
        _output = options.Output;
        _channel = Channel.CreateBounded<PrintPayload>(new BoundedChannelOptions(options.Capacity)
        {
            SingleReader = true,
            SingleWriter = options.SingleWriter,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>
    /// Costruttore rapido: specifica solo la destinazione, tutto il resto usa i default.
    /// </summary>
    public FastPrinter(IFastOutput output) : this(new FastPrinterOptions(output)) { }

    /// <summary>Costruttore di default: stampa su console con parametri standard.</summary>
    public FastPrinter() : this(new FastPrinterOptions()) { }

    // # run

    /// <summary>
    /// Avvia il task di scrittura in background.
    /// </summary>
    public void Run(CancellationToken ct)
    {
        _fastPrinterTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    using (item)
                    {
                        await _output.WriteAsync(item.Memory, ct).ConfigureAwait(false);
                    }

                    // Flush-on-idle: svuota i buffer solo quando il channel è
                    // momentaneamente vuoto. In questo modo le scritture consecutive
                    // vengono raggruppate in un unico flush, riducendo drasticamente
                    // le syscall senza aggiungere latenza percepibile.
                    if (_channel.Reader.Count == 0)
                        await _output.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* cancellazione volontaria */ }
            catch (Exception) { /* errore di scrittura */      }
            finally
            {
                // Flush finale garantito anche in caso di eccezione
                try { await _output.FlushAsync(ct).ConfigureAwait(false); }
                catch { /* ignora errori nel flush di chiusura */ }
            }
        }, ct);
    }

    // # post su channel

    /// <summary>Invia un blocco di memoria nel channel (percorso zero-allocazioni).</summary>
    public ValueTask PostAsync(IMemoryOwner<char> owner, int length)
        => _channel.Writer.WriteAsync(new PrintPayload(owner, length));

    /// <summary>Invia una stringa nel channel (retrocompatibilità).</summary>
    public ValueTask PostAsync(string item)
        => _channel.Writer.WriteAsync(new PrintPayload(item));

    /// <summary>
    /// Tenta di inviare un blocco di memoria in modo sincrono (non bloccante).
    /// Restituisce false se il channel è pieno.
    /// </summary>
    public bool TryPost(IMemoryOwner<char> owner, int length)
        => _channel.Writer.TryWrite(new PrintPayload(owner, length));

    /// <summary>
    /// Tenta di inviare una stringa in modo sincrono (non bloccante).
    /// Restituisce false se il channel è pieno.
    /// </summary>
    public bool TryPost(string item)
        => _channel.Writer.TryWrite(new PrintPayload(item));

    /// <summary>
    /// Scrive nel canale in modo sincrono. Se il canale è pieno, blocca il thread 
    /// corrente finché non si libera spazio (Backpressure pura).
    /// Perfetto per chiamate da loop sincroni ad altissime prestazioni.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Post(IMemoryOwner<char> memory, int length)
    {
        // Fast path: Proviamo a scrivere subito
        if (_channel.Writer.TryWrite(new PrintPayload(memory, length)))
        {
            return;
        }

        // Slow path: Il canale è pieno
        // uso .AsTask().GetAwaiter().GetResult() per bloccare il worker.
        _channel.Writer.WriteAsync(new PrintPayload(memory, length)).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Scrive nel canale in modo sincrono. Se il canale è pieno, blocca il thread 
    /// corrente finché non si libera spazio (Backpressure pura).
    /// Alternativa con stringa invece di blocco di memoria.
    /// </summary>
    /// <param name="message"></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Post(string message)
    {
        // Fast path: Proviamo a scrivere subito
        if (_channel.Writer.TryWrite(new PrintPayload(message)))
        {
            return;
        }

        // Slow path: Il canale è pieno
        // uso .AsTask().GetAwaiter().GetResult() per bloccare il worker.
        _channel.Writer.WriteAsync(new PrintPayload(message)).AsTask().GetAwaiter().GetResult();
    }

    // # chiusura del channel

    /// <summary>
    /// Chiude il channel, attende il completamento di tutti i messaggi in coda
    /// e rilascia la destinazione di output.
    /// </summary>
    public async Task Complete()
    {
        _channel.Writer.Complete();
        if (_fastPrinterTask != null)
        {
            await _fastPrinterTask.ConfigureAwait(false);
        }

        await _output.DisposeAsync().ConfigureAwait(false);
    }
}
#endregion