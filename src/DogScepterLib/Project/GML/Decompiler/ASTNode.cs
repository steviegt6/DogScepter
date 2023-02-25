﻿using DogScepterLib.Core.Models;
using DogScepterLib.Project.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using static DogScepterLib.Core.Models.GMCode.Bytecode;

/// This file contains the definitions of all AST nodes for GML output.
/// It also contains the necessary code to write all of them to a string, recursively.

namespace DogScepterLib.Project.GML.Decompiler;

public interface ASTNode
{
    public enum StatementKind
    {
        None,

        Block,

        Int16,
        Int32,
        Int64,
        Float,
        Double,
        String,
        Boolean,
        Variable,

        TypeInst,
        Asset,
        FunctionRef,
        Instance,

        Binary,
        Unary,

        Function,
        FunctionVar,
        Assign,

        Break,
        Continue,
        Exit,
        Return,

        IfStatement,
        ShortCircuit,
        WhileLoop,
        ForLoop,
        DoUntilLoop,
        RepeatLoop,
        WithLoop,
        SwitchStatement,
        SwitchCase,
        SwitchDefault,

        TryStatement,
        Exception,

        FunctionDecl,
        Struct,
        New,
    }

    public StatementKind Kind { get; set; }
    public bool NeedsSemicolon { get; set; }
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public List<ASTNode> Children { get; set; }
    public Instruction.DataType DataType { get; set; }
    public void Write(DecompileContext ctx, StringBuilder sb);
    public ASTNode Clean(DecompileContext ctx);

    public static void Newline(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append('\n');
        sb.Append(ctx.Indentation);
    }

    public static string WriteFromContext(DecompileContext ctx)
    {
        StringBuilder sb = new StringBuilder();
        WriteFromContext(ctx, sb);
        return sb.ToString();
    }

    public static void WriteFromContext(DecompileContext ctx, StringBuilder sb)
    {
        if (ctx.RemainingLocals.Count != 0)
        {
            sb.Append("var ");
            string[] locals = ctx.RemainingLocals.ToArray();
            for (int i = 0; i < locals.Length - 1; i++)
            {
                sb.Append(locals[i]);
                sb.Append(", ");
            }
            sb.Append(locals[^1]);
            sb.Append(';');
            Newline(ctx, sb);
        }

        using var enumerator = ctx.BaseASTBlock.Children.GetEnumerator();
        bool last = !enumerator.MoveNext();
        while (!last)
        {
            var curr = enumerator.Current;
            curr.Write(ctx, sb);
            last = !enumerator.MoveNext();
            if (!last)
            {
                if (ctx.StructArguments != null)
                    sb.Append(',');
                else if (curr.NeedsSemicolon)
                    sb.Append(';');
                Newline(ctx, sb);
            }
            else if (curr.NeedsSemicolon && ctx.StructArguments == null)
                sb.Append(';');
        }
    }

    public static int GetStackLength(ASTNode node)
    {
        if (node.DataType != Instruction.DataType.Unset)
            return Instruction.GetDataTypeStackLength(node.DataType);
        switch (node.Kind)
        {
            case StatementKind.Int16:
            case StatementKind.Int32:
            case StatementKind.Boolean:
            case StatementKind.Float:
                return 4;
            case StatementKind.Int64:
            case StatementKind.Double:
                return 8;
            case StatementKind.Binary:
                return Instruction.GetDataTypeStackLength((node as ASTBinary).Instruction.Type2);
        }
        return 16;
    }
}

public class ASTBlock : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.Block;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; } = new List<ASTNode>();

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append('{');
        ctx.IndentationLevel++;
        foreach (var child in Children)
        {
            ASTNode.Newline(ctx, sb);
            child.Write(ctx, sb);
            if (child.NeedsSemicolon)
                sb.Append(';');
        }
        ctx.IndentationLevel--;
        ASTNode.Newline(ctx, sb);
        sb.Append('}');
    }

    public static void BlockCleanup(DecompileContext ctx, List<ASTNode> nodes)
    {
        if (nodes.Count != 0)
        {
            nodes[0] = nodes[0].Clean(ctx);
            for (int i = 1; i < nodes.Count; i++)
            {
                nodes[i] = nodes[i].Clean(ctx);
                if (nodes[i - 1].Kind == ASTNode.StatementKind.Assign)
                {
                    // Check for while/for loop conversions
                    if (nodes[i].Kind == ASTNode.StatementKind.WhileLoop)
                    {
                        ASTWhileLoop loop = nodes[i] as ASTWhileLoop;
                        if (!loop.ContinueUsed && loop.Children[0].Kind == ASTNode.StatementKind.Block)
                        {
                            ASTBlock block = loop.Children[0] as ASTBlock;
                            if (block.Children.Count >= 1 && block.Children[^1].Kind == ASTNode.StatementKind.Assign)
                            {
                                // This while loop can be cleanly turned into a for loop, so do it!
                                ASTForLoop newLoop = new ASTForLoop();
                                newLoop.HasInitializer = true;
                                newLoop.Children.Add(block.Children[^1]);
                                newLoop.Children.Add(block);
                                block.Children.RemoveAt(block.Children.Count - 1);
                                newLoop.Children.Add(loop.Children[1]);
                                newLoop.Children.Add(nodes[i - 1].Clean(ctx));
                                nodes[i - 1] = newLoop.Clean(ctx);
                                nodes.RemoveAt(i--);
                            }
                        }
                    }
                    else if (nodes[i].Kind == ASTNode.StatementKind.ForLoop)
                    {
                        // This for loop should have the intialization added to it
                        ASTForLoop loop = nodes[i] as ASTForLoop;
                        loop.HasInitializer = true;
                        loop.Children.Add(nodes[i - 1].Clean(ctx));
                        nodes.RemoveAt(--i);
                        loop.Clean(ctx);
                    }
                    else
                    {
                        // Check for $$$$temp$$$$
                        ASTAssign assign = nodes[i - 1] as ASTAssign;
                        if (assign.Children[0].Kind == ASTNode.StatementKind.Variable &&
                            (assign.Children[0] as ASTVariable).Variable.Name?.Content == "$$$$temp$$$$")
                        {
                            if (nodes[i].Kind == ASTNode.StatementKind.Return)
                            {
                                ASTReturn ret = nodes[i] as ASTReturn;
                                if (ret.Children[0].Kind == ASTNode.StatementKind.Variable && 
                                    (ret.Children[0] as ASTVariable).Variable.Name?.Content == "$$$$temp$$$$")
                                {
                                    // Change   $$$$temp$$$$ = <value>; return $$$$temp$$$$;
                                    // into     return <value>;
                                    nodes[i - 1] = new ASTReturn(assign.Children[1]);
                                    nodes.RemoveAt(i--);
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        if (Children.Count == 1)
        {
            switch (Children[0].Kind)
            {
                case ASTNode.StatementKind.IfStatement:
                case ASTNode.StatementKind.SwitchStatement:
                case ASTNode.StatementKind.WhileLoop:
                case ASTNode.StatementKind.ForLoop:
                case ASTNode.StatementKind.DoUntilLoop:
                case ASTNode.StatementKind.RepeatLoop:
                case ASTNode.StatementKind.WithLoop:
                case ASTNode.StatementKind.TryStatement:
                    // Don't get rid of curly brackets for these
                    Children[0] = Children[0].Clean(ctx);
                    break;
                default:
                    Children[0] = Children[0].Clean(ctx);
                    return Children[0];
            }
        }
        else
            BlockCleanup(ctx, Children);
        return this;
    }
}

public class ASTBreak : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.Break;
    public bool NeedsSemicolon { get; set; } = true;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }
    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append("break");
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        return this;
    }
}

public class ASTContinue : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.Continue;
    public bool NeedsSemicolon { get; set; } = true;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }
    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append("continue");
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        return this;
    }
}

