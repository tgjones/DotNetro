internal interface ITargetSystem
{
    string Name { get; }
    ushort LoadAddress { get; }
    void InitialiseMemory(byte[] memory);
    EmulationResult Run(byte[] memory, TextReader input, TextWriter? trace, int maxTicks);
}

internal sealed record EmulationResult(string Output, EmulationStatus Status);

internal enum EmulationStatus { Completed, RunawayTimeout }
