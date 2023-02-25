using System.Collections.Generic;
using GameBreaker.Core.Models;
using static GameBreaker.Core.Models.GMCode.Bytecode;
using static GameBreaker.Core.Models.GMCode.Bytecode.Instruction;

namespace GameBreaker.Project.GML.Compiler;

public static partial class Bytecode
{
    /// <summary>
    /// Represents an instruction to be patched with a function reference after instructions have all been written.
    /// </summary>
    public record FunctionPatch(GMCode.Bytecode.Instruction Target, TokenFunction Token, FunctionReference Reference);

    /// <summary>
    /// Represents an instruction to be patched with a variable reference after instructions have all been written.
    /// </summary>
    public record VariablePatch(GMCode.Bytecode.Instruction Target, TokenVariable Token);

    /// <summary>
    /// Represents an instruction to be patched with a string ID after instructions have all been written.
    /// </summary>
    public record StringPatch(GMCode.Bytecode.Instruction Target, string Content);

    /// <summary>
    /// Represents branch instructions that must be later patched to point to correct addresses.
    /// </summary>
    public interface IJumpPatch
    {
        /// <summary>
        /// Adds a new instruction to be patched by this specific instance.
        /// </summary>
        public void Add(GMCode.Bytecode.Instruction instr);

        /// <summary>
        /// Patches all of the targeted instructions to point to desired address.
        /// </summary>
        public void Finish(CodeContext ctx);
    }

    /// <summary>
    /// Jump patch where every instruction included will be jumping forwards to the address
    /// reached when calling Finish().
    /// </summary>
    public class JumpForwardPatch : IJumpPatch
    {
        private readonly List<GMCode.Bytecode.Instruction> _patches;

        public JumpForwardPatch()
        {
            _patches = new();
        }

        public JumpForwardPatch(GMCode.Bytecode.Instruction firstToPatch)
        {
            _patches = new() { firstToPatch };
        }

        public void Add(GMCode.Bytecode.Instruction instr)
        {
            _patches.Add(instr);
        }

        public void Finish(CodeContext ctx)
        {
            foreach (var instr in _patches)
                instr.JumpOffset = (ctx.BytecodeLength - instr.Address) / 4;
        }
    }

    /// <summary>
    /// Jump patch where every instruction included will be jumping backwards to the address
    /// at the time of the instance's instantiation.
    /// </summary>
    public class JumpBackwardPatch : IJumpPatch
    {
        private readonly List<GMCode.Bytecode.Instruction> _patches;
        private readonly int _startAddress;

        public JumpBackwardPatch(CodeContext ctx)
        {
            _startAddress = ctx.BytecodeLength;
            _patches = new();
        }

        public void Add(GMCode.Bytecode.Instruction instr)
        {
            _patches.Add(instr);
        }

        public void Finish(CodeContext ctx)
        {
            foreach (var instr in _patches)
                instr.JumpOffset = (_startAddress - instr.Address) / 4;
        }
    }

    /// <summary>
    /// Helper record used for generating code for switch statements.
    /// </summary>
    public record SwitchCase(JumpForwardPatch Jump, int ChildIndex);

    /// <summary>
    /// Represents a loop or context where break/continue need special behavior.
    /// </summary>
    public class Context
    {
        public enum ContextKind
        {
            BasicLoop,
            With,
            Switch,
            Repeat,
            // todo: try/catch/finally?
        }

        public ContextKind Kind { get; init; }
        public bool BreakUsed { get; private set; }
        public bool ContinueUsed { get; private set; }
        public GMCode.Bytecode.Instruction.DataType DuplicatedType { get; init; }

        // Internal references to break/continue patches
        private readonly IJumpPatch _breakJump;
        private readonly IJumpPatch _continueJump;

        /// <summary>
        /// Instantiates a new context of any kind, given its break/continue jump patches.
        /// </summary>
        public Context(ContextKind kind, IJumpPatch breakJump, IJumpPatch continueJump)
        {
            Kind = kind;
            _breakJump = breakJump;
            _continueJump = continueJump;
        }

