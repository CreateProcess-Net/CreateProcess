using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reflection.Metadata;

namespace Process;

/// <summary>
/// Hook for events when an CreateProcess is executed.
/// </summary>
internal interface IProcessHook<TState, TRes> where TState : IDisposable?
{
    TState PrepareState();
    StreamSpecs PrepareStreams(TState state, StreamSpecs specs);
    void ProcessStarted(TState state, System.Diagnostics.Process process);
    Task<TRes> RetrieveResult (TState state,  System.Threading.Tasks.Task<RawProcessResult> rawResult);
}

internal static class ProcessHookExtensions
{
    public static IProcessHook<IDisposable, TRes> ToRawHook<TState, TRes>(this IProcessHook<TState, TRes> h)
        where TState : IDisposable
    {
        return new RawHookWrapper<TState, TRes>(h);
    }

    private class RawHookWrapper<TState, TRes> : IProcessHook<IDisposable, TRes> where TState: IDisposable
    {
        private readonly IProcessHook<TState, TRes> _processHook;

        public RawHookWrapper(IProcessHook<TState, TRes> processHook)
        {
            _processHook = processHook;
        }

        public IDisposable PrepareState()
        {
            return _processHook.PrepareState();
        }

        public StreamSpecs PrepareStreams(IDisposable disposable, StreamSpecs specs)
        {
            return _processHook.PrepareStreams((TState)disposable, specs);
        }

        public void ProcessStarted(IDisposable disposable, System.Diagnostics.Process process)
        {
            _processHook.ProcessStarted((TState)disposable, process);
        }

        public Task<TRes> RetrieveResult(IDisposable disposable, Task<RawProcessResult> rawResult)
        {
            return _processHook.RetrieveResult((TState)disposable, rawResult);
        }
    }
}
/// <summary>
/// The output of the process. If ordering between stdout and stderr is important you need to use streams.
/// </summary>
public record ProcessOutput(string Output, string Error);

public record ProcessResult<T>(T Result, int ExitCode);

public class InternalCreateProcessData<TRes>
{
    internal IProcessHook<IDisposable?, TRes> Hook { get; }
    internal StreamSpecs Specs { get; }
    internal bool TraceCommand { get; }

    internal InternalCreateProcessData(bool traceCommand, StreamSpecs specs, IProcessHook<IDisposable?, TRes> hook)
    {
        Hook = hook;
        Specs = specs;
        TraceCommand = traceCommand;
    }

    internal InternalCreateProcessData<TRes> WithStreams(StreamSpecs newSpec)
    {
        return new InternalCreateProcessData<TRes>(TraceCommand, newSpec, Hook);
    }
    internal InternalCreateProcessData<TOut> WithHook<TOut>(IProcessHook<IDisposable?, TOut> newHook)
    {
        return new InternalCreateProcessData<TOut>(TraceCommand, Specs, newHook);
    }

    public InternalCreateProcessData<TRes> WithDisableTrace()
    {
        return new InternalCreateProcessData<TRes>(false, Specs, Hook);
    }
}

public record RedirectSpecification
{
}
public static class Redirect
{
    public static RedirectSpecification ToFile(string file)
    {
        return new RedirectSpecification();
    }
}

public abstract record CreateProcessRaw
{
    
}

public record CreateProcess<TRes>(Command Command, string? WorkingDirectory, EnvMap? Environment, InternalCreateProcessData<TRes> InternalData) : CreateProcessRaw
{
    public string CommandLine => Command.CommandLine;

    public static CreateProcess<TRes> operator >(CreateProcess<TRes> res, RedirectSpecification spec)
    {
        return res;
    }
    
    public static CreateProcess<TRes> operator <(CreateProcess<TRes> res, RedirectSpecification spec)
    {
        return res;
    }
    
    public static ShellCommand<TRes> operator |(CreateProcessRaw left, CreateProcess<TRes> right)
    {
        return new ShellCommand<TRes>.ProcessPipeline(left, right);
    }
}

public record StartedProcessInfo
{
    internal readonly System.Diagnostics.Process _internalprocess;
    internal StartedProcessInfo(System.Diagnostics.Process internalprocess)
    {
        _internalprocess = internalprocess;
    }

    public System.Diagnostics.Process Process => _internalprocess;
}


internal class RefCell<T>
{
    public T Data { get; set; }
}

