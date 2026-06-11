// Reads a compiled .dll from stdin and runs it under the host .NET runtime,
// feeding optional console input.

using System.CommandLine;
using System.Diagnostics;

var inputOption = new Option<string?>("--input") { Description = "Console input. Use \\r to separate lines." };

var rootCommand = new RootCommand("DotNetro .NET runner for Lit tests");
rootCommand.Options.Add(inputOption);

rootCommand.SetAction(parseResult =>
{
    var inputString = parseResult.GetValue(inputOption);

    var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);

    try
    {
        var dllPath = Path.Combine(tempDir, "program.dll");

        using (var fs = File.OpenWrite(dllPath))
            Console.OpenStandardInput().CopyTo(fs);

        // Copy our own runtimeconfig.json so the child process uses the same runtime.
        var myRuntimeConfig = Path.Combine(AppContext.BaseDirectory,
            "DotNetro.Compiler.Tests.DotNetRunner.runtimeconfig.json");
        File.Copy(myRuntimeConfig, Path.Combine(tempDir, "program.runtimeconfig.json"));

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("dotnet", dllPath)
        {
            UseShellExecute = false,
            // Redirect only stdin so we can feed --input. Leave stdout/stderr
            // unredirected so the child writes directly through our own stdout/stderr
            // to the executor's pipe — no double-buffering.
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };

        process.Start();

        // Replace the \r escape with a newline so ReadLine() in the child process
        // receives separate lines, matching the emulator's --input convention.
        if (inputString != null)
            process.StandardInput.Write(inputString.Replace("\\r", "\n"));
        process.StandardInput.Close();

        process.WaitForExit();
        return process.ExitCode;
    }
    finally
    {
        Directory.Delete(tempDir, recursive: true);
    }
});

return await rootCommand.Parse(args).InvokeAsync();
