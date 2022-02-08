using NUnit.Framework;

namespace CreateProcess.Tests;

public class ArgumentEscaping
{
    
    [Test]
    public void SimpleEscapingWorks()
    {
        var msbuild = @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe";
        var proj = @"C:\proj\CreateProcess - Copy\CreateProcess.sln";
        var args = Arguments.OfArgs(new[] { "/C", $"\"{msbuild}\" \"{proj}\"" });
        Assert.AreEqual("/C \"\\\"C:\\Program Files (x86)\\Microsoft Visual Studio\\2019\\Enterprise\\MSBuild\\Current\\Bin\\MSBuild.exe\\\" \\\"C:\\proj\\CreateProcess - Copy\\CreateProcess.sln\\\"\"", args.StartInfo);
    }
    
    //[Test]
    public void CanRunCmdManual()
    {
        var msbuild = @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe";
        var proj = @"C:\proj\CreateProcess - Copy\CreateProcess.sln";
        var cmdLine = Arguments.OfArgs(new[] { msbuild, proj });
        // https://superuser.com/questions/1213094/how-to-escape-in-cmd-exe-c-parameters
        var args = Arguments.OfArgs(new[] { "/C", cmdLine.StartInfo});
        var proc = args.ToCreateProcess("cmd").WithTraceCommand(true).RedirectToConsole();
        var shell = ProcessShell.Create();
        shell.Run(proc);
    }
    
    //[Test]
    public void CanRunCmd()
    {
        var msbuild = @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe";
        var proj = @"C:\proj\CreateProcess - Copy\CreateProcess.sln";
        var cmdLine = Arguments.OfArgs(new[] { msbuild, proj });
        // https://superuser.com/questions/1213094/how-to-escape-in-cmd-exe-c-parameters
        var args = Arguments.OfArgs(new[] { "/C"}).AppendRaw("\"" + cmdLine.StartInfo + "\"");
        var proc = args.ToCreateProcess("cmd").WithTraceCommand(true).RedirectToConsole();
        var shell = ProcessShell.Create();
        shell.Run(proc);
    }
}