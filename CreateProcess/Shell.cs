namespace CreateProcess;


public record Option
{
    private Option(){}

    internal record DisableNullCheckOption() : Option;

    public static Option DisableNullCheck { get; } = new DisableNullCheckOption();
}


public abstract record ShellCommand
{
    private ShellCommand()
    {
        
    }

    public abstract CreateProcess LastCreateProcess { get; }

    internal record SingleProcess(CreateProcess CreateProcess, Func<int, bool>? IsExitCodeOk = null) : ShellCommand
    {
        public override CreateProcess LastCreateProcess => CreateProcess;
    }

    internal record ProcessPipeline(ShellCommand Left, SingleProcess Right) : ShellCommand
    {
        public override CreateProcess LastCreateProcess => Right.LastCreateProcess;
    }

    public static implicit operator ShellCommand(CreateProcess createProcess)
    {
        return new SingleProcess(createProcess);
    }
    
    public static ShellCommand operator ==(ShellCommand left, Option option)
    {
        return left.AddOption(option);
    }

    public static ShellCommand operator !=(ShellCommand left, Option option)
    {
        throw new InvalidOperationException("Cannot use != with option, but C# forces us to overload.");
    }
    
    public static ShellCommand operator !=(ShellCommand res, int exitCode)
    {
        return res.AddLastExitCodeCheck(e => e != exitCode);
    }
    
    public static ShellCommand operator ==(ShellCommand res, int exitCode)
    {
        return res.AddLastExitCodeCheck(e => e == exitCode);
    }
    
    public static ShellCommand operator >(ShellCommand res, int exitCode)
    {
        return res.AddLastExitCodeCheck(e => e > exitCode);
    }
    
    public static ShellCommand operator <(ShellCommand res, int exitCode)
    {
        return res.AddLastExitCodeCheck(e => e < exitCode);
    }
    
    public static ShellCommand operator >=(ShellCommand res, int exitCode)
    {
        return res.AddLastExitCodeCheck(e => e >= exitCode);
    }
    
    public static ShellCommand operator <=(ShellCommand res, int exitCode)
    {
        return res.AddLastExitCodeCheck(e => e <= exitCode);
    }
    
    public static ShellCommand operator /(ShellCommand res, Option option)
    {
        return res.AddOption(option);
    }

    public static ShellCommand operator |(ShellCommand left, CreateProcess right)
    {
        return left.AppendPipeline(right);
    }

    public static ShellCommand operator |(ShellCommand left, ShellCommand right)
    {
        return left.AppendPipeline(right);
    }

}

public static class ShellCommandExtensions
{
    public static ShellCommand WithLastCreateProcess(this ShellCommand shellCommand, CreateProcess replacement)
    {
        switch (shellCommand)
        {
            case ShellCommand.ProcessPipeline processPipeline:
                return processPipeline with { Right = (ShellCommand.SingleProcess)processPipeline.Right.WithLastCreateProcess(replacement) };
            case ShellCommand.SingleProcess singleProcess:
                return singleProcess with { CreateProcess = replacement };
            default:
                throw new ArgumentOutOfRangeException(nameof(shellCommand));
        }
    }
    public static ShellCommand AddOption(this ShellCommand shellCommand, Option option)
    {
        switch (option)
        {
            case Option.DisableNullCheckOption disableNullCheckOption:
                return shellCommand.WithLastExitCodeCheck(e => true);
            default:
                throw new ArgumentOutOfRangeException(nameof(option));
        }
    }
    
    public static ShellCommand AppendPipeline(this ShellCommand left, ShellCommand right)
    {
        if (right is ShellCommand.SingleProcess singleProcess)
        {
            return new ShellCommand.ProcessPipeline(left, singleProcess);
        }

        throw new InvalidOperationException("Can only append a single process at a time!");
    }
    
    public static ShellCommand AppendPipeline(this ShellCommand left, CreateProcess right)
    {
        return new ShellCommand.ProcessPipeline(left, new ShellCommand.SingleProcess(right));
    }
    
    public static ShellCommand AddLastExitCodeCheck(this ShellCommand shellCommand, Func<int, bool> isExitCodeOk)
    {
        switch (shellCommand)
        {
            case ShellCommand.ProcessPipeline processPipeline:
                return processPipeline with
                {
                    Right = (ShellCommand.SingleProcess)processPipeline.Right.AddLastExitCodeCheck(isExitCodeOk)
                };
            case ShellCommand.SingleProcess singleProcess:
                var old = singleProcess.IsExitCodeOk;
                if (old != null)
                {
                    var newCheck = isExitCodeOk;
                    isExitCodeOk = e => old(e) && newCheck(e);
                }

                return singleProcess with { IsExitCodeOk = isExitCodeOk };
            default:
                throw new ArgumentOutOfRangeException(nameof(shellCommand));
        }
    }
    
