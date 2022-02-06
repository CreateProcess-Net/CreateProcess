using System.Text;

namespace CreateProcess;

// Source: https://github.com/fsprojects/FAKE/blob/release/next/src/app/Fake.Core.Process/CmdLineParsing.fs


internal static class CmdLineParsing
{
    internal static string escapeCommandLineForShell(string cmdLine)
    {
        return $"'{cmdLine.Replace("'", "'\\''")}'";
    }

    internal static void escapeBackslashes(StringBuilder sb, string s, int lastSearchIndex)
    {
        // Backslashes must be escaped if and only if they precede a double quote.
        var countSlashes = 0;
        for (var i = lastSearchIndex; i >= 0; i--)
            if (s[i] == '\\')
                countSlashes++;
        if (countSlashes > 0) sb.Append('\\', countSlashes);
    }

    internal static string windowsArgvToCommandLine(bool shorten, IEnumerable<string> args)
    {
        if (args == null)
            throw new ArgumentException("'args' cannot be null", nameof(args));
        var sb = new StringBuilder();
        var enumerator = args.GetEnumerator();
        try
        {
            while (enumerator.MoveNext())
            {
                var current = enumerator.Current;
                if (current == null)
                    throw new ArgumentException("'args' cannot contain null", nameof(args));
                StringBuilder stringBuilder;
                if (shorten && current.Length > 0)
                    if (current.IndexOfAny(new char[4]
                        {
                            ' ',
                            '"',
                            '\\',
                            '\t'
                        }) < 0)
                    {
                        stringBuilder = sb.Append(current);
                        stringBuilder = sb.Append(" ");
                        continue;
                    }

                stringBuilder = sb.Append('"');
                var startIndex = 0;
                var num = 0;
                while ((startIndex >= current.Length ? 0 : num >= 0 ? 1 : 0) != 0)
                {
                    num = current.IndexOf('"', startIndex);
                    if (num >= 0)
                    {
                        stringBuilder = sb.Append(current, startIndex, num - startIndex);
                        escapeBackslashes(sb, current, num - 1);
                        stringBuilder = sb.Append('\\');
                        stringBuilder = sb.Append('"');
                        startIndex = num + 1;
                    }
                }

                stringBuilder = sb.Append(current, startIndex, current.Length - startIndex);
                escapeBackslashes(sb, current, current.Length - 1);
                stringBuilder = sb.Append("\" ");
            }
        }
        finally
        {
            if (enumerator is IDisposable disposable)
                disposable.Dispose();
        }

        return sb.ToString(0, Math.Max(0, sb.Length - 1));
    }

    internal static string[] windowsCommandLineToArgv(string arguments)
    {
        if (arguments.Contains("\"\"\""))
            throw new ArgumentException(
                $"triple quotes are not allowed in the command line ('{arguments}') as they behave different across programs, see https://github.com/vbfox/FoxSharp/issues/1 to escape a quote use backslash and the rules from https://docs.microsoft.com/en-US/cpp/cpp/parsing-cpp-command-line-arguments?view=vs-2017.",
                nameof(arguments));
        var stringBuilder1 = new StringBuilder();
        var flag1 = false;
        var flag2 = false;
        var stringList = new List<string>();
        for (var index = 0; index < arguments.Length; ++index)
        {
            var repeatCount = 0;
            while ((index >= arguments.Length ? 0 : arguments[index] == '\\' ? 1 : 0) != 0)
            {
                ++index;
                ++repeatCount;
            }

            StringBuilder stringBuilder2;
            if (repeatCount > 0)
            {
                if (index >= arguments.Length || arguments[index] != '"')
                {
                    stringBuilder2 = stringBuilder1.Append('\\', repeatCount);
                    --index;
                }
                else
                {
                    stringBuilder2 = stringBuilder1.Append('\\', repeatCount / 2);
                    if (repeatCount % 2 == 0)
                        --index;
                    else
                        stringBuilder2 = stringBuilder1.Append('"');
                }
            }
            else
            {
                var ch = arguments[index];
                switch (ch)
                {
                    case '\t':
                        if (flag1)
                            goto default;
                        else
                            break;
                    case ' ':
                        if (flag1)
                            goto default;
                        else
                            break;
                    case '"':
                        flag2 = true;
                        flag1 = !flag1;
                        continue;
                    default:
                        stringBuilder2 = stringBuilder1.Append(ch);
                        continue;
                }

                if (stringBuilder1.Length > 0 || flag2)
                {
                    flag2 = false;
                    stringList.Add(stringBuilder1.ToString());
                    stringBuilder2 = stringBuilder1.Clear();
                }
            }
        }

        if (stringBuilder1.Length > 0 || flag2)
            stringList.Add(stringBuilder1.ToString());
        return stringList.ToArray();
    }

