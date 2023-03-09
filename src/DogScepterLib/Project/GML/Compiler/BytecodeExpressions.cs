using GameBreaker.Chunks;
using System;
using GameBreaker.Models;
using static GameBreaker.Models.GMCode.Bytecode.Instruction;

namespace GameBreaker.Project.GML.Compiler;

public static partial class Bytecode
{

    private static void CompileExpression(CodeContext ctx, Node expr)
    {
        switch (expr.Kind)
        {
            case NodeKind.Constant:
                CompileConstant(ctx, expr.Token.Value as TokenConstant);
                break;
            case NodeKind.FunctionCall:
            case NodeKind.FunctionCallChain:
                CompileFunctionCall(ctx, expr, false);
                break;
            case NodeKind.Variable:
                if (expr.Children.Count == 0)
                {
                    TokenVariable tokenVar = expr.Token.Value as TokenVariable;
                    if (ctx.BaseContext.Builtins.Functions.TryGetValue(tokenVar.Name, out BuiltinFunction builtin) ||
                        ctx.BaseContext.Functions.ContainsKey(tokenVar.Name))
                    {
                        // This is actually a function reference being pushed
                        TokenFunction tokenFunc = new TokenFunction(tokenVar.Name, builtin);
                        ctx.BaseContext.Functions.TryGetValue(tokenFunc.Name, out FunctionReference reference);
                        EmitPushFunc(ctx, tokenFunc, reference);
                        ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.Int32);
                        break;
                    }
                }

                CompileVariable(ctx, expr, false);
                break;
            case NodeKind.ChainReference:
                CompileChain(ctx, expr);
                break;
            case NodeKind.Prefix:
                CompilePrefixAndPostfix(ctx, expr, true, true);
                break;
            case NodeKind.Postfix:
                CompilePrefixAndPostfix(ctx, expr, true, false);
                break;
            case NodeKind.Conditional:
                {
                    // Condition
                    CompileExpression(ctx, expr.Children[0]);
                    ConvertTo(ctx, GMCode.Bytecode.Instruction.DataType.Boolean);
                    var falseJump = new JumpForwardPatch(Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Bf));

                    // True expression
                    CompileExpression(ctx, expr.Children[1]);
                    ConvertTo(ctx, GMCode.Bytecode.Instruction.DataType.Variable);
                    var endJump = new JumpForwardPatch(Emit(ctx, GMCode.Bytecode.Instruction.Opcode.B));

                    // False expression
                    falseJump.Finish(ctx);
                    CompileExpression(ctx, expr.Children[2]);
                    ConvertTo(ctx, GMCode.Bytecode.Instruction.DataType.Variable);

                    endJump.Finish(ctx);
                    ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.Variable);
                }
                break;
            case NodeKind.NullCoalesce:
                // TODO
                ctx.Error("Unsupported", -1);
                break;
            case NodeKind.Binary:
                CompileBinary(ctx, expr);
                break;
            case NodeKind.Unary:
                CompileUnary(ctx, expr);
                break;
            case NodeKind.Accessor:
                CompileExpression(ctx, expr.Children[0]);
                break;
            case NodeKind.FunctionDecl:
                CompileFunctionDecl(ctx, expr);
                break;
            case NodeKind.New:
                CompileNew(ctx, expr);
                break;
        }
    }

    private static void CompileConstant(CodeContext ctx, TokenConstant constant)
    {
        switch (constant.Kind)
        {
            case ConstantKind.Number:
                if (constant.IsBool && 0 <= constant.ValueNumber && constant.ValueNumber <= 1)
                {
                    // This is a proper boolean type
                    Emit(ctx, GMCode.Bytecode.Instruction.Opcode.PushI, GMCode.Bytecode.Instruction.DataType.Int16).Value = (short)constant.ValueNumber;
                    ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.Boolean);
                }
                else if ((long)constant.ValueNumber == constant.ValueNumber)
                {
                    // This is an integer type
                    long number = (long)constant.ValueNumber;
                    if (number <= int.MaxValue && number >= int.MinValue)
                    {
                        if (number <= short.MaxValue && number >= short.MinValue)
                        {
                            // 16-bit int
                            Emit(ctx, GMCode.Bytecode.Instruction.Opcode.PushI, GMCode.Bytecode.Instruction.DataType.Int16).Value = (short)number;
                            ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.Int32);
                        }
                        else
                        {
                            // 32-bit int
                            Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Push, GMCode.Bytecode.Instruction.DataType.Int32).Value = (int)number;
                            ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.Int32);
                        }
                    }
                    else
                    {
                        // 64-bit int
                        Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Push, GMCode.Bytecode.Instruction.DataType.Int64).Value = number;
                        ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.Int64);
                    }
                }
                else
                {
                    // Floating point (64-bit)
                    Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Push, GMCode.Bytecode.Instruction.DataType.Double).Value = constant.ValueNumber;
                    ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.Double);
                }
                break;
            case ConstantKind.Int64:
                Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Push, GMCode.Bytecode.Instruction.DataType.Int64).Value = constant.ValueInt64;
                ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.Int64);
                break;
            case ConstantKind.String:
                EmitString(ctx, GMCode.Bytecode.Instruction.Opcode.Push, GMCode.Bytecode.Instruction.DataType.String, constant.ValueString);
                ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.String);
                break;
        }
    }

    private static void CompileBinary(CodeContext ctx, Node bin)
    {
        TokenKind kind = bin.Token.Kind;

        // Put left value onto stack
        CompileExpression(ctx, bin.Children[0]);

        if ((kind == TokenKind.And || kind == TokenKind.Or) &&
             ctx.BaseContext.Project.DataHandle.VersionInfo.ShortCircuit)
        {
            // This is a short-circuit evaluation
            var branchJump = new JumpForwardPatch();

            for (int i = 1; i < bin.Children.Count; i++)
            {
                // Convert previous value in chain to boolean
                ConvertTo(ctx, GMCode.Bytecode.Instruction.DataType.Boolean);

                // If necessary, branch to the end
                branchJump.Add(Emit(ctx, (kind == TokenKind.And) ? GMCode.Bytecode.Instruction.Opcode.Bf : GMCode.Bytecode.Instruction.Opcode.Bt));

                // Put next value onto the stack
                CompileExpression(ctx, bin.Children[i]);
            }

            // (convert last value in chain to boolean)
            ConvertTo(ctx, GMCode.Bytecode.Instruction.DataType.Boolean);

            // Push final result
            var endJump = new JumpForwardPatch(Emit(ctx, GMCode.Bytecode.Instruction.Opcode.B));
            branchJump.Finish(ctx);
            Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Push, GMCode.Bytecode.Instruction.DataType.Int16).Value = ((kind == TokenKind.And) ? (short)0 : (short)1);
            ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.Boolean);
            endJump.Finish(ctx);
            return;
        }

        // Convert left value on stack appropriately
        ConvertForBinaryOp(ctx, bin.Token.Kind);

        for (int i = 1; i < bin.Children.Count; i++)
        {
            // Put next value onto the stack
            CompileExpression(ctx, bin.Children[i]);
            ConvertForBinaryOp(ctx, bin.Token.Kind);

            // Figure out the new type after the operation
            GMCode.Bytecode.Instruction.DataType type1 = ctx.TypeStack.Pop();
            GMCode.Bytecode.Instruction.DataType type2 = ctx.TypeStack.Pop();

            // Produce actual operation instructions
            bool pushesBoolean = false;
            switch (bin.Token.Kind)
            {
                case TokenKind.Plus:
                    Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Add, type1, type2);
                    break;
                case TokenKind.Minus:
                    Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Sub, type1, type2);
                    break;
                case TokenKind.Times:
                    Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Mul, type1, type2);
                    break;
                case TokenKind.Divide:
                    Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Div, type1, type2);
                    break;
                case TokenKind.Div:
                    Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Rem, type1, type2);
                    break;
                case TokenKind.Mod:
                    Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Mod, type1, type2);
                    break;
                case TokenKind.BitShiftLeft:
                    Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Shl, type1, type2);
                    break;
                case TokenKind.BitShiftRight:
                    Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Shr, type1, type2);
                    break;
                case TokenKind.And:
                case TokenKind.BitAnd:
                    Emit(ctx, GMCode.Bytecode.Instruction.Opcode.And, type1, type2);
                    break;
                case TokenKind.Or:
                case TokenKind.BitOr:
                    Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Or, type1, type2);
                    break;
                case TokenKind.Xor:
                case TokenKind.BitXor:
                    Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Or, type1, type2);
                    break;
                case TokenKind.Equal:
                    EmitCompare(ctx, GMCode.Bytecode.Instruction.ComparisonType.EQ, type1, type2);
                    pushesBoolean = true;
                    break;
                case TokenKind.NotEqual:
                    EmitCompare(ctx, GMCode.Bytecode.Instruction.ComparisonType.NEQ, type1, type2);
                    pushesBoolean = true;
                    break;
                case TokenKind.Greater:
                    EmitCompare(ctx, GMCode.Bytecode.Instruction.ComparisonType.GT, type1, type2);
                    pushesBoolean = true;
                    break;
                case TokenKind.GreaterEqual:
                    EmitCompare(ctx, GMCode.Bytecode.Instruction.ComparisonType.GTE, type1, type2);
                    pushesBoolean = true;
                    break;
                case TokenKind.Lesser:
                    EmitCompare(ctx, GMCode.Bytecode.Instruction.ComparisonType.LT, type1, type2);
                    pushesBoolean = true;
                    break;
                case TokenKind.LesserEqual:
                    EmitCompare(ctx, GMCode.Bytecode.Instruction.ComparisonType.LTE, type1, type2);
                    pushesBoolean = true;
                    break;
            }

            if (pushesBoolean)
            {
                ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.Boolean);
            }
            else
            {
                // Figure out resulting type
                int type1Bias = DataTypeBias(type1);
                int type2Bias = DataTypeBias(type2);
                GMCode.Bytecode.Instruction.DataType newType;
                if (type1Bias == type2Bias)
                {
                    // Same bias, so just go for the lower DataType value
                    newType = (GMCode.Bytecode.Instruction.DataType)Math.Min((byte)type1, (byte)type2);
                }
                else
                {
                    newType = (type1Bias > type2Bias) ? type1 : type2;
                }
                ctx.TypeStack.Push(newType);
            }
        }
    }

    /// <summary>
    /// Returns the priority of a given data type in an expression
    /// </summary>
    private static int DataTypeBias(GMCode.Bytecode.Instruction.DataType type)
    {
        return type switch
        {
            GMCode.Bytecode.Instruction.DataType.Float or GMCode.Bytecode.Instruction.DataType.Int32 or GMCode.Bytecode.Instruction.DataType.Boolean or GMCode.Bytecode.Instruction.DataType.String => 0,
            GMCode.Bytecode.Instruction.DataType.Double or GMCode.Bytecode.Instruction.DataType.Int64 => 1,
            GMCode.Bytecode.Instruction.DataType.Variable => 2,
            _ => -1,
        };
    }

    /// <summary>
    /// Converts the type on the top of the stack to match the operators being used
    /// </summary>
    private static void ConvertForBinaryOp(CodeContext ctx, TokenKind kind)
    {
        GMCode.Bytecode.Instruction.DataType type = ctx.TypeStack.Peek();
        switch (kind)
        {
            case TokenKind.Divide:
                if (type != GMCode.Bytecode.Instruction.DataType.Double && type != GMCode.Bytecode.Instruction.DataType.Variable)
                {
                    ctx.TypeStack.Pop();
                    Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Conv, type, GMCode.Bytecode.Instruction.DataType.Double);
                    ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.Double);
                }
                break;
            case TokenKind.Plus:
            case TokenKind.Minus:
            case TokenKind.Times:
            case TokenKind.Div:
            case TokenKind.Mod:
                if (type == GMCode.Bytecode.Instruction.DataType.Boolean)
                {
                    ctx.TypeStack.Pop();
                    Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Conv, type, GMCode.Bytecode.Instruction.DataType.Int32);
                    ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.Double);
                }
                break;
            case TokenKind.BitAnd:
            case TokenKind.BitOr:
            case TokenKind.BitXor:
                if (type != GMCode.Bytecode.Instruction.DataType.Int32)
                {
                    ctx.TypeStack.Pop();
                    if (type != GMCode.Bytecode.Instruction.DataType.Variable &&
                        type != GMCode.Bytecode.Instruction.DataType.Double &&
                        type != GMCode.Bytecode.Instruction.DataType.Int64)
                    {
                        Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Conv, type, GMCode.Bytecode.Instruction.DataType.Int32);
                        ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.Int32);
                    }
                    else
                    {
                        if (type != GMCode.Bytecode.Instruction.DataType.Int64)
                            Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Conv, type, GMCode.Bytecode.Instruction.DataType.Int64);
                        ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.Int64);
                    }
                }
                break;
            case TokenKind.BitShiftLeft:
            case TokenKind.BitShiftRight:
                if (type != GMCode.Bytecode.Instruction.DataType.Int64)
                {
                    ctx.TypeStack.Pop();
                    Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Conv, type, GMCode.Bytecode.Instruction.DataType.Int64);
                    ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.Int64);
                }
                break;
            case TokenKind.And:
            case TokenKind.Or:
            case TokenKind.Xor:
                if (type != GMCode.Bytecode.Instruction.DataType.Boolean)
                {
                    ctx.TypeStack.Pop();
                    Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Conv, type, GMCode.Bytecode.Instruction.DataType.Boolean);
                    ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.Boolean);
                }
                break;
        }
    }

    private static void CompileUnary(CodeContext ctx, Node unary)
    {
        CompileExpression(ctx, unary.Children[0]);
        GMCode.Bytecode.Instruction.DataType type = ctx.TypeStack.Peek();

        switch (unary.Token.Kind)
        {
            case TokenKind.Not:
                if (type != GMCode.Bytecode.Instruction.DataType.Boolean)
                {
                    if (type == GMCode.Bytecode.Instruction.DataType.String)
                        ctx.Error("Cannot use '!' on string", unary.Token.Index);
                    ctx.TypeStack.Pop();
                    Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Conv, type, GMCode.Bytecode.Instruction.DataType.Boolean);
                    ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.Boolean);
                }
                Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Not, GMCode.Bytecode.Instruction.DataType.Boolean);
                break;
            case TokenKind.Minus:
                if (type == GMCode.Bytecode.Instruction.DataType.String)
                    ctx.Error("Cannot use '-' on string", unary.Token.Index);
                else if (type == GMCode.Bytecode.Instruction.DataType.Boolean)
                {
                    ctx.TypeStack.Pop();
                    Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Conv, GMCode.Bytecode.Instruction.DataType.Boolean, GMCode.Bytecode.Instruction.DataType.Int32);
                    ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.Int32);
                }
                Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Neg, type);
                break;
            case TokenKind.BitNegate:
                if (type == GMCode.Bytecode.Instruction.DataType.String)
                    ctx.Error("Cannot use '~' on string", unary.Token.Index);
                else if (type == GMCode.Bytecode.Instruction.DataType.Double ||
                         type == GMCode.Bytecode.Instruction.DataType.Float ||
                         type == GMCode.Bytecode.Instruction.DataType.Variable)
                {
                    ctx.TypeStack.Pop();
                    Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Conv, type, GMCode.Bytecode.Instruction.DataType.Int32);
                    ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.Int32);
                }
                Emit(ctx, GMCode.Bytecode.Instruction.Opcode.Not, type);
                break;
        }
    }

    private static void CompileChain(CodeContext ctx, Node chain)
    {
        // Compile left side first
        CompileExpression(ctx, chain.Children[0]);

        // Compile right side separately
        Node rhs = chain.Children[1];
        if (rhs.Kind == NodeKind.Variable)
        {
            ConvertToInstance(ctx);

            (rhs.Token.Value as TokenVariable).VariableType = GMCode.Bytecode.Instruction.VariableType.StackTop;
            CompileVariable(ctx, rhs, true);
        } 
        else if (rhs.Kind == NodeKind.FunctionCall)
        {
            if (ConvertTo(ctx, GMCode.Bytecode.Instruction.DataType.Variable))
                EmitCall(ctx, GMCode.Bytecode.Instruction.Opcode.Call, GMCode.Bytecode.Instruction.DataType.Int32, Builtins.MakeFuncToken(ctx, "@@GetInstance@@"), 1);
            CompileFunctionCall(ctx, rhs, true);
        }
        else
            ctx.Error("Unsupported chain variable", rhs.Token);
    }

    private static void CompileVariable(CodeContext ctx, Node variable, bool inChain)
    {
        TokenVariable tokenVar = variable.Token.Value as TokenVariable;

        if (!inChain)
        {
            // Process variable and instance type
            ProcessTokenVariable(ctx, ref tokenVar);
        }

        if (variable.Children.Count != 0)
        {
            if (!inChain)
            {
                // Compile instance type
                CompileConstant(ctx, new TokenConstant((double)tokenVar.InstanceType));
                ConvertToInstance(ctx);
            }

            // Deal with array index
            CompileExpression(ctx, variable.Children[0]);
            ConvertTo(ctx, GMCode.Bytecode.Instruction.DataType.Int32);

            tokenVar.VariableType = (variable.Children.Count == 1 ? GMCode.Bytecode.Instruction.VariableType.Array : GMCode.Bytecode.Instruction.VariableType.MultiPush);
            tokenVar.InstanceType = (int)GMCode.Bytecode.Instruction.InstanceType.Self;
        }

        // Emit actual push instruction
        var opcode = tokenVar.InstanceType switch
        {
            (int)GMCode.Bytecode.Instruction.InstanceType.Global => GMCode.Bytecode.Instruction.Opcode.PushGlb,
            (int)GMCode.Bytecode.Instruction.InstanceType.Builtin => GMCode.Bytecode.Instruction.Opcode.PushBltn,
            (int)GMCode.Bytecode.Instruction.InstanceType.Local => GMCode.Bytecode.Instruction.Opcode.PushLoc,
            _ => ((tokenVar.Builtin == null || !tokenVar.Builtin.IsGlobal || tokenVar.VariableType != GMCode.Bytecode.Instruction.VariableType.Normal) 
                    ? GMCode.Bytecode.Instruction.Opcode.Push : GMCode.Bytecode.Instruction.Opcode.PushBltn),
        };
        EmitVariable(ctx, opcode, GMCode.Bytecode.Instruction.DataType.Variable, tokenVar);
        ctx.TypeStack.Push(GMCode.Bytecode.Instruction.DataType.Variable);

        if (variable.Children.Count >= 2)
            CompileMultiArrayPush(ctx, variable);
    }

    private static void CompileMultiArrayPush(CodeContext ctx, Node variable)
    {
        for (int i = 1; i < variable.Children.Count; i++)
        {
            // Push next array index to stack
            CompileExpression(ctx, variable.Children[i]);
            ConvertTo(ctx, GMCode.Bytecode.Instruction.DataType.Int32);

            // Actual array access instructions
            if (i == variable.Children.Count - 1)
            {
                // For the last one, pushaf
                EmitBreak(ctx, GMCode.Bytecode.Instruction.BreakType.pushaf);
            }
            else
            {
                // For every other one, pushac
                EmitBreak(ctx, GMCode.Bytecode.Instruction.BreakType.pushac);
            }
        }
    }
}