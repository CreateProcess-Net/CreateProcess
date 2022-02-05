using System.Collections.Immutable;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;

namespace Process;

public record EnvMap(ImmutableDictionary<string, string> Dictionary)
{
    public static EnvMap Empty { get; } =
        new EnvMap(Environment.isWindows
            ? ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase)
            : ImmutableDictionary<string, string>.Empty);

    public static EnvMap OfEnumerable(IEnumerable<KeyValuePair<string, string>> l)
    {
        return new EnvMap(Empty.Dictionary.AddRange(l));
    }
    
    public static EnvMap Create()
    {
        return OfEnumerable(System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Select(e => new KeyValuePair<string,string>(e.Key.ToString()!, e.Value?.ToString()!)));
    }
    
    public EnvMap Replace(IEnumerable<KeyValuePair<string, string>> l)
    {
        return new EnvMap(Dictionary.SetItems(l));
    }
    
    public static EnvMap OfMap(IDictionary<string, string> l)
    {
        return Create().Replace(l);
    }

    public EnvMap Set(string key, string value)
    {
        return new EnvMap(Dictionary.SetItem(key, value));
    }
}

public record Command
{
    private Command(){}
    public record Shell(string Command) : Command;

    /// <summary>
    /// Windows: https://msdn.microsoft.com/en-us/library/windows/desktop/bb776391(v=vs.85).aspx
    /// Linux(mono): https://github.com/mono/mono/blob/0bcbe39b148bb498742fc68416f8293ccd350fb6/eglib/src/gshell.c#L32-L104 (because we need to create a commandline string internally which need to go through that code)
    /// Linux(netcore): See https://github.com/fsharp/FAKE/pull/1281/commits/285e585ec459ac7b89ca4897d1323c5a5b7e4558 and https://github.com/dotnet/corefx/blob/master/src/System.Diagnostics.Process/src/System/Diagnostics/Process.Unix.cs#L443-L522
    /// </summary>
    public record Raw : Command
    {
        public Raw(string executable, Arguments arguments)
        {
            _executable = executable;
            _arguments = arguments;
        }

        internal readonly string _executable;
        internal readonly Arguments _arguments;
    }

    public string CommandLine
    {
        get
        {
            switch (this)
            {
                case Shell s:
                    return s.Command;
                case Raw r:
                    return $"{r._executable} {r._arguments.WindowsCommandLine}";
                default:
                    throw InvalidCase();
            }
        }
    }
    
    public Arguments Arguments
    {
        get
        {
            switch (this)
            {
                case Shell s:
                    throw new NotSupportedException($"Cannot retrieve Arguments for Command.Shell");
                case Raw r:
                    return r._arguments;
                default:
                    throw InvalidCase();
            }
        }
    }

    public string Executable
    {
        get
        {
            switch (this)
            {
                case Shell s:
                    throw new NotSupportedException($"Cannot retrieve Executable for Command.Shell");
                case Raw r:
                    return r._executable;
                default:
                    throw InvalidCase();
            }
        }
    }
    private Exception InvalidCase()
    {
        return new InvalidOperationException($"Unexpected case {GetType().FullName}");
    }
}

public static class CommandExtensions
{
    public static Command ToRawCommand(this Arguments arguments, string executable)
    {
        return new Command.Raw(executable, arguments);
    }
    
    public static Command ReplaceExecutable(this Command command, string executable)
    {
        return command switch
        {
            Command.Raw raw => new Command.Raw(executable, raw._arguments),
            Command.Shell s => throw new InvalidOperationException("Expected RawCommand"),
            _ => throw new InvalidOperationException($"Unknown type {command.GetType().FullName}")
        };
    }
}

/*
/// <summary>
/// Represents basically an "out" parameter, allows to retrieve a value after a certain point in time.
/// Used to retrieve "pipes"
/// </summary>
public record DataRef<T>
{
    internal bool _wasSet = false;
    internal T? _value;
    internal Action<T> _onSet;

    internal DataRef(bool wasSet, T? value, Action<T> onSet)
    {
        _wasSet = wasSet;
        _value = value;
        _onSet = onSet;
    }

    public T Value
    {
        get
        {
            if (!_wasSet) throw new InvalidOperationException("Cannot retrieve data cell before it has been set!");
            return _value!;
        }
    }
}

public static class DataRef
{
    public static DataRef<T> CreateEmpty<T>() => new DataRef<T>(false, default, v => { });

    public static DataRef<TNew> Select<TOld, TNew>(this DataRef<TOld> oldRef, Func<TOld, TNew> mapping)
    {
        var newCell = new DataRef<TNew>(oldRef._wasSet, oldRef._wasSet ? mapping(oldRef._value!) : default,
            _ => throw new InvalidOperationException("Cannot set this ref cell"));
        var previousSet = oldRef._onSet;
        oldRef._onSet = (setVal) =>
        {
            oldRef._wasSet = true;
            oldRef._value = setVal;
            previousSet(setVal);
            newCell._wasSet = true;
            newCell._value = mapping(setVal);
        };
        return newCell;
    }
}*/