    internal static string toProcessStartInfo(IEnumerable<string> args)
    {
        var commandLine = windowsArgvToCommandLine(true, args);
        return Environment.isLinux ? commandLine.Replace("\\$", "\\\\$").Replace("\\`", "\\\\`") : commandLine;
    }
}

/// <summary>Helper functions for proper command line parsing</summary>
public static class Args
{
    /// <summary>
    ///     Convert the given argument list to a conforming windows command line string, escapes parameter in quotes if needed
    ///     (currently always but this might change).
    /// </summary>
    public static string toWindowsCommandLine(IEnumerable<string> args)
    {
        return CmdLineParsing.windowsArgvToCommandLine(true, args);
    }

    /// <summary>Escape the given argument list according to a unix shell (bash)</summary>
    public static string toLinuxShellCommandLine(IEnumerable<string> args)
    {
        return string.Join(" ", args.Select(CmdLineParsing.escapeCommandLineForShell));
    }

    /// <summary>Read a windows command line string into its arguments</summary>
    public static string[] fromWindowsCommandLine(string cmd)
    {
        return CmdLineParsing.windowsCommandLineToArgv(cmd);
    }
}

public record Arguments
{
    internal Arguments(IEnumerable<string> args, string? original)
    {
        Args = args.ToList();
        Original = original;
    }

    internal IReadOnlyList<string> Args { get; }
    internal string? Original { get; }

    public static Arguments Empty { get; } = new(Array.Empty<string>(), null);

    /// <summary>
    ///     See https://msdn.microsoft.com/en-us/library/17w5ykft.aspx
    /// </summary>
    public static Arguments OfWindowsCommandLine(string cmd)
    {
        return new(global::CreateProcess.Args.fromWindowsCommandLine(cmd), cmd);
    }

    /// <summary>
    ///     Create a new arguments object from the given list of arguments
    /// </summary>
    public static Arguments OfArgs(IEnumerable<string> args)
    {
        return new(args, null);
    }

    /// <summary>
    ///     Create a new arguments object from a given startinfo-conforming-escaped command line string.
    ///     Same as `OfWindowsCommandLine`.
    /// </summary>
    public static Arguments OfStartInfo(string cmd)
    {
        return OfWindowsCommandLine(cmd);
    }

    /// <summary>
    ///     This is the reverse of https://msdn.microsoft.com/en-us/library/17w5ykft.aspx
    /// </summary>
    public string WindowsCommandLine => global::CreateProcess.Args.toWindowsCommandLine(Args);

    /// <summary>
    ///     Escape the given argument list according to a unix shell (bash)
    /// </summary>
    public string LinuxShellCommandLine => global::CreateProcess.Args.toLinuxShellCommandLine(Args);

    /// <summary>
    ///     Create a new command line string which can be used in a ProcessStartInfo object.
    ///     If given, returns the exact input of `OfWindowsCommandLine` otherwise `ToWindowsCommandLine` (with some special
    ///     code for `mono`) is used.
    /// </summary>
    public string StartInfo => Original ?? CmdLineParsing.toProcessStartInfo(Args);
}

public static class ArgumentsExtensions
{
    /// <summary>
    ///     This assumes a windows command line escaped arguments string, <see cref="Arguments.OfStartInfo" />.
    /// </summary>
    public static Arguments ToArguments(this string cmd)
    {
        return Arguments.OfWindowsCommandLine(cmd);
    }

    /// <summary>
    ///     Append the given arguments before all current arguments
    /// </summary>
    public static Arguments WithPrefix(this Arguments a, IEnumerable<string> s)
    {
        return Arguments.OfArgs(s.Concat(a.Args));
    }

    /// <summary>
    ///     Append all arguments after the current arguments
    /// </summary>
    public static Arguments Append(this Arguments a, IEnumerable<string> s)
    {
        return Arguments.OfArgs(a.Args.Concat(s));
    }

    /// <summary>
    ///     Appends the given raw argument to the command line, you can not use other methods for this to work
    ///     This method is only required if you NEED quotes WITHIN your argument (some old Microsoft Tools).
    ///     "raw" methods are not compatible with non-raw methods.
    /// </summary>
    public static Arguments AppendRaw(this Arguments a, string s)
    {
        var cmd = a.StartInfo;
        var newCmd = cmd.Length == 0 ? s : cmd + " " + s;
        return new Arguments(a.Args.Concat(Enumerable.Repeat(s, 1)), newCmd);
    }

