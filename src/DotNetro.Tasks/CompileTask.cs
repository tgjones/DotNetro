using DotNetro.Compiler;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotNetro.Tasks;

public sealed class CompileTask : Task
{
    [Required]
    public string TargetPath { get; set; } = "";

    [Required]
    public string OutputPath { get; set; } = "";

    public bool DiagnosticLogging { get; set; }

    public override bool Execute()
    {
        var logger = DiagnosticLogging
            ? new MSBuildLogger(Log)
            : null;

        DotNetCompiler.Compile(TargetPath, "Main", OutputPath, logger);

        return !Log.HasLoggedErrors;
    }
}