public record Pipe(System.IO.Pipelines.Pipe SystemPipe, Func<CancellationToken, Task> WorkLoop);


/// <summary>
/// Various options to redirect streams.
/// </summary>
public record StreamSpecification
{
    private StreamSpecification(){}

    /// <summary>
    /// Do not redirect, or use normal process inheritance
    /// </summary>
    public record Inherit() : StreamSpecification;

    /// <summary>
    /// Redirect to the given Pipe
    /// </summary>
    public record UsePipe(Pipe Pipe) : StreamSpecification;
}

internal record StreamSpecs(StreamSpecification StandardInput, StreamSpecification StandardOutput,
    StreamSpecification StandardError)
{
    public void SetStartInfo(ProcessStartInfo p)
    {
        bool IsInherit(StreamSpecification s) => s switch
        {
            StreamSpecification.Inherit => true,
            StreamSpecification.UsePipe => false,
            _ => throw new ArgumentOutOfRangeException(nameof(s), s, null)
        };

        p.RedirectStandardInput = !IsInherit(StandardInput);
        p.RedirectStandardOutput = !IsInherit(StandardOutput);
        p.RedirectStandardError = !IsInherit(StandardError);
    }
}

internal interface IRawProcessHook
{
    StreamSpecs Prepare(StreamSpecs specs);
    void OnStart(System.Diagnostics.Process process);
}

internal record RawCreateProcess(
    Command Command, bool TraceCommand, string? WorkingDirectory,
    EnvMap? Environment, StreamSpecs Streams)
{
    internal ProcessStartInfo StartInfo
    {
        get
        {
            var p = new ProcessStartInfo();
            switch (Command)
            {
                case Command.Shell s:
                    p.UseShellExecute = true;
                    p.FileName = s.Command;
                    p.Arguments = null;
                    break;
                case Command.Raw r:
                    p.UseShellExecute = false;
                    p.FileName = r._executable;
                    p.Arguments = r._arguments.StartInfo;
                    break;
                default:
                    throw new InvalidOperationException($"Invalid type {Command.GetType().FullName}");
            }

            void SetEnv(string key, string val) => p.Environment[key] = val;
            if (Environment != null)
            {
                p.Environment.Clear();
                foreach (var kv in Environment.Dictionary)
                {
                    SetEnv(kv.Key, kv.Value);
                }
            }

            if (WorkingDirectory != null)
            {
                p.WorkingDirectory = WorkingDirectory;
            }

            return p;
        }
    }


    public string CommandLine => Command.CommandLine;
}

public record RawProcessResult(int RawExitCode);

internal interface IProcessStarter
{
    Task<RawProcessResult> Start(RawCreateProcess createProcess);
}

internal static class RawProc
{
    internal class CreateProcessStarter : IProcessStarter
    {
        private readonly Action<RawCreateProcess, System.Diagnostics.Process> _startProcessRaw;

        public CreateProcessStarter(Action<RawCreateProcess, System.Diagnostics.Process> startProcessRaw)
        {
            _startProcessRaw = startProcessRaw;
        }

