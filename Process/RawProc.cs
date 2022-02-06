using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipelines;
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


internal abstract record OutputPipe
{
    private OutputPipe(){}
    internal abstract System.IO.Pipelines.PipeWriter SystemPipe { get; }
    internal abstract Func<CancellationToken, Task> WorkLoop { get; }

    internal record SimplePipe(System.IO.Pipelines.PipeWriter SystemPipe, Func<CancellationToken, Task> WorkLoop) : OutputPipe
    {
        internal override System.IO.Pipelines.PipeWriter SystemPipe { get; } = SystemPipe;
        internal override Func<CancellationToken, Task> WorkLoop { get; } = WorkLoop;
    }
    
    internal record MergedPipe(IReadOnlyList<OutputPipe> Pipes) : OutputPipe
    {
        private System.IO.Pipelines.Pipe? _pipe;
        private IReadOnlyList<OutputPipe> Pipes { get; } = Flatten(Pipes);

        private static IReadOnlyList<OutputPipe> Flatten(IReadOnlyList<OutputPipe> pipes)
        {
            var l = new List<OutputPipe>(pipes.Count);
            foreach (var pipe in pipes)
            {
                if (pipe is MergedPipe mergedPipe)
                {
                    l.AddRange(mergedPipe.Pipes);
                }
                else
                {
                    l.Add(pipe);
                }
            }

            return l;
        }

        private System.IO.Pipelines.Pipe EnsurePipe => _pipe ??= new System.IO.Pipelines.Pipe();

        internal override System.IO.Pipelines.PipeWriter SystemPipe
        {
            get { return EnsurePipe.Writer; }
        }

        internal override Func<CancellationToken, Task> WorkLoop
        {
            get
            {
                return async (ctx) =>
                {
                    var workLoops = new List<Task>(Pipes.Count);
                    workLoops.AddRange(Pipes.Select(p => p.WorkLoop(ctx)));
                    
                    var reader = EnsurePipe.Reader;
                    
                    try
                    {
                        while (true)
                        {
                            var result = await reader.ReadAsync(ctx).ConfigureAwait(false);
                            var buffer = result.Buffer;
                            var position = buffer.Start;
                            var consumed = position;

                            try
                            {
                                if (result.IsCanceled)
                                {
                                    throw new OperationCanceledException("Read has been cancelled");
                                }

                                while (buffer.TryGet(ref position, out var memory))
                                {
                                    var isCompletedFlag = false;
                                    foreach (var resultTask in Pipes.Select(p => p.SystemPipe.WriteAsync(memory, ctx)))
                                    {
                                        FlushResult flushResult = await resultTask.ConfigureAwait(false);
                                        if (flushResult.IsCanceled)
                                        {
                                            throw new OperationCanceledException("Flush of pipeline has been cancelled");
                                        }

                                        if (flushResult.IsCompleted)
                                        {
                                            Debug.Fail("No idea when this happens");
                                            isCompletedFlag = true;
                                        }
                                    }

                                    consumed = position;

                                    if (isCompletedFlag)
                                    {
                                        return;
                                    }
                                }

                                // The while loop completed successfully, so we've consumed the entire buffer.
                                consumed = buffer.End;

                                if (result.IsCompleted)
                                {
                                    break;
                                }
                            }
                            finally
                            {
                                // Advance even if WriteAsync throws so the PipeReader is not left in the
                                // currently reading state
                                reader.AdvanceTo(consumed);
                            }
                        }
                    }
                    finally
                    {
                        await reader.CompleteAsync().ConfigureAwait(false);
                    }

                    await Task.WhenAll(workLoops).ConfigureAwait(false);
                };
            }
        }
    }
}


internal abstract record InputPipe
{
    private InputPipe(){}
    internal abstract System.IO.Pipelines.PipeReader SystemPipe { get; }
    internal abstract Func<CancellationToken, Task> WorkLoop { get; }

