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