        /// <summary>
        /// Instantiates a new switch statement context, given the break/continue jump patches, 
        /// and the type of the data being duplicated by the switch statement.
        /// </summary>
        public Context(IJumpPatch breakJump, IJumpPatch continueJump, GMCode.Bytecode.Instruction.DataType duplicatedType)
        {
            Kind = ContextKind.Switch;
            _breakJump = breakJump;
            _continueJump = continueJump;
            DuplicatedType = duplicatedType;
        }

        /// <summary>
        /// Returns the jump patch that should be used for this context, when "break" is used.
        /// Also marks this context to have used "break".
        /// </summary>
        public IJumpPatch UseBreakJump()
        {
            BreakUsed = true;
            return _breakJump;
        }

        /// <summary>
        /// Returns the jump patch that should be used for this context, when "continue" is used.
        /// Also marks this context to have used "continue".
        /// </summary>
        public IJumpPatch UseContinueJump()
        {
            ContinueUsed = true;
            return _continueJump;
        }
    }

    /// <summary>
    /// Emits a basic instruction with no properties.
    /// </summary>
    private static GMCode.Bytecode.Instruction Emit(CodeContext ctx, GMCode.Bytecode.Instruction.Opcode opcode)
    {
        GMCode.Bytecode.Instruction res = new(ctx.BytecodeLength)
        {
            Kind = opcode
        };
        ctx.Instructions.Add(res);
        ctx.BytecodeLength += 4;
        return res;
    }

    /// <summary>
    /// Emits a basic single-type instruction.
    /// </summary>
    private static GMCode.Bytecode.Instruction Emit(CodeContext ctx, GMCode.Bytecode.Instruction.Opcode opcode, GMCode.Bytecode.Instruction.DataType type)
    {
        GMCode.Bytecode.Instruction res = new(ctx.BytecodeLength)
        {
            Kind = opcode,
            Type1 = type
        };
        ctx.Instructions.Add(res);
        ctx.BytecodeLength += res.GetLength() * 4;
        return res;
    }

    /// <summary>
    /// Emits a basic double-type instruction.
    /// </summary>
    private static GMCode.Bytecode.Instruction Emit(CodeContext ctx, GMCode.Bytecode.Instruction.Opcode opcode, GMCode.Bytecode.Instruction.DataType type, GMCode.Bytecode.Instruction.DataType type2)
    {
        GMCode.Bytecode.Instruction res = new(ctx.BytecodeLength)
        {
            Kind = opcode,
            Type1 = type,
            Type2 = type2
        };
        ctx.Instructions.Add(res);
        ctx.BytecodeLength += res.GetLength() * 4;
        return res;
    }

    /// <summary>
    /// Emits a basic comparison instruction.
    /// </summary>
    private static GMCode.Bytecode.Instruction EmitCompare(CodeContext ctx, GMCode.Bytecode.Instruction.ComparisonType type, GMCode.Bytecode.Instruction.DataType type1, GMCode.Bytecode.Instruction.DataType type2)
    {
        GMCode.Bytecode.Instruction res = new(ctx.BytecodeLength)
        {
            Kind = GMCode.Bytecode.Instruction.Opcode.Cmp,
            ComparisonKind = type,
            Type1 = type1,
            Type2 = type2
        };
        ctx.Instructions.Add(res);
        ctx.BytecodeLength += 4;
        return res;
    }

    /// <summary>
    /// Emits a duplication instruction, with its parameter and type.
    /// </summary>
    private static GMCode.Bytecode.Instruction EmitDup(CodeContext ctx, GMCode.Bytecode.Instruction.DataType type, byte param1)
    {
        GMCode.Bytecode.Instruction res = new(ctx.BytecodeLength)
        {
            Kind = GMCode.Bytecode.Instruction.Opcode.Dup,
            Type1 = type,
            Extra = param1,
            ComparisonKind = 0
        };
        ctx.Instructions.Add(res);
        ctx.BytecodeLength += 4;
        return res;
    }