public class ASTInt16 : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.Int16;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }

    public short Value;
    public Context PotentialContext;

    public enum Context
    {
        None,
        Postfix,
        Prefix
    }

    public ASTInt16(short value, Context potentialContext)
    {
        Value = value;
        PotentialContext = potentialContext;
    }

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append(Value);
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        return this;
    }

    // Used for JSON comparisons
    public override string ToString()
    {
        return Value.ToString();
    }
}

public class ASTInt32 : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.Int32;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }

    public int Value;
    public ASTInt32(int value) => Value = value;

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append(Value);
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        return this;
    }

    // Used for JSON comparisons
    public override string ToString()
    {
        return Value.ToString();
    }
}

public class ASTInt64 : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.Int64;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }

    public long Value;
    public ASTInt64(long value) => Value = value;

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append(Value);
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        return this;
    }

    // Used for JSON comparisons
    public override string ToString()
    {
        return Value.ToString();
    }
}

public class ASTFloat : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.Float;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }

    public float Value;
    public ASTFloat(float value) => Value = value;

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append(Value.ToString("R", CultureInfo.InvariantCulture));
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        return this;
    }

    // Used for JSON comparisons
    public override string ToString()
    {
        return Value.ToString("R", CultureInfo.InvariantCulture);
    }
}

public class ASTDouble : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.Double;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }

    public double Value;
    public ASTDouble(double value) => Value = value;

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append(RoundTripDouble.ToRoundTrip(Value));
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        return this;
    }

    // Used for JSON comparisons
    public override string ToString()
    {
        return RoundTripDouble.ToRoundTrip(Value);
    }
}

public class ASTString : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.String;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }

    public string Value;
    public ASTString(string value) => Value = value;

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        string val = Value;

        if (ctx.Data.VersionInfo.IsVersionAtLeast(2))
            val = "\"" + val.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\"", "\\\"") + "\"";
        else
        {
            // Handle GM:S 1's lack of escaping
            bool front, back;
            if (val.StartsWith('"'))
            {
                front = true;
                val = val.Remove(0, 1);
                if (val.Length == 0)
                    val = "'\"'";
            }
            else
                front = false;
            if (val.EndsWith('"'))
            {
                val = val.Remove(val.Length - 1);
                back = true;
            }
            else
                back = false;
            val = val.Replace("\"", "\" + '\"' + \"");
            if (front)
                val = "'\"' + \"" + val;
            else
                val = "\"" + val;
            if (back)
                val += "\" + '\"'";
            else
                val += "\"";
        }
        sb.Append(val);
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        return this;
    }
        
    // Used for JSON comparisons
    public override string ToString()
    {
        return Value;
    }
}

public class ASTBoolean : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.Boolean;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }

    public bool Value;
    public ASTBoolean(bool value) => Value = value;

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append(Value ? "true" : "false");
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        return this;
    }
        
    // Used for JSON comparisons
    public override string ToString()
    {
        return Value ? "true" : "false";
    }
}

public class ASTUnary : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.Unary;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }

    public Instruction Instruction;
    public ASTUnary(Instruction inst, ASTNode node)
    {
        Instruction = inst;
        Children = new() { node };
    }

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        if (NeedsParentheses)
            sb.Append('(');
        switch (Instruction.Kind)
        {
            case Instruction.Opcode.Neg:
                sb.Append('-');
                break;
            case Instruction.Opcode.Not:
                if (Instruction.Type1 == Instruction.DataType.Boolean)
                    sb.Append('!');
                else
                    sb.Append('~');
                break;
        }
        Children[0].Write(ctx, sb);
        if (NeedsParentheses)
            sb.Append(')');
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        Children[0] = Children[0].Clean(ctx);
        if (Children[0].Kind == ASTNode.StatementKind.Binary || Children[0].Kind == ASTNode.StatementKind.ShortCircuit)
            Children[0].NeedsParentheses = true;
        return this;
    }
}

