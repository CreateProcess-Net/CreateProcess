using System.IO;
using System.Net.NetworkInformation;
using System.Reactive;
using System.Threading.Tasks;
using NUnit.Framework;
using CreateProcess;

namespace CreateProcess.Tests;

public class Tests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public void RedirectToFile()
    {
        var shell = new ProcessShell();
        shell.Run(
            CreateProcess.FromCommandLine(@"C:\Program Files\Git\usr\bin\bash.exe", "-c", "echo test single argument")
            > Redirect.Output.ToFile("test.txt", true)
            > Redirect.Output.ToFile("second.txt", true)
            );
        Assert.AreEqual("test single argument\n", File.ReadAllText("test.txt"));
        Assert.AreEqual("test single argument\n", File.ReadAllText("second.txt"));
    }
    
    [Test]
    public void RedirectFromFileAndPipeToCat()
    {
        var shell = new ProcessShell();
        File.WriteAllTextAsync("test.txt", "RedirectFromFileAndPipeToCat");
        shell.AppendPath(@"C:\Program Files\Git\usr\bin");
        shell.Run(
            CreateProcess.FromCommandLine(@"C:\Program Files\Git\usr\bin\bash.exe", "-c", "cat")
            < Redirect.FromFile("test.txt")
            > Redirect.Output.ToFile("output.txt", true)
        );
        Assert.AreEqual("RedirectFromFileAndPipeToCat", File.ReadAllText("output.txt"));
    }
    
    [Test]
    public void RedirectMultipleFilesAndPipeToCat()
    {
        var shell = new ProcessShell();
        File.WriteAllTextAsync("test.txt", "RedirectFromFileAndPipeToCat");
        File.WriteAllTextAsync("test2.txt", "SecondFile");
        shell.AppendPath(@"C:\Program Files\Git\usr\bin");
        shell.Run(
            CreateProcess.FromCommandLine(@"C:\Program Files\Git\usr\bin\bash.exe", "-c", "cat")
            < Redirect.FromFile("test.txt")
            < Redirect.FromFile("test2.txt")
            > Redirect.Output.ToFile("output.txt", true)
        );
        Assert.AreEqual("RedirectFromFileAndPipeToCatSecondFile", File.ReadAllText("output.txt"));
    }
    
    [Test]
    public void RedirectToFileAndPipeToCat()
    {
        var shell = new ProcessShell();
        shell.AppendPath(@"C:\Program Files\Git\usr\bin");
        shell.Run(
            CreateProcess.FromCommandLine(@"C:\Program Files\Git\usr\bin\bash.exe", "-c", "echo test single argument")
            > Redirect.Output.ToFile("test.txt", true)
            | CreateProcess.FromCommandLine(@"C:\Program Files\Git\usr\bin\bash.exe", "-c", "cat 1>&2")
            > Redirect.Error.ToFile("other.txt", true)
        );
        Assert.AreEqual("test single argument\n", File.ReadAllText("test.txt"));
        Assert.AreEqual("test single argument\n", File.ReadAllText("other.txt"));
    }
    
    [Test]
    public void RedirectToFileAndPipeToCatCheckAllExitCodes()
    {
        var shell = new ProcessShell();
        shell.AppendPath(@"C:\Program Files\Git\usr\bin");
        shell.Run(
            CreateProcess.FromCommandLine(@"C:\Program Files\Git\usr\bin\bash.exe", "-c", "echo test single argument")
            > Redirect.Output.ToFile("test.txt", true)
            == 0
            | CreateProcess.FromCommandLine(@"C:\Program Files\Git\usr\bin\bash.exe", "-c", "cat -; exit 1")
            == 1
            | CreateProcess.FromCommandLine(@"C:\Program Files\Git\usr\bin\bash.exe", "-c", "cat 1>&2")
            > Redirect.Error.ToFile("other.txt", true)
            == 0
        );
        Assert.AreEqual("test single argument\n", File.ReadAllText("test.txt"));
        Assert.AreEqual("test single argument\n", File.ReadAllText("other.txt"));
    }
    
    //[Test]
    //public async Task RedirectErrorToOutput()
    //{
    //    var shell = new ProcessShell();
    //    await shell.RunAsync(
    //        CreateProcess.FromCommandLine(@"C:\Program Files\Git\usr\bin\bash.exe", "-c", "echo test 2>&1")
    //        > Redirect.Output.ToFile("test.txt", true)
    //        > Redirect.Error.ToStandardOutput
    //    );
    //    Assert.AreEqual("test", await File.ReadAllTextAsync("test.txt"));
    //}
    //
    //[Test]
    //public async Task RedirectErrorToOutputReverse()
    //{
    //    var shell = new ProcessShell();
    //    await shell.RunAsync(
    //        CreateProcess.FromCommandLine(@"C:\Program Files\Git\usr\bin\bash.exe", "-c", "echo test 2>&1")
    //        > Redirect.Error.ToStandardOutput
    //        > Redirect.Output.ToFile("test.txt", true)
    //    );
    //    Assert.AreEqual("test", await File.ReadAllTextAsync("test.txt"));
    //}
    
    [Test]
    public void ExitCodeThrows()
    {
        var shell = new ProcessShell();
        Assert.ThrowsAsync<ProcessErroredException>(() =>
            shell.RunAsync(
                CreateProcess.FromCommandLine(@"C:\Program Files\Git\usr\bin\bash.exe", "-c", "exit 1")
                > Redirect.Output.ToFile("test.txt", true)
            ));
    }
    
    [Test]
    public async Task ExitCodeCheckCanBeDisabled()
    {
        var shell = new ProcessShell();
        var r = await shell.RunAsync(
            (CreateProcess.FromCommandLine(@"C:\Program Files\Git\usr\bin\bash.exe", "-c", "exit 1")
            > Redirect.Output.ToFile("test.txt", true))
            == Option.DisableNullCheck
        );
        
        Assert.AreEqual(1, r.Results.Count);
        Assert.AreEqual(1, r.Results[0].ExitCode);
    }

    
    [Test]
    public void ExitCodeThrowsWhenCheckedForAnyValue()
    {
        var shell = new ProcessShell();
        Assert.ThrowsAsync<ProcessErroredException>(() =>
            shell.RunAsync(
                CreateProcess.FromCommandLine(@"C:\Program Files\Git\usr\bin\bash.exe", "-c", "exit 2")
                > Redirect.Output.ToFile("test.txt", true)
                == 1
            ));
    }
    [Test]
    public async Task ExitCodeCanBeCheckedForAnyValue()
    {
        var shell = new ProcessShell();
        await shell.RunAsync(
            CreateProcess.FromCommandLine(@"C:\Program Files\Git\usr\bin\bash.exe", "-c", "exit 2")
            > Redirect.Output.ToFile("test.txt", true)
            == 2
        );
    }
}