internal static class RefCell
{
    public static RefCell<T> Create<T>(T data) => new RefCell<T>() { Data = data };
}

public static class CreateProcess
{
    internal static IProcessHook<IDisposable?, ProcessResult<Unit>> EmptyHook { get; } = new EmptyHookImpl();

    private class EmptyHookImpl : IProcessHook<IDisposable?,ProcessResult<Unit>>
    {
        public IDisposable? PrepareState()
        {
            return null;
        }

        public StreamSpecs PrepareStreams(IDisposable? state, StreamSpecs specs)
        {
            return specs;
        }

        public void ProcessStarted(IDisposable? state, System.Diagnostics.Process process)
        {
        }

        public async Task<ProcessResult<Unit>> RetrieveResult(IDisposable? state, Task<RawProcessResult> rawResult)
        {
            var r = await rawResult;
            return new ProcessResult<Unit>(Unit.Default, r.RawExitCode);
        }
    }

    /// <summary>
    /// Create a simple <see cref="CreateProcess{TRes}"/> instance from the given command.
    /// </summary>
    /// <example>
    /// <code>
    ///     new Command.Raw("file", Arguments.OfArgs(new []{ "arg1", "arg2" })
    ///         .ToCreateProcess()
    ///         .RunProcess()
    /// </code>
    /// </example>
    public static CreateProcess<ProcessResult<Unit>> ToCreateProcess(this Command command)
    {
        return new CreateProcess<ProcessResult<Unit>>(
            command, null, null, new InternalCreateProcessData<ProcessResult<Unit>>(
                true, 
                new StreamSpecs(new StreamSpecification.Inherit(), new StreamSpecification.Inherit(),
                    new StreamSpecification.Inherit()),
                EmptyHook));
    }

    /// <summary>
    /// Create a CreateProcess from the given file and arguments
    /// </summary>
    /// <example>
    /// <code>
    ///     CreateProcess.FromRawCommandLine("cmd", "/C \"echo test\"")
    ///         .RunProcess()
    /// </code>
    /// </example>
    /// <returns></returns>
    public static CreateProcess<ProcessResult<Unit>> FromRawCommandLine(string executable, string arguments)
    {
        return Arguments.OfWindowsCommandLine(arguments)
            .ToRawCommand(executable)
            .ToCreateProcess();
    }
    
    public static CreateProcess<ProcessResult<Unit>> FromCommandLine(string executable, IEnumerable<string> arguments)
    {
        return Arguments.OfArgs(arguments)
            .ToRawCommand(executable)
            .ToCreateProcess();
    }
    