public class ASTBinary : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.Binary;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }

    public Instruction Instruction;
    public bool Chained = false;
    public ASTBinary(Instruction inst, ASTNode left, ASTNode right)
    {
        Instruction = inst;
        Children = new() { left, right };
    }

    public static bool IsTypeTheSame(ASTBinary a, ASTBinary b)
    {
        if (a.Instruction.Kind != b.Instruction.Kind)
            return false;
        if (a.Instruction.ComparisonKind != b.Instruction.ComparisonKind)
            return false;
        return true;
    }

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        if (NeedsParentheses)
            sb.Append('(');

        Children[0].Write(ctx, sb);

        string op = null;
        switch (Instruction.Kind)
        {
            case Instruction.Opcode.Mul: op = " * "; break;
            case Instruction.Opcode.Div: op = " / "; break;
            case Instruction.Opcode.Rem: op = " div "; break;
            case Instruction.Opcode.Mod: op = " % "; break;
            case Instruction.Opcode.Add: op = " + "; break;
            case Instruction.Opcode.Sub: op = " - "; break;
            case Instruction.Opcode.And:
                if (Instruction.Type1 == Instruction.DataType.Boolean &&
                    Instruction.Type2 == Instruction.DataType.Boolean)
                    op = " && "; // Non-short-circuit
                else
                    op = " & ";
                break;
            case Instruction.Opcode.Or:
                if (Instruction.Type1 == Instruction.DataType.Boolean &&
                    Instruction.Type2 == Instruction.DataType.Boolean)
                    op = " || "; // Non-short-circuit
                else
                    op = " | ";
                break;
            case Instruction.Opcode.Xor:
                if (Instruction.Type1 == Instruction.DataType.Boolean &&
                    Instruction.Type2 == Instruction.DataType.Boolean)
                    op = " ^^ ";
                else
                    op = " ^ ";
                break;
            case Instruction.Opcode.Shl: op = " << "; break;
            case Instruction.Opcode.Shr: op = " >> "; break;
            case Instruction.Opcode.Cmp:
                op = Instruction.ComparisonKind switch
                {
                    Instruction.ComparisonType.LT => " < ",
                    Instruction.ComparisonType.LTE => " <= ",
                    Instruction.ComparisonType.EQ => " == ",
                    Instruction.ComparisonType.NEQ => " != ",
                    Instruction.ComparisonType.GTE => " >= ",
                    Instruction.ComparisonType.GT => " > ",
                    _ => null
                };
                break;
        }

        sb.Append(op);

        Children[1].Write(ctx, sb);

        if (NeedsParentheses)
            sb.Append(')');
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        for (int i = 0; i < Children.Count; i++)
        {
            Children[i] = Children[i].Clean(ctx);
            if (Children[i].Kind == ASTNode.StatementKind.Binary &&
                    (!IsTypeTheSame(this, Children[i] as ASTBinary) || !(Children[i] as ASTBinary).Chained))
            {
                Children[i].NeedsParentheses = true;
            }
            else if (Children[i].Kind == ASTNode.StatementKind.ShortCircuit)
                Children[i].NeedsParentheses = true;
        }
        MacroResolver.ResolveBinary(ctx, this);
        return this;
    }
}

public class ASTFunction : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.Function;
    public bool NeedsSemicolon { get; set; } = true;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Variable;
    public List<ASTNode> Children { get; set; }

    public GMFunctionEntry Function;
    public ASTFunction(GMFunctionEntry function, List<ASTNode> args)
    {
        Function = function;
        Children = args;
    }

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        bool arrayLiteral = Function.Name.Content == "@@NewGMLArray@@";

        if (arrayLiteral)
            sb.Append('[');
        else
        {
            if (ctx.Cache.GlobalFunctionNames.TryGetValue(Function, out string name))
                sb.Append(name);
            else
            {
                string funcName = Function.Name.Content;

                // Search for functions defined inside this code entry
                foreach (var subCtx in ctx.SubContexts)
                {
                    if (funcName == subCtx.CodeName && subCtx.FunctionName != null)
                    {
                        funcName = subCtx.FunctionName;
                        break;
                    }
                }

                sb.Append(funcName);
            }
            sb.Append('(');
        }

        if (Children.Count >= 1)
            Children[0].Write(ctx, sb);
        for (int i = 1; i < Children.Count; i++)
        {
            sb.Append(", ");
            Children[i].Write(ctx, sb);
        }

        if (arrayLiteral)
            sb.Append(']');
        else
            sb.Append(')');
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        for (int i = 0; i < Children.Count; i++)
            Children[i] = Children[i].Clean(ctx);

        // Deal with 2.3 instance functions
        switch (Function.Name.Content)
        {
            case "@@This@@":
                return new ASTInstance(ASTInstance.InstanceType.Self) { Duplicated = Duplicated };
            case "@@Other@@":
                return new ASTInstance(ASTInstance.InstanceType.Other) { Duplicated = Duplicated };
            case "@@Global@@":
                return new ASTInstance(ASTInstance.InstanceType.Global) { Duplicated = Duplicated };
            case "@@GetInstance@@":
                {
                    ASTNode res = MacroResolver.ResolveObject(ctx, Children[0] as ASTInt16);
                    res.Duplicated = Duplicated;
                    return res;
                }
        }

        MacroResolver.ResolveFunction(ctx, this);
        return this;
    }

    public override string ToString()
    {
        return Function.Name.Content;
    }
}

public class ASTFunctionVar : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.FunctionVar;
    public bool NeedsSemicolon { get; set; } = true;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }

    public ASTFunctionVar(ASTNode instance, ASTNode function, List<ASTNode> args)
    {
        Children = args;
        Children.Insert(0, function);
        Children.Insert(0, instance);
    }

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        if ((Children[0].Kind != ASTNode.StatementKind.Instance ||
            (Children[0] as ASTInstance).InstanceKind != ASTInstance.InstanceType.Self) &&
            !Children[0].Duplicated)
        {
            Children[0].Write(ctx, sb);
            sb.Append('.');
        }
        Children[1].Write(ctx, sb);
        sb.Append('(');

        if (Children.Count >= 3)
            Children[2].Write(ctx, sb);
        for (int i = 3; i < Children.Count; i++)
        {
            sb.Append(", ");
            Children[i].Write(ctx, sb);
        }
            
        sb.Append(')');
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        for (int i = 0; i < Children.Count; i++)
            Children[i] = Children[i].Clean(ctx);
        return this;
    }
}

