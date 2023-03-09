using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Globalization;
using GameBreaker;
using GameBreaker.Chunks;
using GameBreaker.Models;
using static GameBreaker.Models.GMCode.Bytecode;
using static GameBreaker.Models.GMCode.Bytecode.Instruction;

namespace GameBreaker.Project.GML.Decompiler;

public static class Disassembler
{
    public static Dictionary<GMCode.Bytecode.Instruction.DataType, char> DataTypeToChar = new Dictionary<GMCode.Bytecode.Instruction.DataType, char>()
    {
        { GMCode.Bytecode.Instruction.DataType.Double, 'd' },
        { GMCode.Bytecode.Instruction.DataType.Float, 'f' },
        { GMCode.Bytecode.Instruction.DataType.Int32, 'i' },
        { GMCode.Bytecode.Instruction.DataType.Int64, 'l' },
        { GMCode.Bytecode.Instruction.DataType.Boolean, 'b' },
        { GMCode.Bytecode.Instruction.DataType.Variable, 'v' },
        { GMCode.Bytecode.Instruction.DataType.String, 's' },
        { GMCode.Bytecode.Instruction.DataType.Int16, 'e' }
    };
    public static Dictionary<char, GMCode.Bytecode.Instruction.DataType> CharToDataType = new Dictionary<char, GMCode.Bytecode.Instruction.DataType>()
    {
        { 'd', GMCode.Bytecode.Instruction.DataType.Double },
        { 'f', GMCode.Bytecode.Instruction.DataType.Float },
        { 'i', GMCode.Bytecode.Instruction.DataType.Int32 },
        { 'l', GMCode.Bytecode.Instruction.DataType.Int64 },
        { 'b', GMCode.Bytecode.Instruction.DataType.Boolean },
        { 'v', GMCode.Bytecode.Instruction.DataType.Variable },
        { 's', GMCode.Bytecode.Instruction.DataType.String },
        { 'e', GMCode.Bytecode.Instruction.DataType.Int16}
    };
    public static Dictionary<ushort, string> BreakIDToName = new Dictionary<ushort, string>()
    {
        { (ushort)GMCode.Bytecode.Instruction.BreakType.chkindex, "chkindex" },
        { (ushort)GMCode.Bytecode.Instruction.BreakType.pushaf, "pushaf" },
        { (ushort)GMCode.Bytecode.Instruction.BreakType.popaf, "popaf" },
        { (ushort)GMCode.Bytecode.Instruction.BreakType.pushac, "pushac" },
        { (ushort)GMCode.Bytecode.Instruction.BreakType.setowner, "setowner" },
        { (ushort)GMCode.Bytecode.Instruction.BreakType.isstaticok, "isstaticok" },
        { (ushort)GMCode.Bytecode.Instruction.BreakType.setstatic, "setstatic" },
        { (ushort)GMCode.Bytecode.Instruction.BreakType.savearef, "savearef" },
        { (ushort)GMCode.Bytecode.Instruction.BreakType.restorearef, "restorearef" },
        { (ushort)GMCode.Bytecode.Instruction.BreakType.isnullish, "isnullish" }
    };

    public static string Disassemble(GMCode codeEntry, GMData data)
    {
        GMCode.Bytecode bytecode = codeEntry.BytecodeEntry;
        IList<GMString> strings = ((GMChunkSTRG)data.Chunks["STRG"]).List;

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"# Name: {codeEntry.Name.Content}");
        if (codeEntry.BytecodeOffset != 0) // Usually should be 0, but for information sake write this
            sb.AppendLine($"# Offset: {codeEntry.BytecodeOffset}");

