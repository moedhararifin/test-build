using Iced.Intel;

namespace Dollhouse.Core.Patching.Native;

internal sealed record ImportedFunction(string Name);
internal sealed record NativeLeaf(string Name, X64Code Code);
internal sealed record NativeData(string Name, byte[] Bytes, int Alignment);

internal sealed record ImportAddresses(
    IReadOnlyDictionary<ImportedFunction, int> Slots,
    IReadOnlyDictionary<ImportedFunction, int> Thunks,
    IReadOnlyDictionary<NativeLeaf, int>? Leaves = null,
    IReadOnlyDictionary<NativeData, int>? Data = null)
{
    public static ImportAddresses Empty { get; } = new(
        new Dictionary<ImportedFunction, int>(), new Dictionary<ImportedFunction, int>());
}

/// <summary>A short instruction sequence. Iced owns encoding, lengths and relative displacements.</summary>
internal sealed class X64Code
{
    private readonly List<Func<ImportAddresses, Instruction>> instructions = [];
    private readonly List<ImportedFunction> calls = [];
    public IEnumerable<ImportedFunction> CalledFunctions => calls;

    public void StoreOnStack(int offset, Register source) => Add(Instruction.Create(
        Code.Mov_rm64_r64, new MemoryOperand(Register.RSP, offset, 1), source));

    public void Push(Register register) => Add(Instruction.Create(Code.Push_r64, register));

    public void AddressOnStack(Register destination, int offset) => Add(Instruction.Create(
        Code.Lea_r64_m, destination, new MemoryOperand(Register.RSP, offset, 1)));

    public void Address(Register destination, Register source, int offset) => Add(Instruction.Create(
        Code.Lea_r64_m, destination, new MemoryOperand(source, offset, 1)));

    public void Load(Register destination, Register source, int offset) => Add(Instruction.Create(
        Code.Mov_r64_rm64, destination, new MemoryOperand(source, offset, 1)));

    public void Store(Register destination, uint value) => Add(Instruction.Create(
        Code.Mov_rm32_imm32, new MemoryOperand(destination), value));

    public void LoadAddress(Register destination, int address) => Add(Instruction.Create(
        Code.Lea_r64_m, destination, new MemoryOperand(Register.RIP, address, 4)));

    public void LoadAddress(Register destination, NativeData data) => instructions.Add(addresses => Instruction.Create(
        Code.Lea_r64_m, destination, new MemoryOperand(Register.RIP, addresses.Data![data], 4)));

    public void Move(Register destination, Register source)
    {
        var size = destination.GetSize();
        if (size != source.GetSize() || size is not (4 or 8))
            throw new ArgumentException("Moves require matching 32-bit or 64-bit registers.");
        Add(Instruction.Create(size == 8 ? Code.Mov_rm64_r64 : Code.Mov_rm32_r32, destination, source));
    }

    // MSVC also emits the load-form register encoding. Use it when matching
    // an exact native preimage; both forms have identical execution semantics.
    public void MoveFrom(Register destination, Register source) =>
        Add(Instruction.Create(Code.Mov_r64_rm64, destination, source));

    public void CompareByteAt(int address, byte value) => Add(Instruction.Create(
        Code.Cmp_rm8_imm8, new MemoryOperand(Register.RIP, address, 4), (int)value));

    public void CompareByte(Register owner, int offset, Register value) => Add(Instruction.Create(
        Code.Cmp_rm8_r8, new MemoryOperand(owner, offset, 8), value));

    public void OrByte(Register owner, int offset, Register value) => Add(Instruction.Create(
        Code.Or_rm8_r8, new MemoryOperand(owner, offset, 8), value));

    public void Compare(Register register, int value) => Add(Instruction.Create(Code.Cmp_rm32_imm8, register, value));
    public void CompareDwordOnStack(int offset, int value) => Add(Instruction.Create(
        Code.Cmp_rm32_imm8, new MemoryOperand(Register.RSP, offset, 1), value));
    public void SetEqual(Register register) => Add(Instruction.Create(Code.Sete_rm8, register));
    public void JumpIfNotEqual(int address) => Add(Instruction.CreateBranch(Code.Jne_rel32_64, checked((ulong)address)));
    public void Jump(NativeLeaf function) => instructions.Add(imports => Instruction.CreateBranch(
        Code.Jmp_rel32_64, checked((ulong)imports.Leaves![function])));

    public void Call(int address) => Add(Instruction.CreateBranch(Code.Call_rel32_64, checked((ulong)address)));
    public void Call(NativeLeaf function) => instructions.Add(addresses => Instruction.CreateBranch(
        Code.Call_rel32_64, checked((ulong)addresses.Leaves![function])));

    public void Call(ImportedFunction function)
    {
        calls.Add(function);
        instructions.Add(imports => Instruction.CreateBranch(Code.Call_rel32_64, checked((ulong)imports.Thunks[function])));
    }

    public void Jump(ImportedFunction function) => instructions.Add(imports => Instruction.Create(
        Code.Jmp_rm64, new MemoryOperand(Register.RIP, imports.Slots[function], 4)));

    public void Jump(int address) => Add(Instruction.CreateBranch(Code.Jmp_rel32_64, checked((ulong)address)));

    private void Add(Instruction instruction) => instructions.Add(_ => instruction);

    public byte[] Assemble(int address, ImportAddresses? imports = null)
    {
        using var stream = new MemoryStream();
        var encoder = Encoder.Create(64, new StreamCodeWriter(stream));
        ulong nextInstruction = checked((ulong)address);
        foreach (var instruction in instructions)
            nextInstruction += encoder.Encode(instruction(imports ?? ImportAddresses.Empty), nextInstruction);
        return stream.ToArray();
    }

    public static void RequireWholeInstructions(ReadOnlySpan<byte> bytes, int address)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes.ToArray()), checked((ulong)address));
        ulong end = decoder.IP + (uint)bytes.Length;
        while (decoder.IP < end)
            if (decoder.Decode().IsInvalid || decoder.IP > end)
                throw new InvalidDataException($"Patch span at RVA 0x{address:x} splits or contains an invalid instruction.");
    }

}
