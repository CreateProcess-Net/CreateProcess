using System.Diagnostics;
using System.IO.Pipelines;

namespace CreateProcess;

internal static class PipeUtils
{
    // This method exists in PipeWriter but is internal at the time of writing. 
    public static async Task CopyFromAsync(this PipeWriter writer, Stream source, CancellationToken cancellationToken)
    {
        while (true)
        {
            Memory<byte> buffer = writer.GetMemory();
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            writer.Advance(read);

            FlushResult result = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (result.IsCanceled)
            {
                throw new OperationCanceledException(
                    "Flush of pipeline has been cancelled");
            }

            if (result.IsCompleted)
            {
                break;
            }
        }
    }
    
    public static async Task CopyToManyAsync(this PipeReader reader, IReadOnlyList<PipeWriter> writers, CancellationToken cancellationToken)
    {
        List<bool> completed = new List<bool>(writers.Count);
        completed.AddRange(writers.Select(w => false));
        
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
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
                    var allWrite = Task.WhenAll(writers.Select(async (p,i) =>
                    {
                        if (completed[i])
                        {
                            return;
                        }
                        
                        var flushResult =
                            await p.WriteAsync(memory, cancellationToken).ConfigureAwait(false);
                        if (flushResult.IsCanceled)
                        {
                            throw new OperationCanceledException(
                                "Flush of pipeline has been cancelled");
                        }

                        if (flushResult.IsCompleted)
                        {
                            Console.Error.WriteLine("Pipe indicated that it completed, we will not write to it anymore");
                            completed[i] = true;
                            Debug.Fail("No idea when this happens");
                        }
                    }));

                    await allWrite.ConfigureAwait(false);
                    
                    consumed = position;
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

        var idx = completed.FindIndex(b => b);
        if (idx != -1)
        {
            throw new InvalidOperationException($"A writer at index {idx} completed too early!");
        }
    }
    internal static async Task<T> WrapAwaitAny<T>(Task<T> original, HashSet<Task> other)
    {
        while (true)
        {
            var workTasks = Task.WhenAny(other);
            var finishedItem = await Task.WhenAny(original, workTasks )
                .ConfigureAwait(false);
            if (finishedItem == original)
            {
                break;
            }

            var finishedWorkTask = await workTasks;
            if (finishedWorkTask.IsFaulted)
            {
                throw new InvalidOperationException("A subtask failed",
                    finishedWorkTask.Exception);
            }
                
            other.Remove(finishedWorkTask);
        }

        Debug.Assert(original.IsCompleted);
        return await original;
    }

    internal static async Task WrapAwaitAny(Task original, HashSet<Task> other)
    {
        static async Task<bool> WrapTask(Task original)
        {
            await original;
            return true;
        }

        await WrapAwaitAny(WrapTask(original), other);
    }
}

public class CreateProcessPipe
{
    internal Pipe _pipe = new Pipe();

    private int _toSet;
    private int _fromSet;
    
    private CreateProcessPipe()
    {
        
    }

    public static CreateProcessPipe Create() => new CreateProcessPipe();
    
    public ValueTask FromComplete()
    {
        return _pipe.Writer.CompleteAsync();
    }
    
    public void Reset()
    {
        _pipe.Reset();
        _toSet = 0;
        _fromSet = 0;
    }

    public async Task FromStreamAsync(Stream stream, CancellationToken tok, bool leaveOpen = false)
    {
        EnsureSingleFrom();
        try
        {
            await _pipe.Writer.CopyFromAsync(stream, tok).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseFrom(leaveOpen).ConfigureAwait(false);
        }
    }
    
    public async Task FromPipeAsync(PipeReader reader, CancellationToken tok, bool leaveOpen = false)
    {
        EnsureSingleFrom();
        try
        {
            await reader.CopyToAsync(_pipe.Writer, tok).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseFrom(leaveOpen).ConfigureAwait(false);
        }
    }
    
    public Task FromPipeAsync(CreateProcessPipe pipe, CancellationToken tok, bool leaveOpen = false)
    {
        return pipe.ToPipeAsync(this, tok, leaveOpen);
    }
    
    public async Task FromFileAsync(string fileName, CancellationToken tok, bool leaveOpen = false)
    {
        await using var file = File.OpenRead(fileName);
        await FromStreamAsync(file, tok, leaveOpen).ConfigureAwait(false);
    }

