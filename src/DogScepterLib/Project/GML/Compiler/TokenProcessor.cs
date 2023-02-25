﻿namespace DogScepterLib.Project.GML.Compiler;

public static class TokenProcessor
{
    public static void ProcessIdentifiers(CodeContext ctx)
    {
        for (int i = 0; i < ctx.Tokens.Count; i++)
        {
            Token curr = ctx.Tokens[i];
            if (curr.Kind == TokenKind.Identifier)
            {
                string name = ctx.Tokens[i].Text;

                // Check macros
                if (ctx.BaseContext.Macros.TryGetValue(name, out CodeContext macro))
                {
                    ctx.Tokens.RemoveAt(i);
                    ctx.Tokens.InsertRange(i, macro.Tokens);
                    if (macro.Tokens.Count != 0)
                        i--; // Process the first macro token when returning to loop
                    continue;
                }

                if (i + 1 < ctx.Tokens.Count && ctx.Tokens[i + 1].Kind == TokenKind.Open)
                {
                    // Process function call
                    BuiltinFunction builtin;
                    ctx.BaseContext.Builtins.Functions.TryGetValue(name, out builtin);
                    ctx.Tokens[i] = new Token(ctx, new TokenFunction(name, builtin), ctx.Tokens[i].Index);
                }
                else
                {
                    // Process everything else

                    // Check assets
                    if (ctx.BaseContext.AssetIds.TryGetValue(name, out int assetId))
                    {
                        ctx.Tokens[i] = new Token(ctx, new TokenConstant((double)assetId), ctx.Tokens[i].Index, name);
                        continue;
                    }

                    // Check builtin constants
                    if (ctx.BaseContext.Builtins.Constants.TryGetValue(name, out double constant))
                    {
                        var constantToken = new TokenConstant(constant);
                        if (ctx.BaseContext.IsGMS23)
                            constantToken.IsBool = (name == "true" || name == "false");
                        ctx.Tokens[i] = new Token(ctx, constantToken, ctx.Tokens[i].Index, name);
                        continue;
                    }

                    // If none of the above apply, this should be a variable
                    // Check if it's a builtin, too
                    BuiltinVariable builtin;
                    if (!ctx.BaseContext.Builtins.VarGlobal.TryGetValue(name, out builtin))
                    {
                        if (!ctx.BaseContext.Builtins.VarGlobalArray.TryGetValue(name, out builtin))
                        {
                            ctx.BaseContext.Builtins.VarInstance.TryGetValue(name, out builtin);
                        }
                    }
                    ctx.Tokens[i] = new Token(ctx, new TokenVariable(name, builtin), ctx.Tokens[i].Index);
                }
            }
        }
    }
}
