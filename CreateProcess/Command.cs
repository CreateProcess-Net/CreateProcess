namespace CreateProcess;


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
    
    /// <summary>
    /// Create a simple <see cref="CreateProcess"/> instance from the given command.
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
                null, 
                new StreamSpecs(new InputStreamSpecification.Inherit(), new OutputStreamSpecification.Inherit(),
                    new OutputStreamSpecification.Inherit())));
    }
}