    public async Task ToStreamAsync(Stream stream, CancellationToken tok)
    {
        EnsureSingleTo();
        await _pipe.Reader.CopyToAsync(stream, tok).ConfigureAwait(false);
        await _pipe.Reader.CompleteAsync().ConfigureAwait(false);
    }


    internal void EnsureSingleFrom()
    {
        var old = Interlocked.CompareExchange(ref _fromSet, 1, 0);
        if (old != 0)
        {
            throw new InvalidOperationException("You can only use a single From-Call, please call From methods one after another and set 'leaveOpen' to true.");
        }
    }
    
    internal async Task ReleaseFrom(bool leaveOpen)
    {
        if (!leaveOpen)
        {
            await FromComplete().ConfigureAwait(false);
            return; // Do not allow next from call
        }
        
        var old = Interlocked.CompareExchange(ref _fromSet, 0, 1);
        if (old != 1)
        {
            throw new InvalidOperationException("Unexpected value in _fromSet.");
        }
    }
    
    internal void EnsureSingleTo()
    {
        var old = Interlocked.CompareExchange(ref _toSet, 1, 0);
        if (old != 0)
        {
            throw new InvalidOperationException("You can only use a single To-Call, You can create multiple pipes and use ToMany() if needed.");
        }
    }

    public Stream ToAsStream()
    {
        EnsureSingleTo();
        return _pipe.Reader.AsStream();
    }
    
    public Stream FromAsStream(bool leaveOpen = false)
    {
        EnsureSingleFrom();
        return _pipe.Writer.AsStream(leaveOpen);
    }
    
    public async Task<string> ToStringAsync()
    {
        await using var mem = ToAsStream();
        using var reader = new StreamReader(mem);
        var data =  await reader.ReadToEndAsync().ConfigureAwait(false);
        
        return data;
    }
    
    public async Task ToPipeAsync(PipeWriter writer, CancellationToken tok)
    {
        EnsureSingleTo();
        await _pipe.Reader.CopyToAsync(writer, tok).ConfigureAwait(false);
        await _pipe.Reader.CompleteAsync().ConfigureAwait(false);
    }
    
    public async Task ToMany(IReadOnlyList<PipeWriter> writer, CancellationToken tok)
    {
        EnsureSingleTo();
        await _pipe.Reader.CopyToManyAsync(writer, tok).ConfigureAwait(false);
        await _pipe.Reader.CompleteAsync().ConfigureAwait(false);
    }
    
    public async Task ToMany(IReadOnlyList<CreateProcessPipe> pipes, CancellationToken tok, bool leaveOpen = false)
    {
        foreach (var createProcessPipe in pipes)
        {
            createProcessPipe.EnsureSingleFrom();
        }
        try
        {
            var writer = pipes.Select(p => p._pipe.Writer).ToList();
            await ToMany(writer, tok).ConfigureAwait(false);
        }
        finally
        {
            foreach (var createProcessPipe in pipes)
            {
                await createProcessPipe.ReleaseFrom(leaveOpen).ConfigureAwait(false);
            }
        }
    }

    public async Task ToPipeAsync(CreateProcessPipe pipe, CancellationToken tok, bool leaveOpen = false)
    {
        EnsureSingleTo();
        pipe.EnsureSingleFrom();
        try
        {
            await _pipe.Reader.CopyToAsync(pipe._pipe.Writer, tok).ConfigureAwait(false);
            await _pipe.Reader.CompleteAsync().ConfigureAwait(false);
        }
        finally
        {
            await pipe.ReleaseFrom(leaveOpen).ConfigureAwait(false);
        }
    }

    public async Task ToFileAsync(string fileName, CancellationToken tok, bool truncate = false)
    {
        await using var file = truncate ? File.Open(fileName, FileMode.Create) : File.OpenWrite(fileName);
        await ToStreamAsync(file, tok).ConfigureAwait(false);
    }
}

internal abstract record OutputPipe
{
    private readonly string _debugName;
    private OutputPipe(string debugName)
    {
        _debugName = debugName;
    }
    internal abstract System.IO.Pipelines.PipeWriter SystemPipe { get; }
    internal abstract Func<CancellationToken, Task> WorkLoop { get; }

    internal record SimplePipe(System.IO.Pipelines.PipeWriter SystemPipe, Func<CancellationToken, Task> WorkLoop, string _debugName) : OutputPipe(_debugName)
    {
        internal override System.IO.Pipelines.PipeWriter SystemPipe { get; } = SystemPipe;
        internal override Func<CancellationToken, Task> WorkLoop { get; } = WorkLoop;
    }
    
