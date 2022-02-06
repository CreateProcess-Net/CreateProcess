using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reflection.Metadata;

namespace CreateProcess;


/// <summary>
/// The output of the process. If ordering between stdout and stderr is important you need to use streams.
/// </summary>
public record ProcessOutput(string Output, string Error);

public record ProcessResult<T>(T Result, int ExitCode);

public class InternalCreateProcessData
{
    internal StreamSpecs Specs { get; }
    internal bool? TraceCommand { get; }

    internal InternalCreateProcessData(bool? traceCommand, StreamSpecs specs)
    {
        Specs = specs;
        TraceCommand = traceCommand;
    }

    internal InternalCreateProcessData WithStreams(StreamSpecs newSpec)
    {
        return new InternalCreateProcessData(TraceCommand, newSpec);
    }
    public InternalCreateProcessData WithTraceCommand(bool? newVal)
    {
        return new InternalCreateProcessData(newVal, Specs);
    }
}


public enum RedirectStream
{
    StandardInput = 1,
    StandardOutput = 2,
    StandardError = 3,
}

public record OutputRedirectSpecification
{
    internal RedirectStream Stream { get; }
    
    private OutputRedirectSpecification(RedirectStream stream)
    {
        if (stream != RedirectStream.StandardOutput && stream != RedirectStream.StandardError)
        {
            throw new ArgumentException(
                "RedirectStream can only be StandardInput or StandardOutput for output streams.");
        }
        
        Stream = stream;
    }
    
    internal record ToFile(RedirectStream Stream, string FileName, bool Truncate) : OutputRedirectSpecification(Stream);
    internal record ToProcessStream(RedirectStream Source, RedirectStream Target) : OutputRedirectSpecification(Source);
    internal record ToConsole(RedirectStream Source, RedirectStream Target) : OutputRedirectSpecification(Source);
}

public record InputRedirectSpecification
{
    private InputRedirectSpecification(){}
    
    internal record FromFile(string FileName) : InputRedirectSpecification;
    internal record FromPipe(PipeReader reader) : InputRedirectSpecification;
}


public record Redirect
{
    internal RedirectStream Stream { get; }
    
    private Redirect(RedirectStream stream)
    {
        this.Stream = stream;
    }
    
    public OutputRedirectSpecification ToFile(string file, bool truncate = false)
    {
        return new OutputRedirectSpecification.ToFile(Stream, file, truncate);
    }

    internal OutputRedirectSpecification ToStandardOutput
    {
        get
        {
            if (Stream == RedirectStream.StandardOutput)
                throw new InvalidOperationException("You cannot redirect the Standard output to itself.");
            return new OutputRedirectSpecification.ToProcessStream(Stream, RedirectStream.StandardOutput);
        }
    }
    
    internal OutputRedirectSpecification ToStandardError
    {
        get
        {
            if (Stream == RedirectStream.StandardError)
                throw new InvalidOperationException("You cannot redirect the Standard error to itself.");
            return new OutputRedirectSpecification.ToProcessStream(Stream, RedirectStream.StandardError);
        }
    }

    public OutputRedirectSpecification ToConsole
    {
        get
        {
            return new OutputRedirectSpecification.ToConsole(Stream, Stream);
        }
    }

    public OutputRedirectSpecification ToOutputConsole
    {
        get
        {
            return new OutputRedirectSpecification.ToConsole(Stream, RedirectStream.StandardOutput);
        }
    }
    
    public OutputRedirectSpecification ToErrorConsole
    {
        get
        {
            return new OutputRedirectSpecification.ToConsole(Stream, RedirectStream.StandardError);
        }
    }

    public static Redirect Error { get; } = new Redirect(RedirectStream.StandardError);
    public static Redirect Output { get; } = new Redirect(RedirectStream.StandardOutput);

    public static InputRedirectSpecification FromFile(string filePath)
    {
        return new InputRedirectSpecification.FromFile(filePath);
    }
}

public record CreateProcess(Command Command, string? WorkingDirectory, EnvMap? Environment, InternalCreateProcessData InternalData)
{
    public string CommandLine => Command.CommandLine;

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

            InternalData.Specs.SetStartInfo(p);
            