public class ASTExit : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.Exit;
    public bool NeedsSemicolon { get; set; } = true;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }
    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append("exit");
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        return this;
    }
}

public class ASTReturn : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.Return;
    public bool NeedsSemicolon { get; set; } = true;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }

    public ASTReturn(ASTNode arg) => Children = new() { arg };

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append("return ");
        Children[0].Write(ctx, sb);
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        Children[0] = Children[0].Clean(ctx);

        MacroResolver.ResolveReturn(ctx, this);

        return this;
    }
}

public class ASTVariable : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.Variable;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }

    public GMVariable Variable;
    public ASTNode Left;
    public Instruction.VariableType VarType;
    public Instruction.Opcode Opcode;

    public ASTVariable(GMVariable var, Instruction.VariableType varType, Instruction.Opcode opcode)
    {
        Variable = var;
        VarType = varType;
        Opcode = opcode;
    }

    public bool IsSameAs(ASTVariable other)
    {
        if (Variable != other.Variable)
            return false;
        if (VarType != other.VarType)
            return false;
        if (Left.Kind != other.Left.Kind)
            return false;
        if (Left.Kind == ASTNode.StatementKind.Variable)
        {
            if (!(Left as ASTVariable).IsSameAs(other.Left as ASTVariable))
                return false;
        }
        else if (Left.Kind == ASTNode.StatementKind.TypeInst)
        {
            if ((Left as ASTTypeInst).Value != (other.Left as ASTTypeInst).Value)
                return false;
        }
        else if (Left != other.Left)
            return false;
        if (Children != null)
        {
            if (Children.Count != other.Children.Count)
                return false;
            for (int i = 0; i < Children.Count; i++)
                if (Children[i] != other.Children[i])
                    return false;
        }
        return true;
    }

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        if (Left.Kind == ASTNode.StatementKind.Int16 ||
            Left.Kind == ASTNode.StatementKind.TypeInst)
        {
            int value;
            if (Left.Kind == ASTNode.StatementKind.Int16)
                value = (Left as ASTInt16).Value;
            else
                value = (Left as ASTTypeInst).Value;

            if (value < 0)
            {
                // Builtin constants
                switch (value)
                {
                    case -1:
                        if (ctx.AllLocals.Contains(Variable.Name.Content))
                            sb.Append("self.");
                        break;
                    case -5:
                        sb.Append("global.");
                        break;
                    case -2:
                        sb.Append("other.");
                        break;
                    case -3:
                        sb.Append("all.");
                        break;
                    case -15:
                        // Arguments: check if this is a struct argument
                        if (ctx.StructArguments != null &&
                            Variable.Name.Content == "argument" && 
                            Children != null && Children.Count == 1 && 
                            Children[0].Kind == ASTNode.StatementKind.Int16)
                        {
                            short index = (Children[0] as ASTInt16).Value;
                            if (index < ctx.StructArguments.Count)
                            {
                                ctx.StructArguments[index].Write(ctx.ParentContext /* maybe not? */, sb);
                                return;
                            }
                        }
                        break;
                    case -16:
                        sb.Append("static ");
                        break;
                }
            }
            else if (Left.Kind == ASTNode.StatementKind.TypeInst && (Left as ASTTypeInst).IsRoomInstance)
            {
                // This is a room instance ID; need to convert it here
                sb.Append('(');
                sb.Append(value + 100000);
                sb.Append(").");
            }
            else if (value < ctx.Project.Objects.Count)
            {
                // Object names
                sb.Append(ctx.Project.Objects[value].Name);
                sb.Append('.');
            }
            else
            {
                // Unknown number
                sb.Append('(');
                sb.Append(value);
                sb.Append(").");
            }
        }
        else if (Left.Kind == ASTNode.StatementKind.Variable ||
                    Left.Kind == ASTNode.StatementKind.Asset ||
                    Left.Kind == ASTNode.StatementKind.Instance ||
                    Left.Kind == ASTNode.StatementKind.Function ||
                    Left.Kind == ASTNode.StatementKind.FunctionVar)
        {
            // Variable expression, asset/instance type, or function call
            Left.Write(ctx, sb);
            sb.Append('.');
        }
        else
        {
            // Unknown expression
            sb.Append('(');
            Left.Write(ctx, sb);
            sb.Append(").");
        }

        // The actual variable name
        sb.Append(Variable.Name.Content);

        // Handle arrays
        if (ctx.Data.VersionInfo.IsVersionAtLeast(2, 3))
        {
            // ... for GMS2.3
            if (Children != null)
            {
                foreach (var c in Children)
                {
                    sb.Append('[');
                    c.Write(ctx, sb);
                    sb.Append(']');
                }
            }
        }
        else if (Children != null)
        {
            // ... for pre-GMS2.3
            sb.Append('[');
            Children[0].Write(ctx, sb);
            if (Children.Count == 2)
            {
                sb.Append(", ");
                Children[1].Write(ctx, sb);
            }
            sb.Append(']');
        }
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        Left = Left.Clean(ctx);
        for (int i = 0; i < Children?.Count; i++)
            Children[i] = Children[i].Clean(ctx);
        return this;
    }

    // Used for macro type detection
    public override string ToString()
    {
        return Variable.Name.Content;
    }
}

public class ASTTypeInst : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.TypeInst;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }

    public int Value;
    public bool IsRoomInstance;

    public ASTTypeInst(int value, bool isRoomInstance)
    {
        Value = value;
        IsRoomInstance = isRoomInstance;
    }

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        // Doesn't really do anything on its own here
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        return this;
    }
}