    internal record MergedPipe(IReadOnlyList<OutputPipe> Pipes) : OutputPipe("Merged")
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
                    var whenAllWorkTasks = Task.WhenAll(workLoops);
                    var openWorkLoops = new HashSet<Task>(Pipes.Count);
                    foreach (var workLoop in workLoops)
                    {
                        openWorkLoops.Add(workLoop);
                    }

                    var pipeWriters = Pipes.Select(p => p.SystemPipe).ToList();
                    var reader = EnsurePipe.Reader;
                    
                    try
                    {
                        var writeLoop = reader.CopyToManyAsync(pipeWriters, ctx);
                        await PipeUtils.WrapAwaitAny(writeLoop, openWorkLoops).ConfigureAwait(false);
                    }
                    finally
                    {
                        foreach (var writer in pipeWriters)
                        {
                            await writer.CompleteAsync().ConfigureAwait(false);
                        }
                        
                        await reader.CompleteAsync().ConfigureAwait(false);
                    }

                    await whenAllWorkTasks.ConfigureAwait(false);
                    if (whenAllWorkTasks.IsFaulted)
                    {
                        throw new InvalidOperationException("A subtask failed",
                            whenAllWorkTasks.Exception);
                    }
                    
                    EnsurePipe.Reset();
                };
            }
        }
    }
}


internal abstract record InputPipe
{
    private readonly string _debugName;
    private InputPipe(string debugName)
    {
        _debugName = debugName;
    }
    internal abstract System.IO.Pipelines.PipeReader SystemPipe { get; }
    internal abstract Func<Task, CancellationToken, Task> WorkLoop { get; }

    internal record SimplePipe(System.IO.Pipelines.PipeReader SystemPipe, Func<Task, CancellationToken, Task> WorkLoop, string _debugName) : InputPipe(_debugName)
    {
        internal override System.IO.Pipelines.PipeReader SystemPipe { get; } = SystemPipe;
        internal override Func<Task, CancellationToken, Task> WorkLoop { get; } = WorkLoop;
    }
    
    
    internal record MergedPipe(IReadOnlyList<InputPipe> Pipes) : InputPipe("Merged")
    {
        private System.IO.Pipelines.Pipe? _pipe;
        private IReadOnlyList<InputPipe> Pipes { get; } = Flatten(Pipes);

        private static IReadOnlyList<InputPipe> Flatten(IReadOnlyList<InputPipe> pipes)
        {
            var l = new List<InputPipe>(pipes.Count);
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

        internal override System.IO.Pipelines.PipeReader SystemPipe
        {
            get { return EnsurePipe.Reader; }
        }


        internal override Func<Task, CancellationToken, Task> WorkLoop
        {
            get
            {
                return async (readCompleted, ctx) =>
                {
                    var readCompletedChildren = new List<TaskCompletionSource>(Pipes.Count);
                    readCompletedChildren.AddRange(Pipes.Select(p => new TaskCompletionSource()));
                    var workLoops = new List<Task>(Pipes.Count);
                    workLoops.AddRange(Pipes.Select((p, i) => p.WorkLoop(readCompletedChildren[i].Task, ctx)));
                    var whenAllWorkTasks = Task.WhenAll(workLoops);
                    var openWorkLoops = new HashSet<Task>(Pipes.Count);
                    foreach (var workLoop in workLoops)
                    {
                        openWorkLoops.Add(workLoop);
                    }
                    
                    var writer = EnsurePipe.Writer;

                    // Work through all pipes
                    try
                    {
                        foreach (var (pipe, i) in Pipes.Select((p, i) => (p,i)))
                        {
                            var reader = pipe.SystemPipe;
                            var completeTask = reader.CopyToAsync(writer, ctx);
                            await PipeUtils.WrapAwaitAny(completeTask, openWorkLoops).ConfigureAwait(false);
                            await reader.CompleteAsync().ConfigureAwait(false);
                            readCompletedChildren[i].SetResult(); // Notify this reader that it is complete.
                        }
                    }
                    finally
                    {
                        await writer.CompleteAsync().ConfigureAwait(false);
                    }

                    await whenAllWorkTasks.ConfigureAwait(false);
                    if (whenAllWorkTasks.IsFaulted)
                    {
                        throw new InvalidOperationException("A subtask failed",
                            whenAllWorkTasks.Exception);
                    }

                    await readCompleted.ConfigureAwait(false);
                    EnsurePipe.Reset();
                };
            }
        }
    }
}