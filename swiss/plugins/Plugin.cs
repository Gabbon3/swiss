using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using lib.console;
using lib.io;
using Spectre.Console;

namespace plugins;

public abstract class Plugin
{
    #region Metadata

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract Task RunAsync(string[] args, CancellationToken ct);

    #endregion

    #region Parsing

    /// <summary>
    /// Parsa automaticamente gli args e restituisce l'oggetto Settings popolato
    /// </summary>
    protected static TSettings ParseSettings<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSettings>(string[] args) where TSettings : new()
    {
        var settings = new TSettings();
        var props = typeof(TSettings).GetProperties();

        // 1. Estrae i Fixed e le Opzioni
        var fixedProps = props
            .Select(p => new { Prop = p, Attr = p.GetCustomAttribute<FixedAttribute>() })
            .Where(x => x.Attr != null)
            .OrderBy(x => x.Attr!.Position)
            .ToList();

        var optionProps = props
            .Select(p => new { Prop = p, Attr = p.GetCustomAttribute<OptionAttribute>() })
            .Where(x => x.Attr != null)
            .ToList();

        int argIndex = 0;

        // 2. Parsa prima gli argomenti Fixed (Posizionali)
        foreach (var fp in fixedProps)
        {
            // Se finiscono gli argomenti, o se incontriamo improvvisamente un flag (es: -r), ci fermiamo
            if (argIndex >= args.Length || args[argIndex].StartsWith('-'))
                break;

            AssignValue(settings, fp.Prop, args[argIndex]);
            argIndex++;
        }

        // 3. Parsa il resto come Opzioni (Flag e Valori)
        while (argIndex < args.Length)
        {
            string currentArg = args[argIndex];

            if (currentArg.StartsWith('-'))
            {
                bool isLong = currentArg.StartsWith("--");
                string optName = isLong ? currentArg[2..] : currentArg[1..];

                // Cerca la proprietà corrispondente all'attributo
                var match = optionProps.FirstOrDefault(o =>
                    (isLong && o.Attr!.LongName == optName) ||
                    (!isLong && o.Attr!.ShortName == optName)
                );

                if (match != null)
                {
                    if (match.Prop.PropertyType == typeof(bool))
                    {
                        // Se è booleano, la sua semplice presenza significa TRUE (es: --recursive)
                        match.Prop.SetValue(settings, true);
                    }
                    else
                    {
                        // Se è un valore (es: --threads 4), prendiamo l'argomento successivo
                        if (argIndex + 1 < args.Length && !args[argIndex + 1].StartsWith('-'))
                        {
                            argIndex++;
                            AssignValue(settings, match.Prop, args[argIndex]);
                        }
                    }
                }
            }
            argIndex++;
        }

        return settings;
    }

    #endregion

    #region Input Helpers

    /// <summary>
    /// Fa il parsing dell'input path preso dalla CLI e controlla se esiste su richiesta
    /// </summary>
    /// <param name="inputPath">percorso preso dagli args</param>
    /// <param name="checkPath">se true controlla se il path esuste</param>
    /// <returns></returns>
    protected string? ParsePath(string inputPath, bool checkPath = false)
    {
        string path = inputPath;
        if (inputPath == ".")
        {
            path = Environment.CurrentDirectory;
        }
        else if (inputPath.StartsWith("./") || inputPath.StartsWith($".{Path.DirectorySeparatorChar}"))
        {
            path = Path.Combine(Environment.CurrentDirectory, inputPath[2..]);
        }
        if (checkPath && !Path.Exists(path))
        {
            PrintError($"Il percorso non esiste: {path}");
            return null;
        }
        return path;
    }

    /// <summary>
    /// Parsa il pattern da usare nelle ricerche dei match
    /// </summary>
    /// <param name="pattern">usare * o vuoto o null per includere tutto</param>
    /// <returns></returns>
    protected string? ParseMatchPattern(string? pattern)
    {
        // Restituisco null se * (cioe voglio tutti i match) e se è gia null o vuoto
        if (pattern == "*" || string.IsNullOrEmpty(pattern)) return null;
        return pattern;
    }

    /// <summary>
    /// Restituisce il PatternMatchType corrispettivo alla stringa passata in input
    /// </summary>
    /// <param name="value"></param>
    /// <param name="matchType"></param>
    /// <returns>Default restituisce Glob poiche molto potente e semplice da usare</returns>
    protected static PatternMatchType ParsePatternMatchType(string? value)
    {
        return value switch
        {
            "regex" => PatternMatchType.Regex,
            "fixed" => PatternMatchType.Fixed,
            "glob" => PatternMatchType.Glob,
            _ => PatternMatchType.Auto, // null oppure altro
        };
    }