            return p;
        }
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

    public static CreateProcess operator >(CreateProcess res, OutputRedirectSpecification spec)
    {
        return res.AddRedirect(spec);
    }
    public static CreateProcess operator <(CreateProcess res, OutputRedirectSpecification spec)
    {
        throw new InvalidOperationException(
            "Cannot redirect an output specification to an process. C# forces us to overload this operator.");
    }
    
    public static CreateProcess operator <(CreateProcess res, InputRedirectSpecification spec)
    {
        return res.AddRedirect(spec);
        throw new NotImplementedException("Not yet implemented");
        return res;
    }
    
    public static CreateProcess operator >(CreateProcess res, InputRedirectSpecification spec)
    {
        throw new InvalidOperationException(
            "Cannot redirect an output specification to an process. C# forces us to overload this operator.");
    }
    
    public static ShellCommand operator |(CreateProcess left, CreateProcess right)
    {
        return (ShellCommand)left | right;
    }
    
    public static ShellCommand operator ==(CreateProcess left, Option option)
    {
        return (ShellCommand)left == option;
    }
    
    public static ShellCommand operator !=(CreateProcess left, Option option)
    {
        return (ShellCommand)left != option;
    }
    
    public static ShellCommand operator ==(CreateProcess left, int exitCode)
    {
        return (ShellCommand)left == exitCode;
    }
    
    public static ShellCommand operator !=(CreateProcess left, int exitCode)
    {
        return (ShellCommand)left != exitCode;
    }
    public static ShellCommand operator <(CreateProcess left, int exitCode)
    {
        return (ShellCommand)left < exitCode;
    }
    public static ShellCommand operator >(CreateProcess left, int exitCode)
    {
        return (ShellCommand)left > exitCode;
    }
    public static ShellCommand operator <=(CreateProcess left, int exitCode)
    {
        return (ShellCommand)left <= exitCode;
    }
    public static ShellCommand operator >=(CreateProcess left, int exitCode)
    {
        return (ShellCommand)left >= exitCode;
    }
}

public static class CreateProcessExtensions
{
    public static CreateProcess WithTraceCommand(this CreateProcess createProcess, bool? newVal)
    {
        return createProcess with { InternalData = createProcess.InternalData.WithTraceCommand(newVal) };
    }
    
    public static CreateProcess WithWorkingDirectory(this CreateProcess createProcess, string? workingDirectory)
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
    public static CreateProcess WithEnvironment(this CreateProcess createProcess, EnvMap? environment)
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

