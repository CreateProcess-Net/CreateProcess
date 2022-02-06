namespace CreateProcess;

/// <summary>
/// Parent class for exceptions thrown by <see cref="CreateProcess"/>.
/// </summary>
public abstract class CreateProcessException : Exception
{
    /// <summary>
    /// Initializes an instance of <see cref="CreateProcessException"/>.
    /// </summary>
    protected CreateProcessException(string message) : base(message)
    {
    }
    /// <summary>
    /// Initializes an instance of <see cref="CreateProcessException"/>.
    /// </summary>
    protected CreateProcessException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Exception thrown when the command fails to execute correctly.
/// </summary>
public class ProcessErroredException : CreateProcessException
{
    /// <summary>
    /// The result of the process
    /// </summary>
    public RawProcessStartResult ProcessResult { get; }

    /// <summary>
    /// The started process
    /// </summary>
    public CreateProcess CreateProcess { get; }
    
    /// <summary>
    /// Exit code returned by the process.
    /// </summary>
    public int ExitCode => ProcessResult.ProcessExecution.Result.ExitCode;

    /// <summary>
    /// Initializes an instance of <see cref="ProcessErroredException"/>.
    /// </summary>
    public ProcessErroredException(CreateProcess process, RawProcessStartResult result, string message)
        : base(message)
    {
        CreateProcess = process;
        ProcessResult = result;
    }
}

/// <summary>
/// Exception thrown when some worker task has thrown an exception while processing.
/// </summary>
public class StreamProcessingException : CreateProcessException
{
    /// <summary>
    /// The result of the process
    /// </summary>
    public RawProcessStartResult ProcessResult { get; }

    /// <summary>
    /// The started process
    /// </summary>
    public CreateProcess CreateProcess { get; }
    
    /// <summary>
    /// Initializes an instance of <see cref="ProcessErroredException"/>.
    /// </summary>
    public StreamProcessingException(CreateProcess process, RawProcessStartResult result, Exception innerException)
        : base("Some stream processing task failed, this might happen due to faulty user code or an CreateProcess bug.", innerException)
    {
        CreateProcess = process;
        ProcessResult = result;
    }
}