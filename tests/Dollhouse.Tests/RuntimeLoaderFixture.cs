using System.Buffers.Binary;
using System.Text;
using Dollhouse.Core.Patching.Native;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Dollhouse.Tests;

/// <summary>Small, analyst-written PE fixtures. Never copy/execute any game code.</summary>
internal static class RuntimeLoaderFixture
{
    internal static byte[] Executable()
    {
        var function = new ImportedFunction("ExitProcess");
        var imports = ImportTable.Build("KERNEL32.dll", [function], 0x2000, []);
        using var stream = new MemoryStream();
        var encoder = Iced.Intel.Encoder.Create(64, new StreamCodeWriter(stream));
        ulong ip = 0x1000;
        foreach (var instruction in new[] {
            Instruction.Create(Code.Sub_rm64_imm8, Register.RSP, 40),
            Instruction.Create(Code.Mov_r32_imm32, Register.ECX, 73u),
            Instruction.Create(Code.Call_rm64, new MemoryOperand(Register.RIP, imports.Slots[function], 4)),
            Instruction.Create(Code.Int3) }) ip += encoder.Encode(instruction, ip);
        var result = Make(stream.ToArray(), imports.Bytes, false);
        Int(result, 0x98 + 120, 0x2000); Int(result, 0x98 + 124, imports.DescriptorTableSize);
        return result;
    }

    internal static byte[] Library(int status, string name = "DollhouseMain")
    {
        var data = new byte[256];
        Int(data, 12, 0x2060); Int(data, 16, 1); Int(data, 20, 1); Int(data, 24, 1);
        Int(data, 28, 0x2040); Int(data, 32, 0x2048); Int(data, 36, 0x2050);
        Int(data, 0x40, 0x1000); Int(data, 0x48, 0x2080);
        Encoding.ASCII.GetBytes("dhruntime.dll\0").CopyTo(data, 0x60);
        Encoding.ASCII.GetBytes(name + '\0').CopyTo(data, 0x80);
        var a = new Assembler(64);
        a.mov(eax, status); a.ret();
        using var stream = new MemoryStream();
        a.Assemble(new StreamCodeWriter(stream), 0x1000);
        var result = Make(stream.ToArray(), data, true);
        Int(result, 0x98 + 112, 0x2000); Int(result, 0x98 + 116, 256);
        return result;
    }

    private static byte[] Make(byte[] code, byte[] data, bool library)
    {
        var result = new byte[0x800];
        result[0] = (byte)'M'; result[1] = (byte)'Z'; Int(result, 60, 0x80);
        Int(result, 0x80, 0x4550); Short(0x84, 0x8664); Short(0x86, 2); Short(0x94, 240);
        Short(0x96, (ushort)(library ? 0x2022 : 0x22));
        const int optional = 0x98;
        Short(optional, 0x20b); Int(result, optional + 4, 0x200); Int(result, optional + 8, 0x200);
        Int(result, optional + 16, library ? 0 : 0x1000); Int(result, optional + 20, 0x1000);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(optional + 24), library ? 0x180000000 : 0x140000000);
        Int(result, optional + 32, 0x1000); Int(result, optional + 36, 0x200);
        Short(optional + 40, 6); Short(optional + 48, 6);
        Int(result, optional + 56, 0x3000); Int(result, optional + 60, 0x400);
        Short(optional + 68, 3); Short(optional + 70, 0x140);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(optional + 72), 1024 * 1024);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(optional + 80), 4096);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(optional + 88), 1024 * 1024);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(optional + 96), 4096);
        Int(result, optional + 108, 16);
        // An all-ABSOLUTE relocation block permits ASLR; fixture code/data use RVAs only.
        Int(result, optional + 112 + 5 * 8, 0x2180); Int(result, optional + 116 + 5 * 8, 12);
        Int(result, 0x780 + 4, 12);
        Section(0x188, ".text", 0x1000, 0x400, code.Length, 0x60000020);
        Section(0x1b0, ".idata", 0x2000, 0x600, 0x200, 0xc0000040);
        code.CopyTo(result, 0x400); data.CopyTo(result, 0x600);
        return result;
        void Short(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(offset), value);
        void Section(int offset, string name, int rva, int raw, int size, uint flags)
        {
            Encoding.ASCII.GetBytes(name).CopyTo(result, offset);
            Int(result, offset + 8, size); Int(result, offset + 12, rva); Int(result, offset + 16, 0x200);
            Int(result, offset + 20, raw); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset + 36), flags);
        }
    }
    private static void Int(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
}