    internal static InputStreamSpecification InterceptStreamFallback(this InputStreamSpecification spec,
        Func<InputStreamSpecification> onInherit, InputPipe target)
    {
        switch (spec)
        {
            case InputStreamSpecification.Inherit:
                return onInherit();
            case InputStreamSpecification.UsePipe usePipe:
                return new InputStreamSpecification.UsePipe(new InputPipe.MergedPipe(new []{usePipe.Pipe, target}));
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

    internal static CreateProcess RedirectOutputTo(this CreateProcess createProcess, PipeWriter writer, Func<CancellationToken, Task> workerTask, string debugName)
    {
        var pipe = new OutputPipe.SimplePipe(writer, workerTask, debugName);
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

    internal static CreateProcess RedirectErrorTo(this CreateProcess createProcess, PipeWriter writer, Func<CancellationToken, Task> workerTask, string debugName)
    {
        var pipe = new OutputPipe.SimplePipe(writer, workerTask, debugName);
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

    internal static CreateProcess RedirectInputFrom(this CreateProcess createProcess, PipeReader reader, Func<Task, CancellationToken, Task> workerTask, string debugName)
    {
        var pipe = new InputPipe.SimplePipe(reader, workerTask, debugName);
        return createProcess with
        {
            InternalData = createProcess.InternalData.WithStreams(
                createProcess.InternalData.Specs with
                {
                    StandardInput = createProcess.InternalData.Specs.StandardInput.InterceptStreamFallback(
                        () => new InputStreamSpecification.UsePipe(pipe), pipe)
                })
        };
    }

    public static CreateProcess AddRedirect(this CreateProcess createProcess, OutputRedirectSpecification spec)
    {
        System.IO.Pipelines.Pipe pipe;
        Func<CancellationToken, Task> workTask;
        var debugName = "ToUnknown";
        switch (spec)
        {
            case OutputRedirectSpecification.ToConsole toConsole:
                debugName = "ToConsole";
                switch (toConsole.Target)
                {
                    case RedirectStream.StandardOutput:
                        pipe = new System.IO.Pipelines.Pipe();
                        workTask = async (CancellationToken tok) =>
                        {
                            var stdOut = System.Console.OpenStandardOutput();
                            await pipe.Reader.CopyToAsync(stdOut, tok).ConfigureAwait(false);
                            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
                            pipe.Reset();
                        };
                        break;
                    case RedirectStream.StandardError:
                        pipe = new System.IO.Pipelines.Pipe();
                        workTask = async (CancellationToken tok) =>
                        {
                            var stdOut = System.Console.OpenStandardOutput();
                            await pipe.Reader.CopyToAsync(stdOut, tok).ConfigureAwait(false);
                            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
                            pipe.Reset();
                        };
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
                break;
            case OutputRedirectSpecification.ToFile toFile:
                debugName = "ToFile";
                pipe = new System.IO.Pipelines.Pipe();
                workTask = async (CancellationToken tok) =>
                {
                    await using var file = toFile.Truncate ? File.Open(toFile.FileName, FileMode.Create) : File.OpenWrite(toFile.FileName);
                    await pipe.Reader.CopyToAsync(file, tok).ConfigureAwait(false);
                    await pipe.Reader.CompleteAsync().ConfigureAwait(false);
                    
                    pipe.Reset();
                };
                break;
            case OutputRedirectSpecification.ToProcessStream toProcessStream:
                debugName = "ToProcessStream";
                throw new NotImplementedException("Need to find a good design for this");
                pipe = new System.IO.Pipelines.Pipe();
                switch (toProcessStream.Target)
                {
                    case RedirectStream.StandardOutput:
                        Func<CancellationToken, Task> writeTask = async (tok) =>
                        {
                        };
                        createProcess = createProcess.RedirectOutputTo(pipe.Writer, writeTask, "");
                        break;
                    case RedirectStream.StandardError:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
                
                throw new NotImplementedException("Need to find a good design for this");
                workTask = async (CancellationToken tok) =>
                {
                    var stdOut = System.Console.OpenStandardOutput();
                    await pipe.Reader.CopyToAsync(stdOut, tok).ConfigureAwait(false);
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(spec));
        }
        
        switch (spec.Stream)
        {
            case RedirectStream.StandardOutput:
                return createProcess.RedirectOutputTo(pipe.Writer, workTask, "Output" + debugName);
            case RedirectStream.StandardError:
                return createProcess.RedirectErrorTo(pipe.Writer, workTask, "Error" + debugName);
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
    
    public static CreateProcess AddRedirect(this CreateProcess createProcess, InputRedirectSpecification spec)
    {
        System.IO.Pipelines.Pipe pipe;
        Func<Task, CancellationToken, Task> workTask;
        var debugName = "FromUnknown";
        switch (spec)
        {
            case InputRedirectSpecification.FromFile fromFile:
                debugName = "FromFile";
                pipe = new System.IO.Pipelines.Pipe();
                workTask = async (readComplete, tok) =>
                {
                    await using var file = File.OpenRead(fromFile.FileName);
                    await pipe.Writer.CopyFromAsync(file, tok).ConfigureAwait(false);
                    await pipe.Writer.CompleteAsync().ConfigureAwait(false);
                    
                    await readComplete.ConfigureAwait(false);
                    pipe.Reset();
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(spec));
        }
        
        return createProcess.RedirectInputFrom(pipe.Reader, workTask, "Input" + debugName);
    }
    
    // TODO: Rework design
    internal static CreateProcess AddRedirect(this CreateProcess createProcess, ref CreateProcess target)
    {
        System.IO.Pipelines.Pipe pipe = new System.IO.Pipelines.Pipe();
        Func<CancellationToken, Task> outputWorkTask = async (tok) =>
        {

        };
        Func<Task, CancellationToken, Task> inputWorkTask = async (readComplete, tok) =>
        {
            await readComplete;
            pipe.Reset();
        };
        
        target = target.RedirectInputFrom(pipe.Reader, inputWorkTask, "Input" + "FromProcess");
        return createProcess.RedirectOutputTo(pipe.Writer, outputWorkTask, "OutputToProcess");
    }
    
    public static CreateProcess RedirectToConsole(this CreateProcess createProcess)
    {
        return createProcess
            .AddRedirect(Redirect.Error.ToConsole)
            .AddRedirect(Redirect.Output.ToConsole);
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