    #endregion

    #region Value Assignment

    /// <summary>
    /// Helper per convertire le stringhe nei tipi giusti (int, string, bool, ecc.)
    /// </summary>
    /// <param name="obj">Opzione delle settings da valorizzare</param>
    /// <param name="prop"></param>
    /// <param name="value">La stringa ottenuta dagli args</param>
    private static void AssignValue(object obj, PropertyInfo prop, string value)
    {
        // Ottengo il tipo di elemento (string, int, bool) della proprietà
        var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
        // in base al tipo confronto e faccio il parsing
        // stringhe
        if (targetType == typeof(string))
            prop.SetValue(obj, value);
        // int
        else if (targetType == typeof(int) && int.TryParse(value, out int intVal))
            prop.SetValue(obj, intVal);
        // bool
        else if (targetType == typeof(bool) && bool.TryParse(value, out bool boolVal))
            prop.SetValue(obj, boolVal);
        // relative datetime (con campo temporale m/c/a)
        else if (targetType == typeof(RelativeDateTime))
        {
            if (RelativeDateTime.TryParseRelativeDate(value, out RelativeDateTime relativeDateTime))
            {
                prop.SetValue(obj, relativeDateTime);
            }
        }
        // datetime
        else if (targetType == typeof(DateTime))
        {
            // provo a vedere se l'utente ha passato una data di quel tipo (es. "12d", "5h")
            if (RelativeDateTime.TryParseRelativeDate(value, out DateTime relativeDate))
            {
                prop.SetValue(obj, relativeDate);
            }
            // fallback parsing standard ("2024-10-25")
            else if (DateTime.TryParse(value, out DateTime standardDate))
            {
                prop.SetValue(obj, RelativeDateTime.NormalizeToUtc(standardDate));
            }
        }
        // double
        else if (targetType == typeof(double) && double.TryParse(value, out double doubleVal))
            prop.SetValue(obj, doubleVal);
            
        // relative size
        else if (targetType == typeof(RelativeSize))
        {
            RelativeSize relativeSize = RelativeSize.Parse(value);
            prop.SetValue(obj, relativeSize);
        }
        // pattern match type
        else if (targetType == typeof(PatternMatchType))
        {
            prop.SetValue(obj, ParsePatternMatchType(value));
        }
        // TODO: aggiungere supporto per altri tipi
    }

    #endregion

    #region Help