    public static ShellCommand WithLastExitCodeCheck(this ShellCommand shellCommand, Func<int, bool> isExitCodeOk)
    {
        switch (shellCommand)
        {
            case ShellCommand.ProcessPipeline processPipeline:
                return processPipeline with
                {
                    Right = (ShellCommand.SingleProcess)processPipeline.Right.WithLastExitCodeCheck(isExitCodeOk)
                };
            case ShellCommand.SingleProcess singleProcess:
                return singleProcess with { IsExitCodeOk = isExitCodeOk };
            default:
                throw new ArgumentOutOfRangeException(nameof(shellCommand));
        }
    }
}

public record ShellProcessResult(int ExitCode, DateTimeOffset EndTime, int ProcessId, DateTimeOffset StartTime, CreateProcess CreateProcess)
{
    public TimeSpan ExecutionTime => EndTime - StartTime;
}

public record ShellResult(IReadOnlyList<ShellProcessResult> Results)
{
    
}

public class ProcessShell
{
    private IProcessStarter _starter;
    private EnvMap? _environment;
    public string? WorkingDirectory { get; set; }
    public bool DefaultTrace { get; set; }

    private ProcessShell()
    {
        
    }

    public static ProcessShell Create() => new ProcessShell();
    
    private IProcessStarter Starter
    {
        get
        {
            if (_starter == null)
            {
                _starter = new RawProc.CreateProcessStarter();
            }

            return _starter;
        }
    }
    
    public EnvMap Environment
    {
        get
        {
            if (_environment == null)
            {
                _environment = EnvMap.Create();
            }

            return _environment;
        }

        set
        {
            _environment = value;
        }
    }

    public static ProcessShell Default { get; } = new ProcessShell();
    
    public ShellResult Run(ShellCommand command)
    {
        return RunAsync(command).GetAwaiter().GetResult();
    }
    
    public ShellResult Run(CreateProcess command) => Run((ShellCommand)command);

    private CreateProcess AddDefaults(CreateProcess createProcess)
    {
        if (createProcess.Environment == null)
        {
            createProcess = createProcess.WithEnvironment(_environment);
        }

        if (createProcess.InternalData.TraceCommand == null)
        {
            createProcess = createProcess.WithTraceCommand(DefaultTrace);
        }
        
        if (createProcess.WorkingDirectory == null)
        {
            createProcess = createProcess.WithWorkingDirectory(WorkingDirectory);
        }

        return createProcess;
    }

    private async Task<ShellProcessResult> RunSinge(ShellCommand.SingleProcess singleProcess)
    {
        var raw = AddDefaults(singleProcess.CreateProcess);
        var result = Starter.Start(raw);
        var isExitCodeOk = singleProcess.IsExitCodeOk ?? (exitCode => exitCode == 0);
        var executionResult = await result.ProcessExecution.ConfigureAwait(false);
        if (!isExitCodeOk(executionResult.ExitCode))
        {
            throw new ProcessErroredException(raw, result,
                $"Process exit code indicated an error {executionResult.ExitCode}");
        }

        return
            new ShellProcessResult(executionResult.ExitCode, executionResult.EndTime, result.ProcessId,
                result.StartTime, raw);
    }

    private async Task RunAndCollect(IList<ShellProcessResult> results, ShellCommand command)
    {
        switch (command)
        {
            case ShellCommand.ProcessPipeline processPipeline:
                var lastLeft = processPipeline.Left.LastCreateProcess;
                var right = processPipeline.Right.CreateProcess;

                lastLeft = lastLeft.AddRedirect(ref right);

                await RunAndCollect(results, processPipeline.Left.WithLastCreateProcess(lastLeft)).ConfigureAwait(false);
                await RunAndCollect(results, processPipeline.Right.WithLastCreateProcess(right)).ConfigureAwait(false);
                
                break;
            case ShellCommand.SingleProcess singleProcess:
                var result = await RunSinge(singleProcess).ConfigureAwait(false);
                results.Add(result);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }
    
    public async Task<ShellResult> RunAsync(ShellCommand command)
    {
        var l = new List<ShellProcessResult>();
        await RunAndCollect(l, command);
        return new ShellResult(l);
    }
    public Task<ShellResult> RunAsync(CreateProcess command) => RunAsync((ShellCommand)command);

    public void AppendPath(string dir)
    {
        var env = Environment;
        var old = env.Get("PATH");
        Environment = env.Set("PATH", old + Path.PathSeparator + dir);
    }
}
