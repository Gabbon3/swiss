using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;
using lib.utils;

namespace lib.io
{
    public delegate bool FileSystemFilter(ref FileSystemEntry entry);

    // Delegato personalizzato essenziale: in C# le 'ref struct' (come ReadOnlySpan<char>) 
    // non possono essere usate nei tipi generici standard come Func<T, R>.
    public delegate bool SpanMatchDelegate(ReadOnlySpan<char> span);

    /// <summary>
    /// Definisce il modello di filtraggio dei file
    /// </summary>
    public enum PatternMatchType
    {
        Auto, // si adatta in automatico in base al pattern passato
        Regex, // usa regex compilata non backtracking
        Fixed, // usa indexOf
        Glob   // pattern glob semplice di windows, simile a regex ma piu veloce
    }

    public static class FileFilterFactory
    {
        // Record che raggruppa tutti i filtri
        public record FilterOptions(
            string? Pattern = null,
            PatternMatchType MatchType = PatternMatchType.Auto,
            bool IgnoreCase = true,
            RelativeDateTime? DateAfter = null,
            RelativeDateTime? DateBefore = null,
            RelativeSize? MinSize = null,
            RelativeSize? MaxSize = null,
            bool MatchFullPath = false // se true il controllo sul pattern verrà esteso all'intero percorso
        )
        {
            /// <summary>
            /// Restituisce una descrizione testuale e human-readable dei filtri attivi.
            /// Ideale per la conferma in CLI prima dell'esecuzione.
            /// </summary>
            public override string ToString()
            {
                var sb = new StringBuilder();

                if (!string.IsNullOrEmpty(Pattern))
                {
                    string caseStr = IgnoreCase ? "(Case-Insensitive)" : "(Case-Sensitive)";
                    string matchStr = MatchType switch
                    {
                        PatternMatchType.Auto => "Sceglie la modalità di ricerca più adatta in base al pattern in automatico",
                        PatternMatchType.Regex => "Espressione regolare",
                        PatternMatchType.Fixed => "Testo fisso",
                        PatternMatchType.Glob => "Pattern glob",
                        _ => "Sconosciuto"
                    };
                    // Aggiungo il contesto dell'ambito di ricerca
                    string scopeStr = MatchFullPath ? "su percorso completo" : "su nome elemento";
                    sb.AppendLine($"[Cyan]*[/] Filtro: Corrispondenza {matchStr} con '{Pattern}' {caseStr} {scopeStr}");
                }

                if (DateAfter.HasValue)
                {
                    var date = DateAfter.Value;
                    sb.AppendLine($"[Cyan]*[/] Piu recente [DarkGray](data {FieldDescription(date.Field)})[/]: {date.ValueUtc:dd.MM.yyyy HH:mm} UTC");
                }

                if (DateBefore.HasValue)
                {
                    var date = DateBefore.Value;
                    sb.AppendLine($"[Cyan]*[/] Piu vecchio [DarkGray](data {FieldDescription(date.Field)})[/]: {date.ValueUtc:dd.MM.yyyy HH:mm} UTC");
                }

                if (MinSize.HasValue)
                {
                    sb.AppendLine($"[Cyan]*[/] Dimensioni >= di [DarkGray]{Formatter.Bytes(MinSize.Value.Value)}[/]");
                }

                if (MaxSize.HasValue)
                {
                    sb.AppendLine($"[Cyan]*[/] Dimensioni <= di [DarkGray]{Formatter.Bytes(MaxSize.Value.Value)}[/]");
                }

                if (sb.Length == 0)
                {
                    return "Nessun filtro applicato (Tutti i file saranno inclusi).";
                }

                return "[Cyan]#[/] Filtri attivi:\n" + sb.ToString().TrimEnd();
            }
        };

        /// <summary>
        /// Fonde due filtri. Se il primo fallisce, il secondo non viene nemmeno eseguito.
        /// </summary>
        private static FileSystemFilter Combine(FileSystemFilter a, FileSystemFilter b)
        {
            return (ref FileSystemEntry entry) => a(ref entry) && b(ref entry);
        }

        private static string FieldDescription(RelativeDateTimeField field)
        {
            return field switch
            {
                RelativeDateTimeField.Created => "creazione",
                RelativeDateTimeField.Accessed => "ultimo accesso",
                _ => "modifica"
            };
        }

        /// <summary>
        /// Helper ad alte prestazioni che decide, senza creare overhead, quale Span passare alla logica di match.
        /// Unisce il percorso sullo stack (Zero-Allocation) se è richiesto il MatchFullPath.
        /// </summary>
        private static bool MatchOnTarget(ref FileSystemEntry entry, bool checkFullPath, SpanMatchDelegate matchFunc)
        {
            // FAST PATH: controllo esclusivo sul nome del file/cartella corrente
            if (!checkFullPath)
            {
                return matchFunc(entry.FileName);
            }

            // FULL PATH: Ricostruzione del percorso completo sullo STACK per evitare allocazioni in Heap.
            ReadOnlySpan<char> dir = entry.Directory;
            ReadOnlySpan<char> name = entry.FileName;
            int totalLen = dir.Length + 1 + name.Length;

            // Se il path rientra nei 512 caratteri (99.9% dei casi) andiamo sullo stack.
            // Se eccede, per non mandare in overflow lo stack del thread, facciamo fallback sull'heap.
            Span<char> buffer = totalLen <= 512
                ? stackalloc char[totalLen]
                : new char[totalLen];

            // 1. Copia la cartella madre
            dir.CopyTo(buffer);
            // 2. Aggiunge lo slash del sistema operativo ('\' o '/')
            buffer[dir.Length] = Path.DirectorySeparatorChar;
            // 3. Appende il nome finale
            name.CopyTo(buffer[(dir.Length + 1)..]);

            // Passiamo il buffer unificato alla logica di test (regex, fixed o glob)
            return matchFunc(buffer);
        }