    /// <summary>
    /// Emits a special "dup swap" instruction, with its parameters and type.
    /// </summary>
    private static GMCode.Bytecode.Instruction EmitDupSwap(CodeContext ctx, GMCode.Bytecode.Instruction.DataType type, byte param1, byte param2)
    {
        GMCode.Bytecode.Instruction res = new(ctx.BytecodeLength)
        {
            Kind = GMCode.Bytecode.Instruction.Opcode.Dup,
            Type1 = type,
            Extra = param1,
            ComparisonKind = (GMCode.Bytecode.Instruction.ComparisonType)((param2 << 3) | 0x80)
        };
        ctx.Instructions.Add(res);
        ctx.BytecodeLength += 4;
        return res;
    }

    /// <summary>
    /// Emits an instruction that references a variable, with data type.
    /// </summary>
    private static GMCode.Bytecode.Instruction EmitVariable(CodeContext ctx, GMCode.Bytecode.Instruction.Opcode opcode, GMCode.Bytecode.Instruction.DataType type, TokenVariable variable, GMCode.Bytecode.Instruction.DataType type2 = GMCode.Bytecode.Instruction.DataType.Double)
    {
        GMCode.Bytecode.Instruction res = new(ctx.BytecodeLength)
        {
            Kind = opcode,
            Type1 = type,
            Type2 = type2
        };

        ctx.VariablePatches.Add(new(res, variable));

        ctx.Instructions.Add(res);
        ctx.BytecodeLength += 8;
        return res;
    }

    /// <summary>
    /// Emits an instruction that references a function, with data type, argument count, and optional reference.
    /// </summary>
    private static GMCode.Bytecode.Instruction EmitCall(CodeContext ctx, GMCode.Bytecode.Instruction.Opcode opcode, GMCode.Bytecode.Instruction.DataType type, TokenFunction function, int argCount, Token token = null, FunctionReference reference = null)
    {
        GMCode.Bytecode.Instruction res = new(ctx.BytecodeLength)
        {
            Kind = opcode,
            Type1 = type,
            Value = (short)argCount
        };

        if (function.Builtin != null)
        {
            if (function.Builtin.ArgumentCount != -1 && argCount != function.Builtin.ArgumentCount)
                ctx.Error($"Built-in function \"{function.Builtin.Name}\" expects {function.Builtin.ArgumentCount} arguments; {argCount} supplied.", token?.Index ?? -1);
        }

        ctx.FunctionPatches.Add(new(res, function, reference));

        ctx.Instructions.Add(res);
        ctx.BytecodeLength += 8;
        return res;
    }

    /// <summary>
    /// Emits a break instruction of a given type.
    /// </summary>
    private static GMCode.Bytecode.Instruction EmitBreak(CodeContext ctx, GMCode.Bytecode.Instruction.BreakType type)
    {
        GMCode.Bytecode.Instruction res = new(ctx.BytecodeLength)
        {
            Kind = GMCode.Bytecode.Instruction.Opcode.Break,
            Type1 = GMCode.Bytecode.Instruction.DataType.Int16,
            Value = (ushort)type
        };
        ctx.Instructions.Add(res);
        ctx.BytecodeLength += 4;
        return res;
    }

    /// <summary>
    /// Emits an integer push instruction referencing a function. (Used in 2.3+ GML only)
    /// </summary>
    private static GMCode.Bytecode.Instruction EmitPushFunc(CodeContext ctx, TokenFunction function, FunctionReference reference)
    {
        GMCode.Bytecode.Instruction res = new(ctx.BytecodeLength)
        {
            Kind = GMCode.Bytecode.Instruction.Opcode.Push,
            Type1 = GMCode.Bytecode.Instruction.DataType.Int32,
            Value = null
        };

        ctx.FunctionPatches.Add(new(res, function, reference));

        ctx.Instructions.Add(res);
        ctx.BytecodeLength += 8;
        return res;
    }

    /// <summary>
    /// Emits an instruction that references a string, with data type.
    /// </summary>
    private static GMCode.Bytecode.Instruction EmitString(CodeContext ctx, GMCode.Bytecode.Instruction.Opcode opcode, GMCode.Bytecode.Instruction.DataType type, string str)
    {
        GMCode.Bytecode.Instruction res = new(ctx.BytecodeLength)
        {
            Kind = opcode,
            Type1 = type
        };

        ctx.StringPatches.Add(new(res, str));

        ctx.Instructions.Add(res);
        ctx.BytecodeLength += 8;
        return res;
    }