public class ASTAssign : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.Assign;
    public bool NeedsSemicolon { get; set; } = true;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }
    public Instruction Compound;
    public CompoundType CompoundKind = CompoundType.None;
    public ModifierType ModifierKind = ModifierType.None;
    public enum CompoundType
    {
        None,
        Normal,
        Prefix,
        Postfix
    }
    public enum ModifierType
    {
        None,
        Local
    }

    public ASTAssign(ASTVariable var, ASTNode node, Instruction compound = null)
    {
        Children = new() { var, node };
        Compound = compound;
        CompoundKind = (compound == null) ? CompoundType.None : CompoundType.Normal;
    }
    public ASTAssign(Instruction inst, ASTNode variable, bool isPrefix)
    {
        Compound = inst;
        Children = new() { variable };
        CompoundKind = isPrefix ? CompoundType.Prefix : CompoundType.Postfix;
    }

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        switch (CompoundKind)
        {
            case CompoundType.Normal:
                Children[0].Write(ctx, sb);
                sb.Append(' ');
                switch (Compound.Kind)
                {
                    case Instruction.Opcode.Add: sb.Append('+'); break;
                    case Instruction.Opcode.Sub: sb.Append('-'); break;
                    case Instruction.Opcode.Mul: sb.Append('*'); break;
                    case Instruction.Opcode.Div: sb.Append('/'); break;
                    case Instruction.Opcode.Mod: sb.Append('%'); break;
                    case Instruction.Opcode.And: sb.Append('&'); break;
                    case Instruction.Opcode.Or: sb.Append('|'); break;
                    case Instruction.Opcode.Xor: sb.Append('^'); break;
                }
                sb.Append("= ");
                Children[1].Write(ctx, sb);
                break;
            case CompoundType.Prefix:
                if (Compound.Kind == Instruction.Opcode.Add)
                    sb.Append("++");
                else
                    sb.Append("--");
                Children[0].Write(ctx, sb);
                break;
            case CompoundType.Postfix:
                Children[0].Write(ctx, sb);
                if (Compound.Kind == Instruction.Opcode.Add)
                    sb.Append("++");
                else
                    sb.Append("--");
                break;
            default:
                if (ctx.StructArguments != null)
                {
                    Children[0].Write(ctx, sb);
                    sb.Append(": ");
                    Children[1].Write(ctx, sb);
                }
                else
                {
                    if (ModifierKind == ModifierType.Local)
                        sb.Append("var ");
                    Children[0].Write(ctx, sb);
                    sb.Append(" = ");
                    Children[1].Write(ctx, sb);
                }
                break;
        }
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        for (int i = 0; i < Children.Count; i++)
            Children[i] = Children[i].Clean(ctx);

        // Check for postfix and compounds
        if (CompoundKind == CompoundType.None &&
            Children[0].Kind == ASTNode.StatementKind.Variable && Children[1].Kind == ASTNode.StatementKind.Binary)
        {
            ASTBinary bin = Children[1] as ASTBinary;
            if (bin.Children[0].Kind == ASTNode.StatementKind.Variable)
            {
                ASTVariable var = bin.Children[0] as ASTVariable;
                if (var.IsSameAs(Children[0] as ASTVariable))
                {
                    // This is one of the two
                    if (bin.Children[1].Kind == ASTNode.StatementKind.Int16)
                    {
                        ASTInt16 i16 = bin.Children[1] as ASTInt16;
                        if (i16.Value == 1)
                        {
                            if (i16.PotentialContext == ASTInt16.Context.Postfix)
                            {
                                CompoundKind = CompoundType.Postfix;
                                Compound = bin.Instruction;
                                return this;
                            }
                            if (i16.PotentialContext == ASTInt16.Context.Prefix)
                            {
                                CompoundKind = CompoundType.Prefix;
                                Compound = bin.Instruction;
                                return this;
                            }
                        }
                    }

                    // Make sure that this isn't a false positive (uses a different instruction in bytecode)
                    if (ctx.Data.VersionInfo.FormatID < 15 || var.Opcode == Instruction.Opcode.Push || var.Variable.VariableType == Instruction.InstanceType.Self)
                    {
                        CompoundKind = CompoundType.Normal;
                        Compound = bin.Instruction;
                        Children[1] = bin.Children[1];
                        return this;
                    }
                }
            }
        }

        if (CompoundKind == CompoundType.None && Children[0].Kind == ASTNode.StatementKind.Variable)
        {
            ASTVariable variable = Children[0] as ASTVariable;
            if (variable.Variable.VariableType == Instruction.InstanceType.Local)
            {
                if (ctx.RemainingLocals.Contains(variable.Variable.Name.Content))
                {
                    ModifierKind = ModifierType.Local;
                    ctx.RemainingLocals.Remove(variable.Variable.Name.Content);
                }
            }
        }

        MacroResolver.ResolveAssign(ctx, this);

        return this;
    }
}

public class ASTIfStatement : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.IfStatement;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; } = new List<ASTNode>(3);
    public bool ElseIf { get; set; } = false;

    // Temporary ternary detection variables
    public int StackCount { get; set; }
    public ASTNode Parent { get; set; }
    public bool EmptyElse { get; set; } = false;

    public ASTIfStatement(ASTNode condition) => Children.Add(condition);

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
#if DEBUG
        if (Children.Count == 4)
            throw new Exception("Ternary logic broke");
