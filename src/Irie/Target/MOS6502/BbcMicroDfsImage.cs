using System;
using System.Text.RegularExpressions;

namespace Irie.Target.MOS6502;

// Packages a flat 6502 byte buffer into an Acorn DFS single-sided
// single-density disk image (.ssd): two catalogue sectors (0 + 1) followed
// by the file's data starting at sector 2.
internal static class BbcMicroDfsImage
{
    private const int SectorSize = 256;
    private const string DiskTitle = "DotNetro"; // exactly 8 chars; bytes 8-11 stay zero
    private static readonly Regex FileNameRegex = new("^[A-Z0-9_!]+$", RegexOptions.Compiled);

    public static byte[] Build(byte[] code, int loadAddress, int execAddress, string fileName)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(fileName);

        var normalizedName = ValidateAndNormalizeFileName(fileName);

        var dataSectorCount = (code.Length + SectorSize - 1) / SectorSize;
        var totalSectors = 2 + dataSectorCount;
        var image = new byte[totalSectors * SectorSize];

        // --- Sector 0: disk title chars 0-7, then file entries (7-char name + dir char). ---
        for (var i = 0; i < 8; i++) // 0x00-0x07: title chars 0-7
            image[i] = (byte)DiskTitle[i];

        for (var i = 0; i < 7; i++) // 0x08-0x0E: file name chars 0-6, space-padded
            image[0x08 + i] = (byte)(i < normalizedName.Length ? normalizedName[i] : ' ');
        image[0x0F] = (byte)'$'; // 0x0F: directory char

        // --- Sector 1: title cont, cycle, file count, boot/sectors, file entries. ---
        // 0x100-0x103: title chars 8-11 (left as zero — title is only 8 chars).
        image[0x104] = 0x00; // cycle number (BCD)
        image[0x105] = 1 * 8; // number of file entries * 8
        image[0x106] = (byte)(0x20 | ((totalSectors >> 8) & 0x03)); // boot option 2 (*RUN !BOOT); low 2 bits = total-sectors high bits
        image[0x107] = (byte)(totalSectors & 0xFF); // total-sectors low

        // 0x108-0x10F: file entry — load(2), exec(2), len(2), packed-high(1), start-sector(1).
        image[0x108] = (byte)(loadAddress & 0xFF);
        image[0x109] = (byte)((loadAddress >> 8) & 0xFF);
        image[0x10A] = (byte)(execAddress & 0xFF);
        image[0x10B] = (byte)((execAddress >> 8) & 0xFF);
        image[0x10C] = (byte)(code.Length & 0xFF);
        image[0x10D] = (byte)((code.Length >> 8) & 0xFF);
        image[0x10E] = EncodePackedHighBits(loadAddress, execAddress, code.Length, startSector: 2);
        image[0x10F] = 2; // start sector low 8 bits

        // --- Sector 2+: file data. ---
        Array.Copy(code, 0, image, 0x200, code.Length);

        return image;
    }

    // Packs bits 17:16 of exec/length/load plus bits 9:8 of start-sector into one byte.
    private static byte EncodePackedHighBits(int load, int exec, int length, int startSector)
    {
        var execHi = (exec >> 16) & 0x03;
        var lenHi = (length >> 16) & 0x03;
        var loadHi = (load >> 16) & 0x03;
        var startHi = (startSector >> 8) & 0x03;
        return (byte)((execHi << 6) | (lenHi << 4) | (loadHi << 2) | startHi);
    }

    private static string ValidateAndNormalizeFileName(string fileName)
    {
        if (fileName.Length == 0)
            throw new ArgumentException("File name must be non-empty.", nameof(fileName));
        if (fileName.Length > 7)
            throw new ArgumentException($"File name '{fileName}' is longer than 7 characters.", nameof(fileName));

        var upper = fileName.ToUpperInvariant();
        if (!FileNameRegex.IsMatch(upper))
            throw new ArgumentException($"File name '{fileName}' must match [A-Z0-9_]+ (after uppercasing).", nameof(fileName));

        return upper;
    }
}