    public static CreateProcess<ProcessResult<Unit>> FromCommandLine(string executable, params string[] arguments)
    {
        return FromCommandLine(executable, (IEnumerable<string>)arguments);
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
    public static CreateProcess<ProcessResult<Unit>> ToCreateProcess(this Arguments arguments, string executable)
    {
        return arguments
            .ToRawCommand(executable)
            .ToCreateProcess();
    }

    public static CreateProcess<ProcessResult<Unit>> ToCreateProcess(
        this System.Diagnostics.ProcessStartInfo startInfo)
    {
        return new CreateProcess<ProcessResult<Unit>>(
            startInfo.UseShellExecute
                ? new Command.Shell(startInfo.FileName)
                : new Command.Raw(startInfo.FileName, startInfo.Arguments.ToArguments()),
            string.IsNullOrWhiteSpace(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
            EnvMap.OfEnumerable(startInfo.Environment),
            new InternalCreateProcessData<ProcessResult<Unit>>(
                true, 
                new StreamSpecs(
                    startInfo.RedirectStandardInput
                        ? new StreamSpecification.UsePipe(new Pipe(new System.IO.Pipelines.Pipe(), _ => Task.CompletedTask))
                        : new StreamSpecification.Inherit(),
                    startInfo.RedirectStandardOutput
                        ? new StreamSpecification.UsePipe(new Pipe(new System.IO.Pipelines.Pipe(), _ => Task.CompletedTask))
                        : new StreamSpecification.Inherit(),
                    startInfo.RedirectStandardError
                        ? new StreamSpecification.UsePipe(new Pipe(new System.IO.Pipelines.Pipe(), _ => Task.CompletedTask))
                        : new StreamSpecification.Inherit()
                ),
                EmptyHook)
           
        );
    }

    internal static StreamSpecification InterceptStreamFallback(this StreamSpecification spec,
        Func<StreamSpecification> onInherit, Pipe target)
    {
        switch (spec)
        {
            case StreamSpecification.Inherit:
                return onInherit();
            case StreamSpecification.UsePipe usePipe:
                var pipe = new System.IO.Pipelines.Pipe();
                var workTask = async (CancellationToken tok) =>
                {
                    var interceptWriter = target.SystemPipe.Writer;
                    var oldWriter = usePipe.Pipe.SystemPipe.Writer;
                    var interceptTask = target.WorkLoop(tok);
                    var oldTask = usePipe.Pipe.WorkLoop(tok);
                    var reader = pipe.Reader;
                    
                    try
                    {
                        while (true)
                        {
                            var result = await reader.ReadAsync(tok).ConfigureAwait(false);
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
                                    var interceptFlushResultTask = interceptWriter.WriteAsync(memory, tok);
                                    var oldWriterFlushResultTask = oldWriter.WriteAsync(memory, tok);
                                    FlushResult interceptFlushResult = await interceptFlushResultTask.ConfigureAwait(false);
                                    if (interceptFlushResult.IsCanceled)
                                    {
                                        throw new OperationCanceledException("Flush of intercept pipeline has been cancelled");
                                    }
                                    FlushResult oldWriterFlushResult = await oldWriterFlushResultTask.ConfigureAwait(false);
                                    if (oldWriterFlushResult.IsCanceled)
                                    {
                                        throw new OperationCanceledException("Flush of old pipeline has been cancelled");
                                    }

                                    consumed = position;

                                    if (interceptFlushResult.IsCompleted || oldWriterFlushResult.IsCompleted)
                                    {
                                        Debug.Fail("No idea when this happens");
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

                    await interceptTask.ConfigureAwait(false);
                    await oldTask.ConfigureAwait(false);
                };

                return new StreamSpecification.UsePipe(new Pipe(pipe, workTask));
            default:
                throw new InvalidOperationException($"Unknown type {spec.GetType().FullName}");
        }
    }

    public static StreamSpecification InterceptStream(this StreamSpecification spec, Pipe target)
    {
        return spec.InterceptStreamFallback(
            () => throw new InvalidOperationException(
                "cannot intercept stream when it is not redirected. Please redirect the stream first!"),
            target);
    }

    public static CreateProcess<T> CopyRedirectedProcessOutputsToStandardOutputs<T>(this CreateProcess<T> createProcess)
    {
        var outputPipe = new System.IO.Pipelines.Pipe();
        var outputWorkTask = async (CancellationToken tok) =>
        {
            var stdOut = System.Console.OpenStandardOutput();
            await outputPipe.Reader.CopyToAsync(stdOut, tok).ConfigureAwait(false);
        };
        
        var errorPipe = new System.IO.Pipelines.Pipe();
        var errorWorkTask = async (CancellationToken tok) =>
        {
            var stdOut = System.Console.OpenStandardOutput();
            await outputPipe.Reader.CopyToAsync(stdOut, tok).ConfigureAwait(false);
        };
        return createProcess with
        {
            InternalData = createProcess.InternalData.WithStreams(
                new StreamSpecs(
                    createProcess.InternalData.Specs.StandardInput,
                    createProcess.InternalData.Specs.StandardOutput.InterceptStream(
                        new Pipe(outputPipe, outputWorkTask)),
                    createProcess.InternalData.Specs.StandardError.InterceptStream(
                        new Pipe(errorPipe, errorWorkTask))

                ))
        };
    }

    public static CreateProcess<T> DisableTraceCommand<T>(this CreateProcess<T> createProcess)
    {
        return createProcess with { InternalData = createProcess.InternalData.WithDisableTrace() };
    }
    
    public static CreateProcess<T> WithWorkingDirectory<T>(this CreateProcess<T> createProcess, string? workingDirectory)
    {
        return createProcess with { WorkingDirectory = workingDirectory };
    }
    
    public static CreateProcess<T> WithCommand<T>(this CreateProcess<T> createProcess, Command command)
    {
        return createProcess with { Command = command };
    }
    
    public static CreateProcess<T> ReplaceExecutable<T>(this CreateProcess<T> createProcess, string newExecutable)
    {
        return createProcess with { Command = createProcess.Command.ReplaceExecutable(newExecutable) };
    }

    public static CreateProcess<T> MapExecutable<T>(this CreateProcess<T> createProcess, Func<string, string> executableMapping)
    {
        return createProcess with { Command = createProcess.Command.ReplaceExecutable(executableMapping(createProcess.Command.Executable)) };
    }

    internal static CreateProcess<TOut> WithHook<TIn, TOut>(this CreateProcess<TIn> createProcess,
        IProcessHook<IDisposable, TOut> hook)
    {
        return new CreateProcess<TOut>(
            createProcess.Command, createProcess.WorkingDirectory, createProcess.Environment,
            createProcess.InternalData.WithHook(hook));
    }
    
    internal static CreateProcess<TOut> WithHook<TIn, TOut, TState>(this CreateProcess<TIn> createProcess,
        IProcessHook<TState, TOut> hook) where TState : IDisposable
    {
        return createProcess.WithHook(hook.ToRawHook());
    }
    
    internal static IProcessHook<TState, TRes> SimpleHook<TState, TRes>(
        Func<TState> prepareState,
        Func<TState, StreamSpecs, StreamSpecs> prepareStreams,
        Action<TState, System.Diagnostics.Process> onStart,
        Func<TState, Task<RawProcessResult>, Task<TRes>> onResult) where TState : IDisposable
    {
        return new SimpleHookImpl<TState, TRes>(prepareState, prepareStreams, onStart, onResult);
    }

    internal record SimpleHookImpl<TState, TRes>(
        Func<TState> prepareState, 
        Func<TState, StreamSpecs, StreamSpecs> prepareStreams,
        Action<TState, System.Diagnostics.Process> onStart,
        Func<TState, Task<RawProcessResult>, Task<TRes>> onResult) : IProcessHook<TState, TRes> where TState : IDisposable
    {
        public TState PrepareState()
        {
            return prepareState();
        }

        public StreamSpecs PrepareStreams(TState disposable, StreamSpecs specs)
        {
            return prepareStreams(disposable, specs);
        }

        public void ProcessStarted(TState disposable, System.Diagnostics.Process process)
        {
            onStart(disposable, process);
        }

        public Task<TRes> RetrieveResult(TState disposable, Task<RawProcessResult> rawResult)
        {
            return onResult(disposable, rawResult);
        }
    }

    internal record CombinedState<T>(IDisposable? State1, T State2) : IDisposable where T : IDisposable
    {
        public void Dispose()
        {
            State1?.Dispose();
            State2.Dispose();
        }
    }
    
    
    public record CombinedResult<T1, T2>(Task<T1> First, Task<T2> Second)
    {
        public static implicit operator Task<(T1 first, T2 second)>(CombinedResult<T1, T2> d) 
            => Task.Run(async () => (await d.First, await d.Second));
    }
    
    internal static IProcessHook<CombinedState<TState>, TResOut> HookAppendFuncs<TState, TResOut, TResIn>(
        this IProcessHook<IDisposable?, TResIn> c,
        Func<TState> prepareState,
        Func<TState, StreamSpecs, StreamSpecs> prepareStreams,
        Action<TState, System.Diagnostics.Process> onStart,
        Func<Task<TResIn>, TState, Task<RawProcessResult>, Task<TResOut>> onResult) where TState : IDisposable
    {
        return new HookAppendFuncsImpl<TState, TResOut, TResIn>(c, prepareState, prepareStreams, onStart, onResult);
    }

    internal record HookAppendFuncsImpl<TState, TResOut, TResIn>(
        IProcessHook<IDisposable?, TResIn> c,
        Func<TState> prepareState,
        Func<TState, StreamSpecs, StreamSpecs> prepareStreams,
        Action<TState, System.Diagnostics.Process> onStart,
        Func<Task<TResIn>, TState, Task<RawProcessResult>, Task<TResOut>> onResult) 
        : IProcessHook<CombinedState<TState>, TResOut> where TState : IDisposable
    {
        public CombinedState<TState> PrepareState()
        {
            var state1 = c.PrepareState();
            var state2 = prepareState();
            return new CombinedState<TState>(state1, state2);
        }

        public StreamSpecs PrepareStreams(CombinedState<TState> state, StreamSpecs specs)
        {
            var newStreams = c.PrepareStreams(state.State1, specs);
            var finalStreams = prepareStreams(state.State2, newStreams);
            return finalStreams;
        }

        public void ProcessStarted(CombinedState<TState> state, System.Diagnostics.Process process)
        {
            c.ProcessStarted(state.State1, process);
            onStart(state.State2, process);
        }

        public Task<TResOut> RetrieveResult(CombinedState<TState> state, Task<RawProcessResult> rawResult)
        {
            var d = c.RetrieveResult(state.State1, rawResult);
            return onResult(d, state.State2, rawResult);
        }
    }

    internal static CreateProcess<TOut> AppendFuncs<TIn, TOut, TState>(
        this CreateProcess<TIn> createProcess,
        Func<TState> prepareState,
        Func<TState, StreamSpecs, StreamSpecs> prepareStreams,
        Action<TState, System.Diagnostics.Process> onStart,
        Func<Task<TIn>, TState, Task<RawProcessResult>, Task<TOut>> onResult) where TState : IDisposable
    {
        return createProcess.WithHook(
            createProcess.InternalData.Hook.HookAppendFuncs(
                prepareState, prepareStreams, onStart, onResult));
    }

    internal record DisposableWrapper<T>(T State, Action<T> OnDispose) : IDisposable
    {
        public void Dispose()
        {
            OnDispose(State);
        }
    }

    internal static CreateProcess<TOut> AppendFuncsDispose<TIn, TOut, TState>(
        this CreateProcess<TIn> createProcess,
        Func<TState> prepareState,
        Func<TState, StreamSpecs, StreamSpecs> prepareStreams,
        Action<TState, System.Diagnostics.Process> onStart,
        Func<Task<TIn>, TState, Task<RawProcessResult>, Task<TOut>> onResult,
        Action<TState> onDispose)
    {
        return createProcess.AppendFuncs(
            () =>
            {
                var state = prepareState();
                return new DisposableWrapper<TState>(state, onDispose);
            },
            (state, streams) => prepareStreams(state.State, streams),
            (state, process) => onStart(state.State, process),
            (prev, state, exitCode) => onResult(prev, state.State, exitCode)
        );
    }

    /// <summary>
    /// Attaches the given functions to the current CreateProcess instance.
    /// </summary>
    public static CreateProcess<TOut> AppendSimpleFuncs<TIn, TOut, TState>(
        this CreateProcess<TIn> createProcess,
        Func<TState> prepareState,
        Action<TState, System.Diagnostics.Process> onStart,
        Func<Task<TIn>, TState, Task<RawProcessResult>, Task<TOut>> onResult,
        Action<TState> onDispose)
    {
        return createProcess.AppendFuncsDispose(
            prepareState, (state, streams) => streams, onStart, onResult, onDispose);
    }
    
    /// <summary>
    /// Attaches the given functions to the current CreateProcess instance.
    /// </summary>
    public static CreateProcess<TOut> AppendStatelessFuncs<TIn, TOut>(
        this CreateProcess<TIn> createProcess,
        Action prepareState,
        Action<System.Diagnostics.Process> onStart,
        Func<Task<TIn>, Task<RawProcessResult>, Task<TOut>> onResult,
        Action onDispose)
    {
        return createProcess.AppendSimpleFuncs(
            () =>
            {
                prepareState();
                return Unit.Default;
            }, 
            (state, process) => onStart(process), 
            (inTask, state, exitCode) => onResult(inTask, exitCode),
            state => onDispose());
    }
    
    /// <summary>
    /// Attaches the given functions to the current CreateProcess instance.
    /// </summary>
    public static CreateProcess<T> AddOnSetup<T>(
        this CreateProcess<T> createProcess,
        Action f)
    {
        return createProcess.AppendStatelessFuncs(
            f, 
            (p) => { }, 
            (prev, exitCode) => prev, 
            () => { });
    }
    
    /// <summary>
    /// Execute the given function when the process is cleaned up.    
    /// </summary>
    public static CreateProcess<T> AddOnFinally<T>(
        this CreateProcess<T> createProcess,
        Action f)
    {
        return createProcess.AppendStatelessFuncs(
            () => { }, 
            (p) => { }, 
            (prev, exitCode) => prev, 
            f);
    }
    
    /// <summary>
    /// Execute the given function right after the process is started. 
    /// </summary>
    public static CreateProcess<T> AddOnStarted<T>(
        this CreateProcess<T> createProcess,
        Action f)
    {
        return createProcess.AppendStatelessFuncs(
            () => { }, 
            (p) => f(), 
            (prev, exitCode) => prev,
            () => { });
    }
    
    /// <summary>
    /// Execute the given function right after the process is started.
    /// PID for process can be obtained from p parameter (p.Process.Id).
    /// </summary>
    public static CreateProcess<T> AddOnStarted<T>(
        this CreateProcess<T> createProcess,
        Action<StartedProcessInfo> f)
    {
        return createProcess.AppendStatelessFuncs(
            () => { }, 
            (p) => f(new StartedProcessInfo(p)), 
            (prev, exitCode) => prev,
            () => { });
    }
    /// <summary>
    /// Sets the given environment map.      
    /// </summary>
    public static CreateProcess<T> WithEnvironment<T>(this CreateProcess<T> createProcess, EnvMap environment)
    {
        return createProcess with { Environment = environment };
    }
 
    /// <summary>
    /// Sets the given environment variables
    /// </summary>
    public static CreateProcess<T> WithEnvironment<T>(this CreateProcess<T> createProcess, IEnumerable<KeyValuePair<string,string>> environment)
    {
        return createProcess.WithEnvironment(EnvMap.OfEnumerable(environment));
    }
    
    /// <summary>
    /// Retrieve the current environment map.
    /// </summary>
    public static EnvMap GetEnvironment<T>(this CreateProcess<T> createProcess)
    {
        return createProcess.Environment ?? EnvMap.Create();
    }

    /// <summary>
    /// Sets the given environment variable
    /// </summary>
    public static CreateProcess<T> SetEnvironmentVariable<T>(this CreateProcess<T> createProcess, string key, string value)
    {
        return createProcess.WithEnvironment(createProcess.GetEnvironment().Set(key, value));
    }

    /// <summary>
    /// Set the standard output stream.
    /// </summary>
    public static CreateProcess<T> WithStandardOutput<T>(this CreateProcess<T> createProcess, StreamSpecification specification)
    {
        return createProcess with { InternalData = createProcess.InternalData.WithStreams(
            createProcess.InternalData.Specs with
            {
                StandardOutput = specification
            })
        };
    }

    /// <summary>
    /// Set the standard error stream.
    /// </summary>
    public static CreateProcess<T> WithStandardError<T>(this CreateProcess<T> createProcess, StreamSpecification specification)
    {
        return createProcess with { InternalData = createProcess.InternalData.WithStreams(
            createProcess.InternalData.Specs with
            {
                StandardError = specification
            })
        };
    }

    /// <summary>
    /// Set the standard error stream.
    /// </summary>
    public static CreateProcess<T> WithStandardInput<T>(this CreateProcess<T> createProcess, StreamSpecification specification)
    {
        return createProcess with { InternalData = createProcess.InternalData.WithStreams(
            createProcess.InternalData.Specs with
            {
                StandardInput = specification
            })
        };
    }
    
    /// <summary>
    /// Map the current result to a new type.
    /// </summary>
    public static CreateProcess<TOut> Select<TIn, TOut>(this CreateProcess<TIn> createProcess, Func<TIn, TOut> mapping)
    {
        return createProcess.AppendStatelessFuncs(
            () => {},
            (p) => {},
            async (prev, exitCode) =>
            {
                var old = await prev;
                return mapping(old);
            },
            () => {});
    }

    /// <summary>
    /// Map the current result to a new type.
    /// </summary>
    public static CreateProcess<TOut> Map<TIn, TOut>(this CreateProcess<TIn> createProcess, Func<TIn, TOut> mapping)
        => createProcess.Select(mapping);
    
    /// <summary>
    ///  Map only the result object and leave the exit code in the result type.
    /// </summary>
    public static CreateProcess<ProcessResult<TOut>> SelectResult<TIn, TOut>(this CreateProcess<ProcessResult<TIn>> createProcess, Func<TIn, TOut> mapping)
        => createProcess.Map((r) => new ProcessResult<TOut>(mapping(r.Result), r.ExitCode));
    
    /// <summary>
    ///  Map only the result object and leave the exit code in the result type.
    /// </summary>
    public static CreateProcess<ProcessResult<TOut>> MapResult<TIn, TOut>(this CreateProcess<ProcessResult<TIn>> createProcess, Func<TIn, TOut> mapping)
        => createProcess.SelectResult(mapping);
    
    
    /// <summary>
    /// Starts redirecting the output streams and collects all data at the end.
    /// </summary>
    public static CreateProcess<CombinedResult<ProcessResult<ProcessOutput>, T>> CollectAllOutput<T>(this CreateProcess<T> createProcess)
    {
        return createProcess.AppendFuncsDispose(
            () =>
            {
                // TODO: TaskCompletionSource?
                return new
                {
                    OutData = new TaskCompletionSource<string>(), ErrData = new TaskCompletionSource<string>(),
                    OutWasStarted = RefCell.Create(false),
                    ErrWasStarted = RefCell.Create(false),
                };
            },
            (state, streams) =>
            {
                var errSystemPipe = new System.IO.Pipelines.Pipe();
                var workTaskErr = async (CancellationToken ctx) =>
                {
                    state.ErrWasStarted.Data = true;
                    var errMem = new MemoryStream();

                    await errSystemPipe.Reader.CopyToAsync(errMem, ctx);

                    var err = await (new StreamReader(errMem)).ReadToEndAsync().ConfigureAwait(false);
                    state.ErrData.SetResult(err);
                };
                var outSystemPipe = new System.IO.Pipelines.Pipe();
                var workTaskOut = async (CancellationToken ctx) =>
                {
                    state.OutWasStarted.Data = true;
                    var outMem = new MemoryStream();

                    await outSystemPipe.Reader.CopyToAsync(outMem, ctx);

                    var outStr = await (new StreamReader(outMem)).ReadToEndAsync().ConfigureAwait(false);
                    state.OutData.SetResult(outStr);
                };

                var errPipe = new Pipe(errSystemPipe, workTaskErr);
                var outPipe = new Pipe(outSystemPipe, workTaskOut);

                return streams with
                {
                    StandardOutput =
                    streams.StandardOutput.InterceptStreamFallback(() => new StreamSpecification.UsePipe(outPipe),
                        outPipe),
                    StandardError =
                    streams.StandardError.InterceptStreamFallback(() => new StreamSpecification.UsePipe(errPipe),
                        errPipe)
                };
            },
            (s, p) => { },
            (prev, state, exitCode) =>
            {
                var work = Task.Run(async () =>
                {
                    var e = await exitCode.ConfigureAwait(false);
                    if (!state.ErrWasStarted.Data || !state.OutWasStarted.Data)
                    {
                        // This can happen if the stream was replaced in a later step.
                        throw new InvalidOperationException(
                            "Data was not redirected, was the redirection-pipeline overwritten?");
                    }
                    
                    var stdErr = await state.ErrData.Task.ConfigureAwait(false);
                    var stdOut = await state.OutData.Task.ConfigureAwait(false);
                    var r = new ProcessOutput(stdOut, stdErr);
                    return new ProcessResult<ProcessOutput>(r, e.RawExitCode);
                });
                return Task.FromResult(new CombinedResult<ProcessResult<ProcessOutput>, T>(work, prev));
            },
            s => {});
    }

    
}

public record ShellCommandRaw
{
    
}

public record ShellCommand<T> : ShellCommandRaw
{
    private ShellCommand()
    {
        
    }

    public record SingleProcess(CreateProcess<T> CreateProcess) : ShellCommand<T>;
    public record ProcessPipeline(ShellCommandRaw Left, CreateProcess<T> Right) : ShellCommand<T>;

    public static implicit operator ShellCommand<T>(CreateProcess<T> createProcess)
    {
        return new SingleProcess(createProcess);
    }
    
    public static implicit operator ShellCommand<T>(CreateProcess<T> createProcess)
    {
        return new SingleProcess(createProcess);
    }
}


public record ProcessShell
{
    public T Run<T>(ShellCommand<T> command)
    {
        throw new NotImplementedException();
    }
    public T Run<T>(CreateProcess<T> command) => Run((ShellCommand<T>)command);

    public Task<T> RunAsync<T>(ShellCommand<T> command)
    {
        throw new NotImplementedException();
    }
    public Task<T> RunAsync<T>(CreateProcess<T> command) => RunAsync((ShellCommand<T>)command);
}

public static class CreateProcessExtensions
{
    
}