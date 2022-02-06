using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;

namespace CreateProcess;

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

    public string Get(string key)
    {
        if (Dictionary.TryGetValue(key, out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"Key {key} was not found in EnvMap!");
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


/// <summary>
/// Various options to redirect streams.
/// </summary>
internal record OutputStreamSpecification
{
    public bool IsInherit { get; }
    private OutputStreamSpecification(bool isInherit)
    {
        this.IsInherit = isInherit;
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
        this.IsInherit = isInherit;
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

public record RawProcessResult(int ExitCode, DateTimeOffset EndTime);

public record RawProcessStartResult(Task<RawProcessResult> ProcessExecution, int ProcessId, DateTimeOffset StartTime)
{
    internal System.Diagnostics.Process? Process { get; set; }
}

internal interface IProcessStarter
{
    RawProcessStartResult Start(CreateProcess createProcess);
}

internal static class RawProc
{
    internal class CreateProcessStarter : IProcessStarter
    {
        private readonly bool _defaultTrace;

        public CreateProcessStarter(bool defaultTrace = false)
        {
            _defaultTrace = defaultTrace;
        }

        public RawProcessStartResult Start(CreateProcess createProcess)
        {
            var p = createProcess.StartInfo;
            var toolProcess = new System.Diagnostics.Process() { StartInfo = p };
            var isStarted = false;
            var streamSpec = createProcess.InternalData.Specs;
            Task readOutputTask = System.Threading.Tasks.Task.FromResult(Stream.Null);
            Task readErrorTask   = System.Threading.Tasks.Task.FromResult(Stream.Null);
            Task redirectStdInTask  = System.Threading.Tasks.Task.FromResult(Stream.Null);
            var tok = new System.Threading.CancellationTokenSource();
            DateTimeOffset start = DateTimeOffset.Now;

            // Subscribe to event
            var exitEvent =
                Observable.FromEventPattern(
                    handler => toolProcess.Exited += handler,
                    handler => toolProcess.Exited -= handler).FirstAsync().ToTask();

            var processId = -1;
            if (!isStarted)
            {
                toolProcess.EnableRaisingEvents = true;
                
                var si = toolProcess.StartInfo;
                //if Environment.isMono || AlwaysSetProcessEncoding then
                //    si.StandardOutputEncoding <- ProcessEncoding
                //    si.StandardErrorEncoding <- ProcessEncoding

                if (createProcess.InternalData.TraceCommand ?? _defaultTrace)
                {
                    var commandLine = $"{si.WorkingDirectory}> \"{si.FileName}\" {si.Arguments}";
                    Console.WriteLine(
                        $"{commandLine} (In: {si.RedirectStandardInput}, Out: {si.RedirectStandardOutput}, Err: {si.RedirectStandardError})");
                }
                
                try
                {
                    var ok = toolProcess.Start();
                    if (!ok)
                    {
                        throw new InvalidOperationException("Could not start process (Start() returned false).");
                    }
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException($"Start of process '{si.FileName}' failed.", e);
                }
                
                start = DateTimeOffset.Now;
                try
                {
                    start = new DateTimeOffset(toolProcess.StartTime);
                }
                catch (Win32Exception)
                {
                    // Can happen when GetProcessTimes returns false
                }
                catch (InvalidOperationException)
                {
                    // Can happen when the process has already exited (GetProcessHandle returned an invalid handle)
                }

                try
                {
                    processId = toolProcess.Id;
                }
                catch (InvalidOperationException)
                {
                    throw;
                }

                async Task HandleUseOutputPipe(OutputStreamSpecification.UsePipe usePipe, Stream processStream)
                {
                    var workTask = usePipe.Pipe.WorkLoop(tok.Token);
                    var writer = usePipe.Pipe.SystemPipe; // Why is there no CopyFromAsync Helper?
                    int read;
                    do
                    {
                        Memory<byte> memory = writer.GetMemory(81920);
                        read = await processStream.ReadAsync(memory, tok.Token).ConfigureAwait(false);
                        if (read > 0)
                        {
                            writer.Advance(read);
                            await writer.FlushAsync(tok.Token).ConfigureAwait(false);
                        }
                    } while (read > 0);
                    
                    await writer.CompleteAsync().ConfigureAwait(false);
                    
                    await workTask;
                }
                
                async Task HandleUseInputPipe(InputStreamSpecification.UsePipe usePipe, Stream processStream)
                {
                    var completedRead = new TaskCompletionSource();
                    var workTask = usePipe.Pipe.WorkLoop(completedRead.Task, tok.Token);
                    await usePipe.Pipe.SystemPipe.CopyToAsync(processStream, tok.Token).ConfigureAwait(false);
                    try
                    {
                        processStream.Close();
                    }
                    finally
                    {
                        await usePipe.Pipe.SystemPipe.CompleteAsync().ConfigureAwait(false);
                        completedRead.SetResult(); // Notify worktasks to reset the pipe
                    }
                    
                    await workTask.ConfigureAwait(false);
                }
                
                async Task HandleInputStream(InputStreamSpecification parameter, Stream processStream)
                {
                    switch (parameter)
                    {
                        case InputStreamSpecification.Inherit:
                            throw new InvalidOperationException("Unexpected value");
                        case InputStreamSpecification.UsePipe usePipe:
                            await HandleUseInputPipe(usePipe, processStream).ConfigureAwait(false);
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
                            await HandleUseOutputPipe(usePipe, processStream).ConfigureAwait(false);
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
            
            var allRedirectionTasks = Task.WhenAll(readErrorTask, readOutputTask, redirectStdInTask);

            static async Task<RawProcessResult> ProcessExecution(Task exitEvent, Process toolProcess, Task allRedirectionTasks, Task readErrorTask,Task  readOutputTask,Task  redirectStdInTask, CancellationTokenSource tok)
            {
                await exitEvent;
                var end = DateTimeOffset.Now;
                try
                {
                    end = new DateTimeOffset(toolProcess.ExitTime);
                }
                catch (Win32Exception)
                {
                    // Can happen when GetProcessTimes returns false
                }
                catch (InvalidOperationException)
                {
                    // Can happen when the process has already exited (GetProcessHandle returned an invalid handle)
                }
                
                
                // Waiting for the process to exit (buffers)
                //await toolProcess.WaitForExitAsync().ConfigureAwait(false);
                toolProcess.WaitForExit();
                var code = toolProcess.ExitCode;
                toolProcess.Dispose();


                async Task<bool> TryWait()
                {
                    var delay = Task.Delay(500);
                    var t = await Task.WhenAny(allRedirectionTasks, delay).ConfigureAwait(false);
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
                        await Console.Error.WriteLineAsync($"At least on redirection task did not finish: \nReadErrorTask: {readErrorTask.Status}\nReadOutputTask: {readOutputTask.Status}\nRedirectStdInTask: {redirectStdInTask.Status}");
                    }

                    allFinished = ok;
                }

                var result = new RawProcessResult(code, end);
                
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
                        await Console.Error.WriteLineAsync(msg);
                    }
                }

                return result;
            }

            var processExecution = ProcessExecution(exitEvent, toolProcess, allRedirectionTasks, readErrorTask,
                readOutputTask, redirectStdInTask, tok);
            
            static async Task<RawProcessResult> ProcessExecutionOrProcessingFailed(Task<RawProcessResult> processExecution, Task allRedirectionTasks, int id, DateTimeOffset start, Process toolProcess, CreateProcess createProcess)
            {
                var finished = await Task.WhenAny(allRedirectionTasks, processExecution);
                if (finished == allRedirectionTasks && allRedirectionTasks.IsFaulted)
                {
                    var result = new RawProcessStartResult(processExecution, id, start) { Process = toolProcess };
                    throw new StreamProcessingException(createProcess, result, allRedirectionTasks.Exception!);
                }

                var r = await processExecution.ConfigureAwait(false);

                if (allRedirectionTasks.IsFaulted)
                {
                    var result = new RawProcessStartResult(processExecution, id, start) { Process = toolProcess };
                    throw new StreamProcessingException(createProcess, result, allRedirectionTasks.Exception!);
                }
                
                return r;
            };

            return new RawProcessStartResult(ProcessExecutionOrProcessingFailed(processExecution, allRedirectionTasks, processId, start, toolProcess, createProcess), processId, start) { Process = toolProcess };
        }
    }
}