    /// <summary>
    ///     Appends the given raw argument to the command line, you can not use other methods for this to work
    ///     This allows unusual quoting with the given prefix, like /k:"myarg" ("/k:" would be the argPrefix)
    ///     This method is only required if you NEED quotes WITHIN your argument (some old Microsoft Tools).
    ///     "raw" methods are not compatible with non-raw methods.
    /// </summary>
    public static Arguments AppendRawEscaped(this Arguments a, string argPrefix, string paramValue)
    {
        if (argPrefix.IndexOfAny(new[] { ' ', '\"', '\\', '\t' }) >= 0)
            throw new ArgumentException("Argument prefix cannot contain special characters", nameof(argPrefix));

        return a.AppendRaw($"{argPrefix}{CmdLineParsing.windowsArgvToCommandLine(false, new[] { paramValue })}");
    }

    /// <summary>
    ///     Append an argument prefixed by another if the value is Some.
    ///     This method is only required if you NEED quotes WITHIN your argument (some old Microsoft Tools).
    ///     "raw" methods are not compatible with non-raw methods.
    /// </summary>
    public static Arguments AppendRawEscapedIf(this Arguments a, bool b, string argPrefix, string paramValue)
    {
        return b ? a.AppendRawEscaped(argPrefix, paramValue) : a;
    }

    /// <summary>
    ///     Append an argument prefixed by another if the value is not null.
    ///     This method is only required if you NEED quotes WITHIN your argument (some old Microsoft Tools).
    ///     "raw" methods are not compatible with non-raw methods.
    /// </summary>
    public static Arguments AppendRawEscapedNotNull(this Arguments a, string argPrefix, string? paramValue)
    {
        return a.AppendRawEscapedIf(paramValue is null, argPrefix, paramValue!);
    } 
  
    /// <summary>
    ///     Append an argument prefixed by another if the value not null or empty.
    ///     This method is only required if you NEED quotes WITHIN your argument (some old Microsoft Tools).
    ///     "raw" methods are not compatible with non-raw methods.
    /// </summary>
    public static Arguments AppendRawEscapedNotEmpty(this Arguments a, string argPrefix, string? paramValue)
    {
        return a.AppendRawEscapedIf(!string.IsNullOrEmpty(paramValue), argPrefix, paramValue!);
    }

    /// <summary>
    ///     Append an argument prefixed by another if the value is not null.
    /// </summary>
    public static Arguments AppendNotNull(this Arguments a, string paramName, string? paramValue)
    {
        return paramValue is null ? a : a.Append(new[] { paramName, paramValue });
    }

    /// <summary>
    ///     Append an argument to a command line if a condition is true.
    /// </summary>
    public static Arguments AppendIf(this Arguments a, bool value, string paramName)
    {
        return value ? a.Append(new[] { paramName }) : a;
    }


    /// <summary>
    ///     Append an argument prefixed by another if the value is not null.
    /// </summary>
    public static Arguments AppendNotEmpty(this Arguments a, string paramName, string? paramValue)
    {
        return string.IsNullOrEmpty(paramValue) ? a : a.Append(new[] { paramName, paramValue });
    }

    public static IReadOnlyList<string> ToList(this Arguments a)
    {
        return a.Args;
    }
    
    /// <summary>
    /// Create a CreateProcess from the given file and arguments
    /// </summary>
    /// <example>
    /// <code>
    ///     Arguments.OfWindowsCommandLine("test")
    ///         .ToCreateProcess("dotnet.exe")
    ///         .RunProcess()
    /// </code>
    /// You can start from an argument string and build from there (for example use external libraries like [`BlackFox.CommandLine`](https://github.com/vbfox/FoxSharp/tree/master/src/BlackFox.CommandLine)).
    /// <code>
    ///
    /// 
    ///     open BlackFox.CommandLine
    /// 
    ///     ExternalLib.Builder()
    ///         .AddArgument()
    ///         .ToArgumentString()
    ///         .ToWindowsCommandLineArguments()
    ///         .ToCreateProcess("dotnet.exe")
    ///         .RunProcess()
    /// </code>
    /// </example>
    /// <returns></returns>
    public static CreateProcess ToCreateProcess(this Arguments arguments, string executable)
    {
        return arguments
            .ToRawCommand(executable)
            .ToCreateProcess();
    }
}