#endif
        if (Children.Count == 5)
        {
            // This is a ternary
            if (NeedsParentheses)
                sb.Append('(');
            Children[0].Write(ctx, sb);
            sb.Append(" ? ");
            Children[3].Write(ctx, sb);
            sb.Append(" : ");
            Children[4].Write(ctx, sb);
            if (NeedsParentheses)
                sb.Append(')');
            return;
        }

        sb.Append("if (");
        Children[0].Write(ctx, sb);
        sb.Append(')');

        // Main block
        if (Children[1].Kind != ASTNode.StatementKind.Block)
        {
            ctx.IndentationLevel++;
            ASTNode.Newline(ctx, sb);
            Children[1].Write(ctx, sb);
            if (Children[1].NeedsSemicolon)
                sb.Append(';');
            ctx.IndentationLevel--;
        }
        else
        {
            ASTNode.Newline(ctx, sb);
            Children[1].Write(ctx, sb);
        }

        // Else block
        if (Children.Count >= 3)
        {
            ASTNode.Newline(ctx, sb);
            sb.Append("else");

            if (ElseIf)
            {
                sb.Append(' ');
                Children[2].Write(ctx, sb);
            }
            else if (Children[2].Kind != ASTNode.StatementKind.Block)
            {
                ctx.IndentationLevel++;
                ASTNode.Newline(ctx, sb);
                Children[2].Write(ctx, sb);
                if (Children[2].NeedsSemicolon)
                    sb.Append(';');
                ctx.IndentationLevel--;
            }
            else
            {
                ASTNode.Newline(ctx, sb);
                Children[2].Write(ctx, sb);
            }
        }
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        for (int i = 0; i < Children.Count; i++)
            Children[i] = Children[i].Clean(ctx);

        if (Children.Count == 3 && Children[2].Kind == ASTNode.StatementKind.Block &&
            Children[2].Children.Count == 1 && Children[2].Children[0].Kind == ASTNode.StatementKind.IfStatement)
        {
            // This is an else if chain, so mark this as such
            ElseIf = true;
            Children[2] = Children[2].Children[0];
        }
        else if (Children.Count == 5)
        {
            // This is a ternary. Check if parentheses are needed for operands.
            var kind = Children[0].Kind;
            if (kind == ASTNode.StatementKind.Binary || kind == ASTNode.StatementKind.ShortCircuit || kind == ASTNode.StatementKind.IfStatement)
                Children[0].NeedsParentheses = true;
            kind = Children[3].Kind;
            if (kind == ASTNode.StatementKind.Binary || kind == ASTNode.StatementKind.ShortCircuit || kind == ASTNode.StatementKind.IfStatement)
                Children[3].NeedsParentheses = true;
            kind = Children[4].Kind;
            if (kind == ASTNode.StatementKind.Binary || kind == ASTNode.StatementKind.ShortCircuit || kind == ASTNode.StatementKind.IfStatement)
                Children[4].NeedsParentheses = true;
        }

        return this;
    }
}

public class ASTShortCircuit : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.ShortCircuit;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }

    public ShortCircuit.ShortCircuitType ShortCircuitKind;
    public ASTShortCircuit(ShortCircuit.ShortCircuitType kind, List<ASTNode> conditions)
    {
        ShortCircuitKind = kind;
        Children = conditions;
    }

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        if (NeedsParentheses)
            sb.Append('(');

        Children[0].Write(ctx, sb);
        string op = (ShortCircuitKind == ShortCircuit.ShortCircuitType.And) ? " && " : " || ";
        for (int i = 1; i < Children.Count; i++)
        {
            sb.Append(op);
            Children[i].Write(ctx, sb);
        }

        if (NeedsParentheses)
            sb.Append(')');
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        for (int i = 0; i < Children.Count; i++)
        {
            Children[i] = Children[i].Clean(ctx);
            if (Children[i].Kind == ASTNode.StatementKind.ShortCircuit)
                Children[i].NeedsParentheses = true;
        }
        return this;
    }
}

public class ASTWhileLoop : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.WhileLoop;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public bool ContinueUsed { get; set; }
    public List<ASTNode> Children { get; set; } = new List<ASTNode>();
    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append("while (");
        Children[1].Write(ctx, sb);
        sb.Append(')');

        // Main block
        if (Children[0].Kind != ASTNode.StatementKind.Block)
        {
            ctx.IndentationLevel++;
            ASTNode.Newline(ctx, sb);
            Children[0].Write(ctx, sb);
            if (Children[0].NeedsSemicolon)
                sb.Append(';');
            ctx.IndentationLevel--;
        }
        else
        {
            ASTNode.Newline(ctx, sb);
            Children[0].Write(ctx, sb);
        }
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        for (int i = 0; i < Children.Count; i++)
            Children[i] = Children[i].Clean(ctx);
        return this;
    }
}

public class ASTForLoop : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.ForLoop;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public bool HasInitializer { get; set; }
    public List<ASTNode> Children { get; set; } = new List<ASTNode>();
    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append("for (");
        if (HasInitializer)
            Children[3].Write(ctx, sb);
        sb.Append("; ");
        Children[2].Write(ctx, sb);
        sb.Append("; ");
        Children[0].Write(ctx, sb);
        sb.Append(')');

        // Main block
        if (Children[1].Kind != ASTNode.StatementKind.Block)
        {
            ctx.IndentationLevel++;
            ASTNode.Newline(ctx, sb);
            Children[1].Write(ctx, sb);
            if (Children[1].NeedsSemicolon)
                sb.Append(';');
            ctx.IndentationLevel--;
        }
        else
        {
            ASTNode.Newline(ctx, sb);
            Children[1].Write(ctx, sb);
        }
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        for (int i = 0; i < Children.Count; i++)
            Children[i] = Children[i].Clean(ctx);
        return this;
    }
}

public class ASTDoUntilLoop : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.DoUntilLoop;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; } = new List<ASTNode>();
    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append("do");

        // Main block
        if (Children[0].Kind != ASTNode.StatementKind.Block)
        {
            ctx.IndentationLevel++;
            ASTNode.Newline(ctx, sb);
            Children[0].Write(ctx, sb);
            if (Children[0].NeedsSemicolon)
                sb.Append(';');
            ctx.IndentationLevel--;
        }
        else
        {
            ASTNode.Newline(ctx, sb);
            Children[0].Write(ctx, sb);
        }

        ASTNode.Newline(ctx, sb);
        sb.Append("until (");
        Children[1].Write(ctx, sb);
        sb.Append(')');
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        for (int i = 0; i < Children.Count; i++)
            Children[i] = Children[i].Clean(ctx);
        return this;
    }
}

