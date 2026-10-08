using System.Diagnostics;
using plugins;
using lib.console;
using Spectre.Console;
using lib.io.log;
// lista plugins
using plugins.find;
using plugins.tree;
using plugins.eliminator;
using plugins.count;
using plugins.mdconverter;
using plugins.regexgrep;
using plugins.move;
// # ----------------------- #
// # CONFIGURAZIONE INIZIALE #
// # ----------------------- #
Console.OutputEncoding = System.Text.Encoding.UTF8;
AnsiConsole.Profile.Encoding = System.Text.Encoding.UTF8;
AnsiConsole.Profile.Capabilities.Ansi = true;
AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.Standard;
// info sulla versione
const string version = "2.6.0";
const string versionDate = "08.10.2026";
const string author = "Gabbon3";
// traccio su event log
// AuditLogger.LogCommandExecution(string.Join(' ', args));
// cancellation token
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (sender, e) =>
{
    e.Cancel = true;
    ConsolePlus.Write("\n[Yellow]Richiesta di annullamento ricevuta (Ctrl+C)...[/]");
    // invio del segnale di stop del processo in maniera safe
    cts.Cancel();
};
// registro dei plugin
List<PluginRegistration> plugins = [
    new("find", "Ricerca file", () => new FindPlugin()),
    new("tree", "Mostra albero delle directory", () => new TreePlugin()),
    new("eliminator", "Eliminatore di files", () => new EliminatorPlugin()),
    new("count", "Conteggio files", () => new CountPlugin()),
    new("mdconverter", "Conversione file md in html (default) e pdf", () => new MdConverterPlugin()),
    new("grep", "Grep Regex e non (UTF-8)", () => new RegexGrepPlugin()),
    new("move", "Sposta files", () => new MovePlugin()),
];
// # ----------------------- #

// # --------------------------------- #
// # RECUPERO INFORMAZIONI PRELIMINARI #
// # --------------------------------- #
if (args.Length == 0)
{
    ConsolePlus.Write("[Yellow][ ! ] Nessun comando inserito[/]\n[ i ] [Cyan]swiss --help[/] oppure [Cyan]swiss -h[/] per ottenere maggiori informazioni");
    //Help(plugins);
    return;
}

string pluginName = args[0].ToLower();

if (pluginName == "--help" || pluginName == "-h")
{
    Help(plugins);
    return;
}

if (pluginName == "--version" || pluginName == "-v")
{
    VersionInfo();
    return;
}

bool printStats = args[^1] == "--stats";
string[] pluginArgs = [];
if (printStats)
{
    pluginArgs = args.Length > 1 ? [.. args[1..^1]] : [];
}
else
{
    pluginArgs = [.. args[1..]];
}

PluginRegistration? pluginMeta = plugins.FirstOrDefault(p => p.Name == pluginName);
// # --------------------------------- #

// # ---------------------- #
// # -- AVVIO DEL PLUGIN -- #
// # ---------------------- #

// setup per analisi performance processo
using Process currentProcess = Process.GetCurrentProcess();

if (pluginMeta != null)
{
    Plugin plugin = pluginMeta.Factory();

    if (pluginArgs.Length > 0 && (pluginArgs[0] == "--help" || pluginArgs[0] == "-h"))
    {
        plugin.Help();
        return;
    }
    // # STATS
    long startTimestamp = 0;
    TimeSpan startCpuTime = TimeSpan.Zero;
    long startGcMemory = 0;
    if (printStats)
    {
        currentProcess.Refresh();
        startTimestamp = Stopwatch.GetTimestamp();
        startCpuTime = currentProcess.TotalProcessorTime;
        startGcMemory = GC.GetTotalMemory(false);
    }
    // # -----
    try
    {
        // # ESECUZIONE PLUGIN
        await plugin.RunAsync(pluginArgs, cts.Token);
        // # -----------------
    }
    catch (OperationCanceledException)
    {
        ConsolePlus.Write("\n[Yellow]Operazione annullata dall'utente.[/]");
    }
    catch (Exception ex)
    {
        ConsolePlus.Write($"\n[Red]Errore imprevisto esecuzione plugin: {ex.Message}[/]");
    }
    finally
    {
        // # STATS
        if (printStats)
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            currentProcess.Refresh();
            TimeSpan endCpuTime = currentProcess.TotalProcessorTime;
            TimeSpan cpuUsed = endCpuTime - startCpuTime;
            long peakMemory = currentProcess.PeakWorkingSet64;
            long endGcMemory = GC.GetTotalMemory(false);
            PrintStatistics(elapsed, cpuUsed, peakMemory, endGcMemory - startGcMemory);
        }
        // # -----
    }
}
else
{
    ConsolePlus.Write($"[Yellow][ ! ] Il comando \"[Magenta]{pluginName}[Yellow]\" non esiste.[/]");
}
// # ---------------------- #

// # -------------------- #
// # METODI BASE DEL MAIN #
// # -------------------- #
static void Help(List<PluginRegistration> plugins)
{
    ConsolePlus.WriteHr();
    ConsolePlus.Write("[Cyan]#[/] Lista comandi supportati:");
    // per formattazione
    int maxNameLength = plugins.Count != 0 ? plugins.Max(p => p.Name.Length) : 0;
    foreach (var plugin in plugins)
    {
        ConsolePlus.Write($"[Cyan]* [Green]{plugin.Name.PadRight(maxNameLength)}[/] {plugin.Description}");
    }
    ConsolePlus.Write($"[Cyan]* [Magenta]{"--stats".PadRight(maxNameLength)}[/] Inseriscilo come ultimo argomento per stampare le statistiche di esecuzione");
    ConsolePlus.WriteHr();
}

static void VersionInfo()
{
    string versionAndDescription = $"[Cyan]*[/] Version:   [Green]{version}[/]";
    int lineLength = versionAndDescription.Length - 18;
    ConsolePlus.WriteHr(lineLength);
    ConsolePlus.Write(versionAndDescription);
    ConsolePlus.Write($"[Cyan]*[/] Date: [Green]{versionDate}");
    ConsolePlus.Write($"[Cyan]*[/] Author:  [Green]{author}");
    ConsolePlus.WriteHr(lineLength);
}

static void PrintStatistics(TimeSpan elapsed, TimeSpan cpuTime, long peakMemoryBytes, long gcMemoryDiff)
{
    double cpuRatio = elapsed.TotalMilliseconds > 0 ? cpuTime.TotalMilliseconds / elapsed.TotalMilliseconds : 0;
    string sign = gcMemoryDiff >= 0 ? "+" : "";

    Console.WriteLine();
    ConsolePlus.WriteBoxHeader("Statistiche".AsSpan(), 40, ConsoleColor.Green);
    ConsolePlus.WriteList([
        $"Tempo Totale: [Cyan]{elapsed.TotalSeconds:N4} s[/]", 
        $"Tempo CPU: [Yellow]{cpuTime.TotalSeconds:N4} s[/] [DarkGray](avg {cpuRatio:N1}x core)[/]", 
        $"RAM Picco (Phys): [Magenta]{peakMemoryBytes / 1024.0 / 1024.0:N2} MB[/]", 
        $"GC Alloc (Delta): [DarkGray]{sign}{gcMemoryDiff / 1024.0 / 1024.0:N4} MB[/]"]
    , 0, '*', 2);
    ConsolePlus.WriteHr(40);
}
// # --------------------------------------------------- #
// dotnet publish swiss/swiss.csproj -c Release -r win-x64
// # --------------------------------------------------- #