        public Task<RawProcessResult> Start(RawCreateProcess createProcess)
        {
            var p = createProcess.StartInfo;
            var toolProcess = new System.Diagnostics.Process() { StartInfo = p };
            var isStarted = false;
            var streamSpec = createProcess.Streams;
            var startTrigger = new System.Threading.Tasks.TaskCompletionSource<bool>();
            Task readOutputTask = System.Threading.Tasks.Task.FromResult(Stream.Null);
            Task readErrorTask   = System.Threading.Tasks.Task.FromResult(Stream.Null);
            Task redirectStdInTask  = System.Threading.Tasks.Task.FromResult(Stream.Null);
            var tok = new System.Threading.CancellationTokenSource();

            // Subscribe to event
            var exitEvent =
                Observable.FromEventPattern(
                    handler => toolProcess.Exited += handler,
                    handler => toolProcess.Exited -= handler).FirstAsync().ToTask();

            if (!isStarted)
            {
                toolProcess.EnableRaisingEvents = true;
                
                _startProcessRaw(createProcess, toolProcess);
               

                async Task HandleUsePipe(StreamSpecification.UsePipe usePipe, Stream processStream, bool isInputStream)
                {
                    if (isInputStream)
                    {
                        var workTask = usePipe.Pipe.WorkLoop(tok.Token);
                        await usePipe.Pipe.SystemPipe.Reader.CopyToAsync(processStream, tok.Token);
                        await workTask;
                        processStream.Close();
                    }
                    else
                    {
                        var workTask = usePipe.Pipe.WorkLoop(tok.Token);
                        var writer = usePipe.Pipe.SystemPipe.Writer; // Why is there no CopyFromAsync Helper?
                        int read;
                        do
                        {
                            Memory<byte> memory = writer.GetMemory(81920);
                            read = await processStream.ReadAsync(memory, tok.Token);
                            writer.Advance(read);
                            await writer.FlushAsync(tok.Token);
                        } while (read > 0);
                        await workTask;
                    }
                }
                
                
                async Task HandleStream(StreamSpecification originalParameter, StreamSpecification parameter, Stream processStream, bool isInputStream)
                {
                    switch (parameter)
                    {
                        case StreamSpecification.Inherit:
                            throw new InvalidOperationException("Unexpected value");
                        case StreamSpecification.UsePipe usePipe:
                            await HandleUsePipe(usePipe, processStream, isInputStream);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(parameter), parameter, null);
                    }
                }

                if (p.RedirectStandardInput)
                {
                    redirectStdInTask = HandleStream(createProcess.Streams.StandardInput, streamSpec.StandardInput,
                        toolProcess.StandardInput.BaseStream, true);
                }
                if (p.RedirectStandardOutput)
                {
                    readOutputTask = HandleStream(createProcess.Streams.StandardOutput, streamSpec.StandardOutput,
                        toolProcess.StandardOutput.BaseStream, false);
                }
                if (p.RedirectStandardError)
                {
                    readErrorTask = HandleStream(createProcess.Streams.StandardError, streamSpec.StandardError,
                        toolProcess.StandardError.BaseStream, false);
                }
            }

            return Task.Run(async () =>
            {
                await exitEvent;
                // Waiting for the process to exit (buffers)
                //await toolProcess.WaitForExitAsync().ConfigureAwait(false);
                toolProcess.WaitForExit();
                var code = toolProcess.ExitCode;
                toolProcess.Dispose();

                var all = Task.WhenAll(readErrorTask, readOutputTask, redirectStdInTask);

                async Task<bool> TryWait()
                {
                    var delay = Task.Delay(500);
                    var t = await Task.WhenAny(all, delay).ConfigureAwait(false);
                    return t != delay;
                }

                var allFinished = false;
                var retries = 10;
                while (!allFinished && retries > 0)
                {
                    var ok = await TryWait().ConfigureAwait(false);
                    retries--;
                    if (retries == 2)
                    {
                        tok.Cancel();
                    }

                    if (!ok && retries < 6)
                    {
                        Console.Error.WriteLine($"At least on redirection task did not finish: \nReadErrorTask: {readErrorTask.Status}\nReadOutputTask: {readOutputTask.Status}\nRedirectStdInTask: {redirectStdInTask.Status}");
                    }

                    allFinished = ok;
                }
                
                // wait for finish.
                if (!allFinished)
                {
                    if (System.Environment.GetEnvironmentVariable("PROC_ATTACH_DEBUGGER") == "true")
                    {
                        Debugger.Launch();
                        Debugger.Break();
                    }

                    if (System.Environment.GetEnvironmentVariable("PROC_FAIL_PROCESS_HANG") == "true")
                    {
                        System.Environment.FailFast($"At least on redirection task did not finish: \nReadErrorTask: {readErrorTask.Status}\nReadOutputTask: {readOutputTask.Status}\nRedirectStdInTask: {redirectStdInTask.Status}");
                    }
                    
                    var msg =
                        "We encountered https://github.com/fsharp/FAKE/issues/2401, please help to resolve this issue! You can set 'PROC_IGNORE_PROCESS_HANG' to true to ignore this error. But please consider sending a full process dump or volunteer with debugging.";
                    if (System.Environment.GetEnvironmentVariable("FAKE_IGNORE_PROCESS_HANG") != "true")
                    {
                        throw new InvalidOperationException(msg);
                    }
                    else
                    {
                        Console.Error.WriteLine(msg);
                    }
                }

                return new RawProcessResult(code);
            });
        }
    }
}