    /// <summary>
    /// Stampa dell'Help standardizzata basata sulla struttura dei settings usando ConsolePlus
    /// </summary>
    public void PrintHelp<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSettings>(bool printEndLine = true)
    {
        const int maxLineWidth = 92;
        var properties = typeof(TSettings).GetProperties();

        // 1. Estrai e ordina gli argomenti Fixed
        var fixedArgs = properties
            .Select(p => new { Prop = p, Attr = p.GetCustomAttribute<FixedAttribute>() })
            .Where(x => x.Attr != null)
            .OrderBy(x => x.Attr!.Position)
            .ToList();

        // 2. Estrai le Opzioni
        var options = properties
            .Select(p => new { Prop = p, Attr = p.GetCustomAttribute<OptionAttribute>() })
            .Where(x => x.Attr != null)
            .OrderBy(x => x.Attr!.Category)
            .ThenBy(x => x.Attr!.LongName)
            .ToList();

        // --- COMPOSIZIONE OUTPUT ---

        // Costruisco la stringa di utilizzo
        string usageLine = $"[Yellow]swiss[/] [Blue]{Name}[/]";
        foreach (var fa in fixedArgs)
        {
            usageLine += $" [DarkGray]<{fa.Attr!.Name}>[/]";
        }
        if (options.Any()) usageLine += " [DarkGray][opzioni][/]";

        // Uso ConsolePlus per l'intestazione
        ConsolePlus.WriteBoxHeader("Uso", 80);
        ConsolePlus.Write($"  {usageLine}");
        ConsolePlus.WriteHr(80);
        ConsolePlus.Write(""); // Riga vuota

        // 3. Calcolo il padding dinamico per l'allineamento a colonna
        int maxArgLen = fixedArgs.Any() ? fixedArgs.Max(x => x.Attr!.Name.Length + 2) : 0; // +2 per le parentesi <>
        int maxOptLen = options.Any() ? options.Max(x =>
        {
            int len = x.Attr!.LongName.Length + 2; // "--" + nome
            if (!string.IsNullOrEmpty(x.Attr.ShortName)) len += x.Attr.ShortName.Length + 3; // ", -" + short
            return len;
        }) : 0;

        int padding = Math.Max(maxArgLen, maxOptLen) + 4; // +4 per spazio extra di respiro

        // 4. Stampa Argomenti Fixed
        if (fixedArgs.Count != 0)
        {
            ConsolePlus.Write("[Cyan]Argomenti:[/]");
            foreach (var fa in fixedArgs)
            {
                string left = $"<{fa.Attr!.Name}>".PadRight(padding);
                WriteHelpDescriptionLine(
                    leftPart: left,
                    description: fa.Attr.Description ?? "",
                    indent: "  ",
                    leftColor: "White",
                    maxLineWidth: maxLineWidth
                );
            }
            ConsolePlus.Write("");
        }

        // 5. Stampa Opzioni
        if (options.Count != 0)
        {
            ConsolePlus.Write("[Cyan]Opzioni:[/]");
            string category = "";

            foreach (var opt in options)
            {
                string currentCategory = opt.Attr!.Category ?? "";

                // Stampa l'intestazione di categoria se cambia
                if (currentCategory != category)
                {
                    category = currentCategory;
                    if (!string.IsNullOrEmpty(category))
                    {
                        ConsolePlus.Write(""); // Spazio prima di una nuova categoria
                        ConsolePlus.Write($"  [Yellow]{category}:[/]");
                    }
                }

                // Composizione dei flag
                string shortFlag = !string.IsNullOrEmpty(opt.Attr!.ShortName) ? $", -{opt.Attr.ShortName}" : "";
                string flags = $"--{opt.Attr.LongName}{shortFlag}";

                // Indentazione: se c'è una categoria indento di più
                string indent = string.IsNullOrEmpty(category) ? "  " : "    ";

                string left = flags.PadRight(padding);
                WriteHelpDescriptionLine(
                    leftPart: left,
                    description: opt.Attr.Description ?? "",
                    indent: indent,
                    leftColor: "Green",
                    maxLineWidth: maxLineWidth
                );
            }
        }

        if (printEndLine)
        {
            ConsolePlus.Write("");
            ConsolePlus.WriteHr(80);
        }
    }

    private static void WriteHelpDescriptionLine(string leftPart, string description, string indent, string leftColor, int maxLineWidth)
    {
        int availableDescriptionWidth = Math.Max(20, maxLineWidth - (indent.Length + leftPart.Length));

        var wrappedLines = WrapText(description, availableDescriptionWidth);
        if (wrappedLines.Count == 0)
        {
            ConsolePlus.Write($"{indent}[{leftColor}]{leftPart}[/]");
            return;
        }

        ConsolePlus.Write($"{indent}[{leftColor}]{leftPart}[/][DarkGray]{wrappedLines[0]}[/]");

        string continuationIndent = new string(' ', indent.Length + leftPart.Length);
        for (int i = 1; i < wrappedLines.Count; i++)
        {
            ConsolePlus.Write($"{continuationIndent}[DarkGray]{wrappedLines[i]}[/]");
        }
    }

    private static List<string> WrapText(string text, int maxCharsPerLine)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return lines;

        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return lines;

        string currentLine = words[0];
        for (int i = 1; i < words.Length; i++)
        {
            string candidate = currentLine + " " + words[i];
            if (candidate.Length <= maxCharsPerLine)
            {
                currentLine = candidate;
                continue;
            }

            lines.Add(currentLine);

            // Spezza parole molto lunghe in chunk fissi.
            if (words[i].Length > maxCharsPerLine)
            {
                int start = 0;
                while (start < words[i].Length)
                {
                    int len = Math.Min(maxCharsPerLine, words[i].Length - start);
                    lines.Add(words[i].Substring(start, len));
                    start += len;
                }

                currentLine = string.Empty;
                continue;
            }

            currentLine = words[i];
        }

        if (!string.IsNullOrEmpty(currentLine))
        {
            lines.Add(currentLine);
        }

        return lines;
    }

    #endregion

    #region Diagnostics

    public virtual void Help()
    {
        PrintWarning("Nessun help definito");
    }

    private readonly Lock _printErrorLock = new();
    public void PrintWarning(string message)
    {
        lock (_printErrorLock)
        {
            ConsolePlus.Write($"[Yellow][ ! ] {Name}: {message}[/]");
        }
    }
    public void PrintError(string message)
    {
        lock (_printErrorLock)
        {
            ConsolePlus.Write($"[Red][ERRORE] {Name}: {message}[/]");
        }
    }

    #endregion
}