    internal record SimplePipe(System.IO.Pipelines.PipeReader SystemPipe, Func<CancellationToken, Task> WorkLoop) : InputPipe
    {
        internal override System.IO.Pipelines.PipeReader SystemPipe { get; } = SystemPipe;
        internal override Func<CancellationToken, Task> WorkLoop { get; } = WorkLoop;
    }
    
}

/// <summary>
/// Various options to redirect streams.
/// </summary>
internal record OutputStreamSpecification
{
    public bool IsInherit { get; }
    private OutputStreamSpecification(bool isInherit)
    {
        this.IsInherit = IsInherit;
    }

    /// <summary>
    /// Do not redirect, or use normal process inheritance
    /// </summary>
    public record Inherit() : OutputStreamSpecification(true);

    /// <summary>
    /// Redirect to the given Pipe
    /// </summary>
    public record UsePipe(OutputPipe Pipe) : OutputStreamSpecification(false);
}

/// <summary>
/// Various options to redirect streams.
/// </summary>
internal record InputStreamSpecification
{
    public bool IsInherit { get; }
    
    private InputStreamSpecification(bool isInherit)
    {
        this.IsInherit = IsInherit;
    }

    /// <summary>
    /// Do not redirect, or use normal process inheritance
    /// </summary>
    public record Inherit() : InputStreamSpecification(true);

    /// <summary>
    /// Redirect to the given Pipe
    /// </summary>
    public record UsePipe(InputPipe Pipe) : InputStreamSpecification(false);
}

internal record StreamSpecs(InputStreamSpecification StandardInput, OutputStreamSpecification StandardOutput,
    OutputStreamSpecification StandardError)
{
    public void SetStartInfo(ProcessStartInfo p)
    {
        p.RedirectStandardInput = !StandardInput.IsInherit;
        p.RedirectStandardOutput = !StandardOutput.IsInherit;
        p.RedirectStandardError = !StandardError.IsInherit;
    }
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
               

                async Task HandleUseOutputPipe(OutputStreamSpecification.UsePipe usePipe, Stream processStream)
                {
                    var workTask = usePipe.Pipe.WorkLoop(tok.Token);
                    var writer = usePipe.Pipe.SystemPipe; // Why is there no CopyFromAsync Helper?
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
                
                async Task HandleUseInputPipe(InputStreamSpecification.UsePipe usePipe, Stream processStream)
                {
                    var workTask = usePipe.Pipe.WorkLoop(tok.Token);
                    await usePipe.Pipe.SystemPipe.CopyToAsync(processStream, tok.Token);
                    await workTask;
                    processStream.Close();
                }
                
                async Task HandleInputStream(InputStreamSpecification parameter, Stream processStream)
                {
                    switch (parameter)
                    {
                        case InputStreamSpecification.Inherit:
                            throw new InvalidOperationException("Unexpected value");
                        case InputStreamSpecification.UsePipe usePipe:
                            await HandleUseInputPipe(usePipe, processStream);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(parameter), parameter, null);
                    }
                }

                async Task HandleOutputStream(OutputStreamSpecification parameter, Stream processStream)
                {
                    switch (parameter)
                    {
                        case OutputStreamSpecification.Inherit:
                            throw new InvalidOperationException("Unexpected value");
                        case OutputStreamSpecification.UsePipe usePipe:
                            await HandleUseOutputPipe(usePipe, processStream);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(parameter), parameter, null);
                    }
                }

                if (p.RedirectStandardInput)
                {
                    redirectStdInTask = HandleInputStream(streamSpec.StandardInput, toolProcess.StandardInput.BaseStream);
                }
                if (p.RedirectStandardOutput)
                {
                    readOutputTask = HandleOutputStream(streamSpec.StandardOutput, toolProcess.StandardOutput.BaseStream);
                }
                if (p.RedirectStandardError)
                {
                    readErrorTask = HandleOutputStream(streamSpec.StandardError, toolProcess.StandardError.BaseStream);
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