        /// <summary>
        /// Genera il delegate ad alte prestazioni per il filtraggio
        /// </summary>
        public static FileSystemFilter? CreateFilter(FilterOptions options)
        {
            // Parto da filtro nullo cosi se non sono stati richiesti volo
            FileSystemFilter? finalFilter = null;
            
            // Helper per aggiungere un filtro alla catena
            void AddFilter(FileSystemFilter newFilter)
            {
                if (finalFilter == null)
                    finalFilter = newFilter;
                else
                    finalFilter = Combine(finalFilter, newFilter);
            }

            static DateTime SelectDateUtc(ref FileSystemEntry entry, RelativeDateTimeField field)
            {
                return field switch
                {
                    RelativeDateTimeField.Created => entry.CreationTimeUtc.UtcDateTime,
                    RelativeDateTimeField.Accessed => entry.LastAccessTimeUtc.UtcDateTime,
                    _ => entry.LastWriteTimeUtc.UtcDateTime
                };
            }

            // Gestione pattern AUTO
            // dato che di default usiamo il Auto, dobbiamo scegliere tra Fixed e Glob:
            // - se il pattern contiene * oppure ? allora lo trattiamo come Glob
            // - altrimenti usiamo Fixed
            PatternMatchType matchType = options.MatchType;
            // se il pattern non è vuoto e la modalità è AUTO
            if (options.MatchType == PatternMatchType.Auto && !string.IsNullOrEmpty(options.Pattern))
            {
                // Fixed se non contiene i caratteri glob
                if (!(options.Pattern.Contains('*') || options.Pattern.Contains('?')))
                {
                    matchType = PatternMatchType.Fixed;
                } else
                {
                    matchType = PatternMatchType.Glob;
                }
            }

            // --- FILTRI SULLE DATE ---
            if (options.DateAfter.HasValue)
            {
                var date = options.DateAfter.Value;
                AddFilter((ref FileSystemEntry entry) => SelectDateUtc(ref entry, date.Field) >= date.ValueUtc);
            }
            if (options.DateBefore.HasValue)
            {
                var date = options.DateBefore.Value;
                AddFilter((ref FileSystemEntry entry) => SelectDateUtc(ref entry, date.Field) <= date.ValueUtc);
            }

            // --- FILTRI SULLA DIMENSIONE ---
            if (options.MinSize.HasValue)
            {
                // sembra un inception ma è normale, MinSize è definito come Nullable, quindi è incapsulato in Nullable<MinSize>
                // dove il valore effettivo di MinSize risiede in Nullable<MinSize>.Value
                // poi di conseguenza MinSize possiede la proprietà Value, che è quella che ci interessa
                var size = options.MinSize.Value.Value;
                AddFilter((ref FileSystemEntry entry) => entry.Length >= size);
            }
            if (options.MaxSize.HasValue)
            {
                var size = options.MaxSize.Value.Value;
                AddFilter((ref FileSystemEntry entry) => entry.Length <= size);
            }
            
            // --- FILTRO SUL NOME / PERCORSO ---
            if (!string.IsNullOrEmpty(options.Pattern))
            {
                // Salviamo il flag per evitare l'accesso continuativo alla proprietà nel loop
                bool fullPath = options.MatchFullPath;

                if (matchType == PatternMatchType.Fixed)
                {
                    // Fixed: pura ricerca di sottostringa (IndexOf)
                    StringComparison comp = options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    string pattern = options.Pattern;

                    // MatchOnTarget si occupa di fornirci lo Span corretto (FileName o Percorso Completo)
                    AddFilter((ref FileSystemEntry entry) => 
                        MatchOnTarget(ref entry, fullPath, pathDaAnalizzare => pathDaAnalizzare.IndexOf(pattern.AsSpan(), comp) >= 0));
                }
                else if (matchType == PatternMatchType.Glob)
                {
                    // Glob
                    string pattern = options.Pattern;
                    bool ignoreCase = options.IgnoreCase;

                    AddFilter((ref FileSystemEntry entry) =>  // span corrisponde al percorso/nome file da analizzare
                        MatchOnTarget(ref entry, fullPath, pathDaAnalizzare => FileSystemName.MatchesSimpleExpression(pattern.AsSpan(), pathDaAnalizzare, ignoreCase)));
                }
                else
                {
                    // Regex
                    var regexOptions = RegexOptions.Compiled | RegexOptions.NonBacktracking;
                    if (options.IgnoreCase) regexOptions |= RegexOptions.IgnoreCase;
                    var regex = new Regex(options.Pattern, regexOptions);

                    // Regex.IsMatch opera a livello di buffer, no altre stringhe
                    AddFilter((ref FileSystemEntry entry) => 
                        MatchOnTarget(ref entry, fullPath, pathDaAnalizzare => regex.IsMatch(pathDaAnalizzare)));
                }
            }
            return finalFilter;
        }
    }
}