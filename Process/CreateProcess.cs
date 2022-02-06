using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reflection.Metadata;

namespace Process;


/// <summary>
/// The output of the process. If ordering between stdout and stderr is important you need to use streams.
/// </summary>
public record ProcessOutput(string Output, string Error);

public record ProcessResult<T>(T Result, int ExitCode);

public class InternalCreateProcessData
{
    internal StreamSpecs Specs { get; }
    internal bool TraceCommand { get; }

    internal InternalCreateProcessData(bool traceCommand, StreamSpecs specs)
    {
        Specs = specs;
        TraceCommand = traceCommand;
    }

    internal InternalCreateProcessData WithStreams(StreamSpecs newSpec)
    {
        return new InternalCreateProcessData(TraceCommand, newSpec);
    }
    public InternalCreateProcessData WithDisableTrace()
    {
        return new InternalCreateProcessData(false, Specs);
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

public record CreateProcess(Command Command, string? WorkingDirectory, EnvMap? Environment, InternalCreateProcessData InternalData)
{
    public string CommandLine => Command.CommandLine;

    
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
    public static CreateProcess FromRawCommandLine(string executable, string arguments)
    {
        return Arguments.OfWindowsCommandLine(arguments)
            .ToRawCommand(executable)
            .ToCreateProcess();
    }
    
    public static CreateProcess FromCommandLine(string executable, IEnumerable<string> arguments)
    {
        return Arguments.OfArgs(arguments)
            .ToRawCommand(executable)
            .ToCreateProcess();
    }
    
    public static CreateProcess FromCommandLine(string executable, params string[] arguments)
    {
        return FromCommandLine(executable, (IEnumerable<string>)arguments);
    }

    
    public static CreateProcess operator >(CreateProcess res, RedirectSpecification spec)
    {
        return res;
    }
    
    public static CreateProcess operator <(CreateProcess res, RedirectSpecification spec)
    {
        return res;
    }
    
    public static ShellCommand operator |(CreateProcess left, CreateProcess right)
    {
        return new ShellCommand.ProcessPipeline(left, right);
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

public static class CreateProcessEx
{
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
    public static CreateProcess ToCreateProcess(this Command command)
    {
        return new CreateProcess(
            command, null, null, new InternalCreateProcessData(
                true, 
                new StreamSpecs(new InputStreamSpecification.Inherit(), new OutputStreamSpecification.Inherit(),
                    new OutputStreamSpecification.Inherit())));
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

    /* API unclear
    public static CreateProcess ToCreateProcess(
        this System.Diagnostics.ProcessStartInfo startInfo)
    {
        return new CreateProcess(
            startInfo.UseShellExecute
                ? new Command.Shell(startInfo.FileName)
                : new Command.Raw(startInfo.FileName, startInfo.Arguments.ToArguments()),
            string.IsNullOrWhiteSpace(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
            EnvMap.OfEnumerable(startInfo.Environment),
            new InternalCreateProcessData(
                true, 
                new StreamSpecs(
                    startInfo.RedirectStandardInput
                        ? new InputStreamSpecification.UsePipe(new InputPipe.SimplePipe(new System.IO.Pipelines.Pipe(), _ => Task.CompletedTask))
                        : new InputStreamSpecification.Inherit(),
                    startInfo.RedirectStandardOutput
                        ? new OutputStreamSpecification.UsePipe(new OutputPipe.SimplePipe(new System.IO.Pipelines.Pipe(), _ => Task.CompletedTask))
                        : new OutputStreamSpecification.Inherit(),
                    startInfo.RedirectStandardError
                        ? new OutputStreamSpecification.UsePipe(new OutputPipe.SimplePipe(new System.IO.Pipelines.Pipe(), _ => Task.CompletedTask))
                        : new OutputStreamSpecification.Inherit()
                ))
           
        );
    }*/

    internal static OutputStreamSpecification InterceptStreamFallback(this OutputStreamSpecification spec,
        Func<OutputStreamSpecification> onInherit, OutputPipe target)
    {
        switch (spec)
        {
            case OutputStreamSpecification.Inherit:
                return onInherit();
            case OutputStreamSpecification.UsePipe usePipe:
                return new OutputStreamSpecification.UsePipe(new OutputPipe.MergedPipe(new []{usePipe.Pipe, target}));
            default:
                throw new InvalidOperationException($"Unknown type {spec.GetType().FullName}");
        }
    }

    internal static OutputStreamSpecification InterceptStream(this OutputStreamSpecification spec, OutputPipe target)
    {
        return spec.InterceptStreamFallback(
            () => throw new InvalidOperationException(
                "cannot intercept stream when it is not redirected. Please redirect the stream first!"),
            target);
    }

    internal static CreateProcess RedirectOutputTo(this CreateProcess createProcess, PipeWriter writer, Func<CancellationToken, Task> workerTask)
    {
        var pipe = new OutputPipe.SimplePipe(writer, workerTask);
        return createProcess with
        {
            InternalData = createProcess.InternalData.WithStreams(
                createProcess.InternalData.Specs with
                {
                    StandardOutput = createProcess.InternalData.Specs.StandardOutput.InterceptStreamFallback(
                        () => new OutputStreamSpecification.UsePipe(pipe), pipe)
                })
        };
    }

    internal static CreateProcess RedirectErrorTo(this CreateProcess createProcess, PipeWriter writer, Func<CancellationToken, Task> workerTask)
    {
        var pipe = new OutputPipe.SimplePipe(writer, workerTask);
        return createProcess with
        {
            InternalData = createProcess.InternalData.WithStreams(
                createProcess.InternalData.Specs with
                {
                    StandardError = createProcess.InternalData.Specs.StandardError.InterceptStreamFallback(
                        () => new OutputStreamSpecification.UsePipe(pipe), pipe)
                })
        };
    }
    
    public static CreateProcess CopyRedirectedProcessOutputsToStandardOutputs(this CreateProcess createProcess)
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
        return createProcess
            .RedirectErrorTo(errorPipe.Writer, errorWorkTask)
            .RedirectOutputTo(outputPipe.Writer, outputWorkTask);
    }

    public static CreateProcess DisableTraceCommand(this CreateProcess createProcess)
    {
        return createProcess with { InternalData = createProcess.InternalData.WithDisableTrace() };
    }
    
    public static CreateProcess WithWorkingDirectory<T>(this CreateProcess createProcess, string? workingDirectory)
    {
        return createProcess with { WorkingDirectory = workingDirectory };
    }
    
    public static CreateProcess WithCommand(this CreateProcess createProcess, Command command)
    {
        return createProcess with { Command = command };
    }
    
    public static CreateProcess ReplaceExecutable<T>(this CreateProcess createProcess, string newExecutable)
    {
        return createProcess with { Command = createProcess.Command.ReplaceExecutable(newExecutable) };
    }

    public static CreateProcess MapExecutable<T>(this CreateProcess createProcess, Func<string, string> executableMapping)
    {
        return createProcess with { Command = createProcess.Command.ReplaceExecutable(executableMapping(createProcess.Command.Executable)) };
    }
    
    
    /// <summary>
    /// Sets the given environment map.      
    /// </summary>
    public static CreateProcess WithEnvironment(this CreateProcess createProcess, EnvMap environment)
    {
        return createProcess with { Environment = environment };
    }
 
    /// <summary>
    /// Sets the given environment variables
    /// </summary>
    public static CreateProcess WithEnvironment(this CreateProcess createProcess, IEnumerable<KeyValuePair<string,string>> environment)
    {
        return createProcess.WithEnvironment(EnvMap.OfEnumerable(environment));
    }
    
    /// <summary>
    /// Retrieve the current environment map.
    /// </summary>
    public static EnvMap GetEnvironment(this CreateProcess createProcess)
    {
        return createProcess.Environment ?? EnvMap.Create();
    }

    /// <summary>
    /// Sets the given environment variable
    /// </summary>
    public static CreateProcess SetEnvironmentVariable(this CreateProcess createProcess, string key, string value)
    {
        return createProcess.WithEnvironment(createProcess.GetEnvironment().Set(key, value));
    }

    /// <summary>
    /// Set the standard output stream.
    /// </summary>
    internal static CreateProcess WithStandardOutput(this CreateProcess createProcess, OutputStreamSpecification specification)
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
    internal static CreateProcess WithStandardError(this CreateProcess createProcess, OutputStreamSpecification specification)
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
    internal static CreateProcess WithStandardInput(this CreateProcess createProcess, InputStreamSpecification specification)
    {
        return createProcess with { InternalData = createProcess.InternalData.WithStreams(
            createProcess.InternalData.Specs with
            {
                StandardInput = specification
            })
        };
    }
    
    /* Should be done at a higher level (working with the result of a process start instead of trying to add the info to the generic parameter.
    /// <summary>
    /// Starts redirecting the output streams and collects all data at the end.
    /// </summary>
    public static CreateProcess<CombinedResult<ProcessResult<ProcessOutput>, T>> CollectAllOutput<T>(this CreateProcess createProcess)
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

                var errPipe = new OutputPipe.SimplePipe(errSystemPipe.Writer, workTaskErr);
                var outPipe = new OutputPipe.SimplePipe(outSystemPipe.Writer, workTaskOut);

                return streams with
                {
                    StandardOutput =
                    streams.StandardOutput.InterceptStreamFallback(() => new OutputStreamSpecification.UsePipe(outPipe),
                        outPipe),
                    StandardError =
                    streams.StandardError.InterceptStreamFallback(() => new OutputStreamSpecification.UsePipe(errPipe),
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
*/
    
}


public record ShellCommand
{
    private ShellCommand()
    {
        
    }

    public record SingleProcess(CreateProcess CreateProcess) : ShellCommand;
    public record ProcessPipeline(ShellCommand Left, CreateProcess Right) : ShellCommand;

    public static implicit operator ShellCommand(CreateProcess createProcess)
    {
        return new SingleProcess(createProcess);
    }
    
}


public record ProcessShell
{
    public void Run(ShellCommand command)
    {
        throw new NotImplementedException();
    }
    public void Run(CreateProcess command) => Run((ShellCommand)command);

    public Task RunAsync(ShellCommand command)
    {
        throw new NotImplementedException();
    }
    public Task RunAsync(CreateProcess command) => RunAsync((ShellCommand)command);
}

public static class CreateProcessExtensions
{
    
}