using System.Buffers.Binary;
using System.Text;
using Aemula.Chips.Mos6502;

internal sealed class BbcMicroTargetSystem : ITargetSystem
{
    public string Name => "bbcmicro";
    public ushort LoadAddress => 0x2000;

    public void InitialiseMemory(byte[] memory)
    {
        memory[0xFFEE] = 0x60; // OSWRCH: RTS
        memory[0xFFE3] = 0x60; // OSASCI: RTS
        memory[0xFFF1] = 0x60; // OSWORD: RTS

        memory[0xFFFC] = 0x00; // Reset vector → $FF00
        memory[0xFFFD] = 0xFF;

        memory[0xFF00] = 0x20; // JSR $2000
        memory[0xFF01] = 0x00;
        memory[0xFF02] = 0x20;
        memory[0xFF03] = 0x60; // RTS (end marker)
    }

    public EmulationResult Run(byte[] memory, TextReader input, TextWriter? trace, int maxTicks)
    {
        var cpu = new Mos6502(Mos6502Options.Default);
        ref var pins = ref cpu.Pins;

        var output = new StringBuilder();
        var ticks = 0;

        while (true)
        {
            cpu.Tick();

            var address = pins.Address;
            if (pins.RW)
                pins.Data = memory[address];
            else
                memory[address] = pins.Data;

            if (pins.Sync)
            {
                trace?.WriteLine($"{cpu.PC:X4}  A:{cpu.A:X2} X:{cpu.X:X2} Y:{cpu.Y:X2} P:{cpu.P.AsByte(false):X2} SP:{cpu.SP:X2}");
            }

            switch (cpu.PC)
            {
                case 0xFF03:
                    return new EmulationResult(output.ToString(), EmulationStatus.Completed);

                case 0xFFE3:
                    output.Append((char)cpu.A);
                    break;

                case 0xFFF1:
                    if (cpu.A == 0) // OSWORD 0: read line
                    {
                        var line = input.ReadLine() ?? string.Empty;
                        var stringAddress = BinaryPrimitives.ReadUInt16LittleEndian(memory.AsSpan(0x37));
                        Encoding.ASCII.GetBytes(line + '\r').CopyTo(memory.AsSpan(stringAddress));
                    }
                    break;
            }

            ticks++;
            if (ticks >= maxTicks)
                return new EmulationResult(output.ToString(), EmulationStatus.RunawayTimeout);
        }
    }
}