public class ASTRepeatLoop : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.RepeatLoop;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; } = new List<ASTNode>();

    public ASTRepeatLoop(ASTNode expr) => Children.Add(expr);
    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append("repeat (");
        Children[0].Write(ctx, sb);
        sb.Append(')');

        // Main block
        if (Children[1].Kind != ASTNode.StatementKind.Block)
        {
            ctx.IndentationLevel++;
            ASTNode.Newline(ctx, sb);
            Children[1].Write(ctx, sb);
            if (Children[1].NeedsSemicolon)
                sb.Append(';');
            ctx.IndentationLevel--;
        }
        else
        {
            ASTNode.Newline(ctx, sb);
            Children[1].Write(ctx, sb);
        }
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        for (int i = 0; i < Children.Count; i++)
            Children[i] = Children[i].Clean(ctx);
        return this;
    }
}

public class ASTWithLoop : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.WithLoop;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; } = new List<ASTNode>();

    public ASTWithLoop(ASTNode expr) => Children.Add(expr);
    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append("with (");
        Children[0].Write(ctx, sb);
        sb.Append(')');

        // Main block
        if (Children[1].Kind != ASTNode.StatementKind.Block)
        {
            ctx.IndentationLevel++;
            ASTNode.Newline(ctx, sb);
            Children[1].Write(ctx, sb);
            if (Children[1].NeedsSemicolon)
                sb.Append(';');
            ctx.IndentationLevel--;
        }
        else
        {
            ASTNode.Newline(ctx, sb);
            Children[1].Write(ctx, sb);
        }
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        for (int i = 0; i < Children.Count; i++)
            Children[i] = Children[i].Clean(ctx);
        if (Children[0].Kind == ASTNode.StatementKind.Int16)
            Children[0] = MacroResolver.ResolveObject(ctx, Children[0] as ASTInt16);
        return this;
    }
}

public class ASTSwitchStatement : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.SwitchStatement;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; } = new List<ASTNode>();
    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        // Find statements before cases start
        int startCases = 1;
        for (; startCases < Children.Count; startCases++)
            if (Children[startCases].Kind == ASTNode.StatementKind.SwitchCase ||
                Children[startCases].Kind == ASTNode.StatementKind.SwitchDefault)
                break;
        for (int i = 1; i < startCases; i++)
        {
            Children[i].Write(ctx, sb);
            if (Children[i].NeedsSemicolon)
                sb.Append(';');
            ASTNode.Newline(ctx, sb);
        }

        sb.Append("switch (");
        Children[0].Write(ctx, sb);
        sb.Append(')');

        ASTNode.Newline(ctx, sb);
        sb.Append('{');
        ctx.IndentationLevel += 2;
        for (int i = startCases; i < Children.Count; i++)
        {
            var child = Children[i];
            if (child.Kind == ASTNode.StatementKind.SwitchCase ||
                child.Kind == ASTNode.StatementKind.SwitchDefault)
            {
                ctx.IndentationLevel--;
                ASTNode.Newline(ctx, sb);
                child.Write(ctx, sb);
                ctx.IndentationLevel++;
            }
            else
            {
                ASTNode.Newline(ctx, sb);
                child.Write(ctx, sb);
                if (child.NeedsSemicolon)
                    sb.Append(';');
            }
        }
        ctx.IndentationLevel -= 2;
        ASTNode.Newline(ctx, sb);
        sb.Append('}');
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        ASTBlock.BlockCleanup(ctx, Children);
        MacroResolver.ResolveSwitch(ctx, this);
        return this;
    }
}

public class ASTSwitchCase : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.SwitchCase;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }

    public ASTSwitchCase(ASTNode expr) => Children = new() { expr };

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append("case ");
        Children[0].Write(ctx, sb);
        sb.Append(':');
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        Children[0] = Children[0].Clean(ctx);
        return this;
    }
}

public class ASTSwitchDefault : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.SwitchDefault;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }
    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append("default:");
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        return this;
    }
}

public class ASTTryStatement : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.TryStatement;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; } = new List<ASTNode>();

    public bool HasCatch;
    public bool Cleaned = false;

    public ASTTryStatement(bool hasCatch)
    {
        HasCatch = hasCatch;
    }

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append("try");

        // Main block
        if (Children[0].Kind != ASTNode.StatementKind.Block)
        {
            ASTNode.Newline(ctx, sb);
            sb.Append('{');
            ctx.IndentationLevel++;
            ASTNode.Newline(ctx, sb);
            Children[0].Write(ctx, sb);
            if (Children[0].NeedsSemicolon)
                sb.Append(';');
            ctx.IndentationLevel--;
            ASTNode.Newline(ctx, sb);
            sb.Append('}');
        }
        else
        {
            ASTNode.Newline(ctx, sb);
            Children[0].Write(ctx, sb);
        }

        // Catch block
        if (HasCatch)
        {
            ASTNode.Newline(ctx, sb);
            sb.Append("catch (");
            Children[2].Write(ctx, sb);
            sb.Append(')');

            if (Children[1].Kind != ASTNode.StatementKind.Block)
            {
                ASTNode.Newline(ctx, sb);
                sb.Append('{');
                ctx.IndentationLevel++;
                ASTNode.Newline(ctx, sb);
                Children[1].Write(ctx, sb);
                if (Children[1].NeedsSemicolon)
                    sb.Append(';');
                ctx.IndentationLevel--;
                ASTNode.Newline(ctx, sb);
                sb.Append('}');
            }
            else
            {
                ASTNode.Newline(ctx, sb);
                Children[1].Write(ctx, sb);
            }
        }
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        // Rework pop in catch block
        if (!Cleaned)
        {
            if (HasCatch)
            {
#if DEBUG
                if (Children[1].Children[0].Kind != ASTNode.StatementKind.Assign)
                    throw new Exception("Expected exception pop");
#endif
                Children.Insert(2, Children[1].Children[0].Children[0]);
                Children[1].Children.RemoveAt(0);
            }
            Cleaned = true;
        }

        for (int i = 0; i < Children.Count; i++)
            Children[i] = Children[i].Clean(ctx);

        // The local variable for the exception doesn't need to get defined
        ctx.RemainingLocals.Remove((Children[2] as ASTVariable).Variable.Name.Content);

        return this;
    }
}