    /// <summary>
    /// If the top of the type stack isn't the supplied type, emits an instruction to convert to that type.
    /// This removes the type from the top of the type stack in the process.
    /// </summary>
    /// <returns>True if a conversion instruction was emitted, false otherwise</returns>
    private static bool ConvertTo(CodeContext ctx, GMCode.Bytecode.Instruction.DataType to)
    {
        GMCode.Bytecode.Instruction.DataType from = ctx.TypeStack.Pop();
        if (from != to)
        {
            Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Conv, from, to);
            return true;
        }
        return false;
    }

    /// <summary>
    /// If the top of the stack isn't an instance ID type, this emits instructions necessary to
    /// (theroetically) convert it to an instance later on. Depends on the GML version.
    /// 
    /// Will always pop from the type stack once.
    /// </summary>
    /// <returns>2 if used magic -9, 1 if used normal int32 conversion, 0 if no conversion necessary</returns>
    private static int ConvertToInstance(CodeContext ctx)
    {
        if (ctx.BaseContext.IsGMS23 && ctx.TypeStack.Peek() == GMCode.Bytecode.Instruction.DataType.Variable)
        {
            ctx.TypeStack.Pop();

            // Use magic -9 to reference stacktop instance
            Emit(ctx, GMCode.Bytecode.Instruction.Opcode.PushI, GMCode.Bytecode.Instruction.DataType.Int16).Value = (short)-9;

            return 2;
        }

        // Otherwise, if not an instance ID, need to convert to one
        return ConvertTo(ctx, GMCode.Bytecode.Instruction.DataType.Int32) ? 1 : 0;
    }

    /// <summary>
    /// Processes a TokenVariable class to have proper instance type and potentially argument name.
    /// Primarily just for 2.3+ GML.
    /// </summary>
    private static void ProcessTokenVariable(CodeContext ctx, ref TokenVariable tokenVar)
    {
        if (tokenVar.ExplicitInstType)
            return;

        if (tokenVar.InstanceType == (int)GMCode.Bytecode.Instruction.InstanceType.Undefined)
        {
            if (ctx.LocalVars.Contains(tokenVar.Name))
            {
                tokenVar.InstanceType = (int)GMCode.Bytecode.Instruction.InstanceType.Local;
                return;
            }
        }

        if (ctx.BaseContext.IsGMS23)
        {
            // Check for builtin variable
            if (tokenVar.Builtin != null)
            {
                if (tokenVar.Builtin.IsGlobal)
                    tokenVar.InstanceType = (int)GMCode.Bytecode.Instruction.InstanceType.Builtin;
                else
                    tokenVar.InstanceType = (int)GMCode.Bytecode.Instruction.InstanceType.Self;
            }

            // Check for static variable
            if (ctx.StaticVars.Contains(tokenVar.Name))
            {
                tokenVar.InstanceType = (int)GMCode.Bytecode.Instruction.InstanceType.Static;
            }

            if (ctx.BaseContext.Builtins.Arguments.Contains(tokenVar.Name))
            {
                // This is already an argument
                tokenVar.InstanceType = (int)GMCode.Bytecode.Instruction.InstanceType.Argument;
            }
            else
            {
                // Check for argument
                int argIndex = ctx.ArgumentVars.IndexOf(tokenVar.Name);
                if (argIndex != -1)
                {
                    tokenVar = new($"argument{argIndex}", null);
                    tokenVar.InstanceType = (int)GMCode.Bytecode.Instruction.InstanceType.Argument;
                }
            }
        }

        if (tokenVar.InstanceType == (int)GMCode.Bytecode.Instruction.InstanceType.Undefined)
        {
            // Default to self
            tokenVar.InstanceType = (int)GMCode.Bytecode.Instruction.InstanceType.Self;
        }
    }
}