        List<int> blocks = FindBlockAddresses(bytecode);
        foreach (var i in bytecode.Instructions)
        {
            int ind = blocks.IndexOf(i.Address);
            if (ind != -1)
            {
                sb.AppendLine();
                sb.AppendLine($":[{ind}]");
            }

            if (i.Kind != GMCode.Bytecode.Instruction.Opcode.Break)
                sb.Append(i.Kind.ToString().ToLower());

            switch (GetInstructionType(i.Kind))
            {
                case GMCode.Bytecode.Instruction.InstructionType.SingleType:
                    sb.Append($".{DataTypeToChar[i.Type1]}");

                    if (i.Kind == GMCode.Bytecode.Instruction.Opcode.CallV)
                        sb.Append($" {i.Extra}");
                    else if (i.Kind == GMCode.Bytecode.Instruction.Opcode.Dup)
                    {
                        sb.Append($" {i.Extra}");
                        if ((byte)i.ComparisonKind != 0)
                            sb.Append($" {(byte)i.ComparisonKind & 0x7F}");
                    }
                    break;

                case GMCode.Bytecode.Instruction.InstructionType.DoubleType:
                    sb.Append($".{DataTypeToChar[i.Type1]}.{DataTypeToChar[i.Type2]}");
                    break;

                case GMCode.Bytecode.Instruction.InstructionType.Comparison:
                    sb.Append($".{DataTypeToChar[i.Type1]}.{DataTypeToChar[i.Type2]} {i.ComparisonKind}");
                    break;

                case GMCode.Bytecode.Instruction.InstructionType.Branch:
                    if (i.Address + (i.JumpOffset * 4) == codeEntry.Length)
                        sb.Append(" [end]");
                    else if (i.PopenvExitMagic)
                        sb.Append(" [magic]"); // magic popenv instruction when returning early inside a with statement
                    else
                        sb.Append($" [{blocks.IndexOf(i.Address + (i.JumpOffset * 4))}]");
                    break;

                case GMCode.Bytecode.Instruction.InstructionType.Pop:
                    sb.Append($".{DataTypeToChar[i.Type1]}.{DataTypeToChar[i.Type2]} ");
                    if (i.Type1 == GMCode.Bytecode.Instruction.DataType.Int16)
                        sb.Append(((short)i.TypeInst).ToString()); // Special swap instruction
                    else
                    {
                        if (i.Type1 == GMCode.Bytecode.Instruction.DataType.Variable && i.TypeInst != GMCode.Bytecode.Instruction.InstanceType.Undefined)
                        {
                            sb.Append($"{i.TypeInst.ToString().ToLower()}.");
                        }

                        sb.Append(StringifyVariableRef(i.Variable));
                    }
                    break;

                case GMCode.Bytecode.Instruction.InstructionType.Push:
                    sb.Append($".{DataTypeToChar[i.Type1]} ");
                    if (i.Type1 == GMCode.Bytecode.Instruction.DataType.Variable)
                    {
                        if (i.TypeInst != GMCode.Bytecode.Instruction.InstanceType.Undefined)
                            sb.Append($"{i.TypeInst.ToString().ToLower()}.");

                        sb.Append(StringifyVariableRef(i.Variable));
                    }
                    else if (i.Type1 == GMCode.Bytecode.Instruction.DataType.String)
                        sb.Append($"\"{SanitizeString(strings[(int)i.Value].Content)}\"");
                    else if (i.Function != null)
                        sb.Append(i.Function.Target.Name.Content);
                    else
                        sb.Append((i.Value as IFormattable)?.ToString(null, CultureInfo.InvariantCulture) ?? i.Value.ToString());
                    break;

                case GMCode.Bytecode.Instruction.InstructionType.Call:
                    sb.Append($".{DataTypeToChar[i.Type1]} {i.Function.Target.Name.Content} {(short)i.Value}");
                    break;

                case GMCode.Bytecode.Instruction.InstructionType.Break:
                    sb.Append($"{BreakIDToName[(ushort)i.Value]}.{DataTypeToChar[i.Type1]}");
                    break;
            }
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.Append(":[end]");

        return sb.ToString();
    }

    public static List<int> FindBlockAddresses(GMCode.Bytecode bytecode, bool slow = true)
    {
        HashSet<int> addresses = new HashSet<int>();

        if (bytecode.Instructions.Count != 0)
        {
            addresses.Add(0);
            for (int i = 0; i < bytecode.Instructions.Count; i++)
            {
                GMCode.Bytecode.Instruction instr = bytecode.Instructions[i];
                switch (instr.Kind)
                {
                    case GMCode.Bytecode.Instruction.Opcode.B:
                    case GMCode.Bytecode.Instruction.Opcode.Bf:
                    case GMCode.Bytecode.Instruction.Opcode.Bt:
                    case GMCode.Bytecode.Instruction.Opcode.PushEnv:
                        addresses.Add(instr.Address + 4);
                        addresses.Add(instr.Address + (instr.JumpOffset * 4));
                        break;
                    case GMCode.Bytecode.Instruction.Opcode.PopEnv:
                        if (!instr.PopenvExitMagic)
                            addresses.Add(instr.Address + (instr.JumpOffset * 4));
                        break;
                    case GMCode.Bytecode.Instruction.Opcode.Exit:
                    case GMCode.Bytecode.Instruction.Opcode.Ret:
                        addresses.Add(instr.Address + 4);
                        break;
                    case GMCode.Bytecode.Instruction.Opcode.Call:
                        if (slow && i >= 4 && instr.Function.Target.Name?.Content == "@@try_hook@@")
                        {
                            int finallyBlock = (int)bytecode.Instructions[i - 4].Value;
                            addresses.Add(finallyBlock);

                            int catchBlock = (int)bytecode.Instructions[i - 2].Value;
                            if (catchBlock != -1)
                                addresses.Add(catchBlock);

                            // Technically not usually a block here (before/after the call), but for our purposes, 
                            // this is easier to split into its own section to isolate it now.
                            addresses.Add(instr.Address - 24);
                            addresses.Add(instr.Address + 12);
                        }
                        break;
                }
            }
        }

        List<int> res = addresses.ToList();
        res.Sort();
        return res;
    }

    public static string SanitizeString(string str)
    {
        StringBuilder sb = new StringBuilder();
        foreach (char c in str)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\n");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\v':
                    sb.Append("\\v");
                    break;
                case '\a':
                    sb.Append("\\a");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string StringifyVariableRef(GMCode.Bytecode.Instruction.Reference<GMVariable> var)
    {
        if (var.Type != GMCode.Bytecode.Instruction.VariableType.Normal)
            return $"[{var.Type.ToString().ToLower()}]{var.Target.VariableType.ToString().ToLower()}.{var.Target.Name.Content}";
        else
            return var.Target.Name.Content;
    }
}