public class ASTAsset : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.Asset;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Int32;
    public List<ASTNode> Children { get; set; }
    public string AssetName { get; set; }

    public ASTAsset(string assetName)
    {
        AssetName = assetName;
    }

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append(AssetName);
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        return this;
    }

    public override string ToString()
    {
        return AssetName;
    }
}

public class ASTFunctionRef : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.FunctionRef;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Int32;
    public List<ASTNode> Children { get; set; }
    public GMFunctionEntry Function { get; set; }
    public string Name { get; set; }

    public ASTFunctionRef(DecompileContext ctx, GMFunctionEntry function)
    {
        Function = function;
        if (ctx.Cache.GlobalFunctionNames.TryGetValue(Function, out string name))
            Name = name;
        else
            Name = Function.Name.Content;
    }

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        // Search for functions defined inside this code entry
        foreach (var subCtx in ctx.SubContexts)
        {
            if (Name == subCtx.CodeName && subCtx.FunctionName != null)
            {
                Name = subCtx.FunctionName;
                break;
            }
        }

        sb.Append(Name);
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        return this;
    }

    public override string ToString()
    {
        return Name;
    }
}

public class ASTInstance : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.Instance;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Variable;
    public List<ASTNode> Children { get; set; }
    public InstanceType InstanceKind { get; set; }
        
    public enum InstanceType
    {
        Self,
        Other,
        Global
    }

    public ASTInstance(InstanceType kind)
    {
        InstanceKind = kind;
    }

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        switch (InstanceKind)
        {
            case InstanceType.Self:
                sb.Append("self");
                break;
            case InstanceType.Other:
                sb.Append("other");
                break;
            case InstanceType.Global:
                sb.Append("global");
                break;
        }
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        return this;
    }
}

public class ASTFunctionDecl : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.FunctionDecl;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Variable;
    public List<ASTNode> Children { get; set; }
    public string FunctionName { get; set; }
    public DecompileContext SubContext { get; set; }
    public bool IsConstructor { get; set; } = false;

    public ASTFunctionDecl(DecompileContext subContext)
    {
        SubContext = subContext;
    }

    public ASTFunctionDecl(DecompileContext subContext, string functionName)
    {
        SubContext = subContext;
        FunctionName = functionName;
        subContext.FunctionName = functionName;
    }

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append("function");
        if (FunctionName != null)
        {
            sb.Append(' ');
            sb.Append(FunctionName);
        }
        sb.Append("()"); // TODO, parameters
        if (IsConstructor)
        {
            if (SubContext.ParentCall != null)
            {
                sb.Append(" : ");
                SubContext.ParentCall.Write(SubContext, sb);
            }
            sb.Append(" constructor");
        }
        ASTNode.Newline(ctx, sb);
        sb.Append('{');
        ctx.IndentationLevel++;
        ASTNode.Newline(ctx, sb);

        SubContext.IndentationLevel = ctx.IndentationLevel;
        ASTNode.WriteFromContext(SubContext, sb);

        ctx.IndentationLevel--;
        ASTNode.Newline(ctx, sb);
        sb.Append('}');
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        return this;
    }
}

public class ASTStruct : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.Struct;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Variable;
    public List<ASTNode> Children { get; set; }
    public DecompileContext SubContext { get; set; }

    public ASTStruct(DecompileContext subContext, List<ASTNode> arguments)
    {
        SubContext = subContext;
        Children = arguments;
    }

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        ASTNode.Newline(ctx, sb);
        sb.Append('{');
        ctx.IndentationLevel++;
        ASTNode.Newline(ctx, sb);

        SubContext.IndentationLevel = ctx.IndentationLevel;
        SubContext.StructArguments = Children;
        ASTNode.WriteFromContext(SubContext, sb);

        ctx.IndentationLevel--;
        ASTNode.Newline(ctx, sb);
        sb.Append('}');
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        for (int i = 0; i < Children.Count; i++)
            Children[i] = Children[i].Clean(ctx);
        return this;
    }
}

public class ASTNew : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.New;
    public bool NeedsSemicolon { get; set; } = true;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Variable;
    public List<ASTNode> Children { get; set; }

    public ASTNew(List<ASTNode> args)
    {
        Children = args;
    }

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        sb.Append("new ");
        Children[0].Write(ctx, sb);
        sb.Append('(');

        if (Children.Count >= 2)
            Children[1].Write(ctx, sb);
        for (int i = 2; i < Children.Count; i++)
        {
            sb.Append(", ");
            Children[i].Write(ctx, sb);
        }

        sb.Append(')');
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        for (int i = 0; i < Children.Count; i++)
            Children[i] = Children[i].Clean(ctx);
        return this;
    }
}

public class ASTException : ASTNode
{
    public ASTNode.StatementKind Kind { get; set; } = ASTNode.StatementKind.Exception;
    public bool NeedsSemicolon { get; set; } = false;
    public bool Duplicated { get; set; }
    public bool NeedsParentheses { get; set; }
    public Instruction.DataType DataType { get; set; } = Instruction.DataType.Unset;
    public List<ASTNode> Children { get; set; }

    public void Write(DecompileContext ctx, StringBuilder sb)
    {
        // Nothing to do here
    }

    public ASTNode Clean(DecompileContext ctx)
    {
        // Nothing to do here
        return this;
    }
}
