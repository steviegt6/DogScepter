﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DogScepterLib.Project.GML.Compiler;

public class Lexer
{
    public static void LexCode(CodeContext ctx)
    {
        ctx.Tokens = new();
        ctx.Position = 0;
        Token next;
        do
        {
            next = GetNextToken(ctx);
            ctx.Tokens.Add(next);
        }
        while (next.Kind != TokenKind.EOF && next.Kind != TokenKind.Error);

        // Remove EOF token for macros
        if (ctx.Kind == CodeContext.CodeKind.Macro && next.Kind == TokenKind.EOF)
            ctx.Tokens.RemoveAt(ctx.Tokens.Count - 1);
        else
        {
            // Add safety EOF tokens at the end for parsing
            Token eof = ctx.Tokens[^1];
            for (int i = 0; i < 8; i++)
                ctx.Tokens.Add(eof);
        }
    }

    private static Token GetNextToken(CodeContext ctx)
    {
        SkipWhitespace(ctx);
        if (ctx.Position >= ctx.Code.Length)
            return new Token(ctx, TokenKind.EOF, ctx.Position);

        char c = ctx.Code[ctx.Position];
        char lookahead = (ctx.Position + 1 < ctx.Code.Length) ? ctx.Code[ctx.Position + 1] : '\0';
            
        // Directives, such as macros
        while (c == '#' && lookahead != '\0')
        {
            int startIndex = ctx.Position;

            // Read the type
            StringBuilder directiveType = new();
            directiveType.Append(lookahead);
            ctx.Position += 2;
            while (ctx.Position < ctx.Code.Length && !char.IsWhiteSpace(ctx.Code[ctx.Position]))
            {
                directiveType.Append(ctx.Code[ctx.Position]);
                ctx.Position++;
            }
                
            // Process each type
            switch (directiveType.ToString())
            {
                case "macro":
                    {
                        // Parse the macro's name
                        ctx.Position++;
                        StringBuilder macroName = new();
                        while (ctx.Position < ctx.Code.Length && !char.IsWhiteSpace(ctx.Code[ctx.Position]))
                        {
                            macroName.Append(ctx.Code[ctx.Position]);
                            ctx.Position++;
                        }

                        // Parse the macro's content
                        ctx.Position++;
                        StringBuilder macroContent = new();
                        while (ctx.Position < ctx.Code.Length && ctx.Code[ctx.Position] != '\n')
                        {
                            char curr = ctx.Code[ctx.Position];
                            if (curr == '\\' && ctx.Position + 1 < ctx.Code.Length)
                            {
                                // Ignore the next newline, as long as there's whitespace between now and the end of the line
                                int backslashPos = ctx.Position++;
                                do
                                {
                                    curr = ctx.Code[ctx.Position++];
                                }
                                while (ctx.Position < ctx.Code.Length && char.IsWhiteSpace(curr) && curr != '\n');

                                if (curr == '\n')
                                {
                                    // We found the newline, so we're safe to resume
                                    continue;
                                }
                                else
                                {
                                    // We didn't find a newline, so this is something else
                                    ctx.Position = backslashPos;
                                }
                            }
                            macroContent.Append(curr);
                            ctx.Position++;
                        }

                        // Attempt adding the macro
                        string newMacroName = macroName.ToString(), newMacroContent = macroContent.ToString();
                        CodeContext newMacro = new(ctx.BaseContext, $"macro \"{macroName}\" from \"{ctx.Name}\"", newMacroContent, CodeContext.CodeMode.Replace, false);
                        newMacro.Kind = CodeContext.CodeKind.Macro;
                        if (!ctx.BaseContext.Macros.TryAdd(newMacroName, newMacro))
                        {
                            // The macro was already defined!
                            ctx.Error($"Duplicate macro \"{newMacroName}\" found", startIndex);
                        }
                        else
                        {
                            // Tokenize this macro
                            LexCode(newMacro);
                        }
                        break;
                    }
                case "region":
                case "endregion":
                    // Skip other recognized directives we don't care about
                    while (ctx.Position < ctx.Code.Length && ctx.Code[ctx.Position] != '\n')
                        ctx.Position++;
                    break;
                default:
                    // This might actually be an RGB color literal
                    if (directiveType.Length == 6 || directiveType.Length == 8)
                    {
                        string color = directiveType.ToString();
                        bool valid = true;
                        foreach (char hex in color)
                        {
                            if ((hex < '0' || hex > '9') && (hex < 'A' || hex > 'F') && (hex < 'a' || hex > 'f'))
                            {
                                valid = false;
                                break;
                            }
                        }
                        if (valid)
                        {
                            // Convert RGB to BGR and parse
                            string converted = color[4..6] + color[2..4] + color[0..2];
                            if (color.Length == 8)
                                converted += color[6..8];
                            if (long.TryParse(converted, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value))
                            {
                                if (value >= int.MinValue && value <= int.MaxValue)
                                    return new Token(ctx, new TokenConstant((double)value), startIndex);
                                return new Token(ctx, new TokenConstant(value), startIndex);
                            }
                        }
                    }

                    // Give an error at compile time (such as in the case of typos of "macro") to try to be helpful
                    ctx.Error($"Unrecognized directive \"{directiveType}\"", startIndex);
                    break;
            }

            // Need to set up for the rest of the token parsing again
            SkipWhitespace(ctx);
            if (ctx.Position >= ctx.Code.Length)
                return new Token(ctx, TokenKind.EOF, ctx.Position);
            c = ctx.Code[ctx.Position];
            lookahead = (ctx.Position + 1 < ctx.Code.Length) ? ctx.Code[ctx.Position + 1] : '\0';
        }

        // Identifiers
        if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_')
        {
            return ReadIdentifier(ctx);
        }
            
        // Numbers/hex
        if (char.IsDigit(c))
        {
            if (c == '0' && lookahead == 'x')
                return ReadHex(ctx);
            return ReadNumber(ctx);
        }
        if (c == '$')
            return ReadHex(ctx);
        if (c == '.' && char.IsDigit(lookahead))
            return ReadNumber(ctx);

        // Strings
        if (ctx.BaseContext.IsGMS2)
        {
            if (c == '@' && (lookahead == '"' || lookahead == '\''))
            {
                ctx.Position++;
                return ReadVerbatimString(ctx);
            }
            if (c == '"')
                return ReadString(ctx);
        }
        else
        {
            if (c == '"' || c == '\'')
                return ReadVerbatimString(ctx);
        }

        // All other symbols
        switch (c)
        {
            case '{':
                return new Token(ctx, TokenKind.Begin, ctx.Position++);
            case '}':
                return new Token(ctx, TokenKind.End, ctx.Position++);
            case '(':
                return new Token(ctx, TokenKind.Open, ctx.Position++);
            case ')':
                return new Token(ctx, TokenKind.Close, ctx.Position++);
            case '=':
                if (lookahead == '=')
                {
                    int startPos = ctx.Position;
                    ctx.Position += 2;
                    return new Token(ctx, TokenKind.Equal, startPos);
                }
                return new Token(ctx, TokenKind.Assign, ctx.Position++);
            case '+':
                if (lookahead == '+')
                {
                    int startPos = ctx.Position;
                    ctx.Position += 2;
                    return new Token(ctx, TokenKind.Increment, startPos);
                }
                if (lookahead == '=')
                {
                    int startPos = ctx.Position;
                    ctx.Position += 2;
                    return new Token(ctx, TokenKind.AssignPlus, startPos);
                }
                return new Token(ctx, TokenKind.Plus, ctx.Position++);
            case '-':
                if (lookahead == '-')
                {
                    int startPos = ctx.Position;
                    ctx.Position += 2;
                    return new Token(ctx, TokenKind.Decrement, startPos);
                }
                if (lookahead == '=')
                {
                    int startPos = ctx.Position;
                    ctx.Position += 2;
                    return new Token(ctx, TokenKind.AssignMinus, startPos);
                }
                return new Token(ctx, TokenKind.Minus, ctx.Position++);
            case '*':
                if (lookahead == '=')
                {
                    int startPos = ctx.Position;
                    ctx.Position += 2;
                    return new Token(ctx, TokenKind.AssignTimes, startPos);
                }
                return new Token(ctx, TokenKind.Times, ctx.Position++);
            case '/':
                if (lookahead == '=')
                {
                    int startPos = ctx.Position;
                    ctx.Position += 2;
                    return new Token(ctx, TokenKind.AssignDivide, startPos);
                }
                return new Token(ctx, TokenKind.Divide, ctx.Position++);
            case '!':
                if (lookahead == '=')
                {
                    int startPos = ctx.Position;
                    ctx.Position += 2;
                    return new Token(ctx, TokenKind.NotEqual, startPos);
                }
                return new Token(ctx, TokenKind.Not, ctx.Position++);
            case ',':
                return new Token(ctx, TokenKind.Comma, ctx.Position++);
            case '.':
                return new Token(ctx, TokenKind.Dot, ctx.Position++);
            case ':':
                if (lookahead == '=')
                {
                    int startPos = ctx.Position;
                    ctx.Position += 2;
                    return new Token(ctx, TokenKind.Assign, startPos);
                }
                return new Token(ctx, TokenKind.Colon, ctx.Position++);
            case ';':
                return new Token(ctx, TokenKind.Semicolon, ctx.Position++);
            case '[':
                switch (lookahead)
                {
                    case '|':
                        {
                            int startPos = ctx.Position;
                            ctx.Position += 2;
                            return new Token(ctx, TokenKind.ArrayListOpen, startPos);
                        }
                    case '?':
                        {
                            int startPos = ctx.Position;
                            ctx.Position += 2;
                            return new Token(ctx, TokenKind.ArrayMapOpen, startPos);
                        }
                    case '#':
                        {
                            int startPos = ctx.Position;
                            ctx.Position += 2;
                            return new Token(ctx, TokenKind.ArrayGridOpen, startPos);
                        }
                    case '@':
                        {
                            int startPos = ctx.Position;
                            ctx.Position += 2;
                            return new Token(ctx, TokenKind.ArrayDirectOpen, startPos);
                        }
                    case '$':
                        {
                            int startPos = ctx.Position;
                            ctx.Position += 2;
                            return new Token(ctx, TokenKind.ArrayStructOpen, startPos);
                        }
                }
                return new Token(ctx, TokenKind.ArrayOpen, ctx.Position++);
            case ']':
                return new Token(ctx, TokenKind.ArrayClose, ctx.Position++);
            case '<':
                switch (lookahead)
                {
                    case '=':
                        {
                            int startPos = ctx.Position;
                            ctx.Position += 2;
                            return new Token(ctx, TokenKind.LesserEqual, startPos);
                        }
                    case '<':
                        {
                            int startPos = ctx.Position;
                            ctx.Position += 2;
                            return new Token(ctx, TokenKind.BitShiftLeft, startPos);
                        }
                    case '>':
                        {
                            int startPos = ctx.Position;
                            ctx.Position += 2;
                            return new Token(ctx, TokenKind.NotEqual, startPos);
                        }
                }
                return new Token(ctx, TokenKind.Lesser, ctx.Position++);
            case '>':
                switch (lookahead)
                {
                    case '=':
                        {
                            int startPos = ctx.Position;
                            ctx.Position += 2;
                            return new Token(ctx, TokenKind.GreaterEqual, startPos);
                        }
                    case '>':
                        {
                            int startPos = ctx.Position;
                            ctx.Position += 2;
                            return new Token(ctx, TokenKind.BitShiftRight, startPos);
                        }
                }
                return new Token(ctx, TokenKind.Greater, ctx.Position++);
            case '?':
                if (lookahead == '?')
                {
                    int startPos = ctx.Position;
                    if (ctx.Position < ctx.Code.Length &&
                        ctx.Code[ctx.Position] == '=')
                    {
                        ctx.Position += 3;
                        return new Token(ctx, TokenKind.AssignNullCoalesce, startPos);
                    }
                    ctx.Position += 2;
                    return new Token(ctx, TokenKind.NullCoalesce, startPos);
                }
                return new Token(ctx, TokenKind.Conditional, ctx.Position++);
            case '%':
                if (lookahead == '=')
                {
                    int startPos = ctx.Position;
                    ctx.Position += 2;
                    return new Token(ctx, TokenKind.AssignMod, startPos);
                }
                return new Token(ctx, TokenKind.Mod, ctx.Position++);
            case '&':
                if (lookahead == '&')
                {
                    int startPos = ctx.Position;
                    ctx.Position += 2;
                    return new Token(ctx, TokenKind.And, startPos);
                }
                if (lookahead == '=')
                {
                    int startPos = ctx.Position;
                    ctx.Position += 2;
                    return new Token(ctx, TokenKind.AssignAnd, startPos);
                }
                return new Token(ctx, TokenKind.BitAnd, ctx.Position++);
            case '|':
                if (lookahead == '|')
                {
                    int startPos = ctx.Position;
                    ctx.Position += 2;
                    return new Token(ctx, TokenKind.Or, startPos);
                }
                if (lookahead == '=')
                {
                    int startPos = ctx.Position;
                    ctx.Position += 2;
                    return new Token(ctx, TokenKind.AssignOr, startPos);
                }
                return new Token(ctx, TokenKind.BitOr, ctx.Position++);
            case '^':
                if (lookahead == '^')
                {
                    int startPos = ctx.Position;
                    ctx.Position += 2;
                    return new Token(ctx, TokenKind.Xor, startPos);
                }
                if (lookahead == '=')
                {
                    int startPos = ctx.Position;
                    ctx.Position += 2;
                    return new Token(ctx, TokenKind.AssignXor, startPos);
                }
                return new Token(ctx, TokenKind.BitXor, ctx.Position++);
            case '~':
                return new Token(ctx, TokenKind.BitNegate, ctx.Position++);
        }

        // No valid token found
        Token err = new(ctx, TokenKind.Error, ctx.Position++);
        ctx.Error("Invalid token", err);
        return err;
    }

    private static void SkipWhitespace(CodeContext ctx)
    {
        bool stillWhitespace = true;
        while (stillWhitespace)
        {
            // Basic whitespace skipping
            while (ctx.Position < ctx.Code.Length && char.IsWhiteSpace(ctx.Code[ctx.Position]))
                ctx.Position++;

            // Comment skipping
            if (ctx.Position < ctx.Code.Length && ctx.Code[ctx.Position] == '/')
            {
                if (ctx.Position + 1 < ctx.Code.Length)
                {
                    char ahead = ctx.Code[ctx.Position + 1];
                    switch (ahead)
                    {
                        case '/':
                            ctx.Position += 2;
                            while (ctx.Position < ctx.Code.Length)
                            {
                                if (ctx.Code[ctx.Position] == '\n')
                                    break;
                                ctx.Position++;
                            }
                            break;
                        case '*':
                            ctx.Position += 2;
                            while (ctx.Position + 1 < ctx.Code.Length)
                            {
                                if (ctx.Code[ctx.Position] == '*' && ctx.Code[ctx.Position + 1] == '/')
                                    break;
                                ctx.Position++;
                            }
                            if (ctx.Position + 1 < ctx.Code.Length)
                                ctx.Position += 2;
                            else
                                ctx.Position = ctx.Code.Length; // EOF
                            break;
                        default:
                            // This isn't a comment, whitespace is over
                            stillWhitespace = false;
                            break;
                    }
                }
                else
                {
                    // This can't be a comment (EOF)
                    stillWhitespace = false;
                }
            }
            else
            {
                // Other characters (or EOF), whitespace is over
                stillWhitespace = false;
            }
        }
    }

    private static Token ReadIdentifier(CodeContext ctx)
    {
        int startPosition = ctx.Position;

        StringBuilder sb = new();
        sb.Append(ctx.Code[ctx.Position++]);
        while (ctx.Position < ctx.Code.Length)
        {
            char c = ctx.Code[ctx.Position];
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')
            {
                sb.Append(c);
                ctx.Position++;
            }
            else
                break;
        }

        string identifier = sb.ToString();
        return identifier switch
        {
            "and" => new Token(ctx, TokenKind.And, startPosition),
            "or" => new Token(ctx, TokenKind.Or, startPosition),
            "xor" => new Token(ctx, TokenKind.Xor, startPosition),
            "while" => new Token(ctx, TokenKind.While, startPosition),
            "with" => new Token(ctx, TokenKind.With, startPosition),
            "if" => new Token(ctx, TokenKind.If, startPosition),
            "do" => new Token(ctx, TokenKind.Do, startPosition),
            "not" => new Token(ctx, TokenKind.Not, startPosition),
            "enum" => new Token(ctx, TokenKind.Enum, startPosition),
            "begin" => new Token(ctx, TokenKind.Begin, startPosition),
            "end" => new Token(ctx, TokenKind.End, startPosition),
            "var" => new Token(ctx, TokenKind.Var, startPosition),
            "globalvar" => new Token(ctx, TokenKind.Globalvar, startPosition),
            "return" => new Token(ctx, TokenKind.Return, startPosition),
            "default" => new Token(ctx, TokenKind.Default, startPosition),
            "for" => new Token(ctx, TokenKind.For, startPosition),
            "case" => new Token(ctx, TokenKind.Case, startPosition),
            "switch" => new Token(ctx, TokenKind.Switch, startPosition),
            "until" => new Token(ctx, TokenKind.Until, startPosition),
            "continue" => new Token(ctx, TokenKind.Continue, startPosition),
            "break" => new Token(ctx, TokenKind.Break, startPosition),
            "else" => new Token(ctx, TokenKind.Else, startPosition),
            "repeat" => new Token(ctx, TokenKind.Repeat, startPosition),
            "exit" => new Token(ctx, TokenKind.Exit, startPosition),
            "then" => new Token(ctx, TokenKind.Then, startPosition),
            "mod" => new Token(ctx, TokenKind.Mod, startPosition),
            "div" => new Token(ctx, TokenKind.Div, startPosition),
            "function" => new Token(ctx, TokenKind.Function, startPosition),
            "new" => new Token(ctx, TokenKind.New, startPosition),
            "delete" => new Token(ctx, TokenKind.Delete, startPosition),
            "throw" => new Token(ctx, TokenKind.Throw, startPosition),
            "try" => new Token(ctx, TokenKind.Try, startPosition),
            "catch" => new Token(ctx, TokenKind.Catch, startPosition),
            "finally" => new Token(ctx, TokenKind.Finally, startPosition),
            "static" => new Token(ctx, TokenKind.Static, startPosition),
            _ => new Token(ctx, TokenKind.Identifier, startPosition, identifier)
        };
    }

    private static Token ReadNumber(CodeContext ctx)
    {
        int startPosition = ctx.Position;

        // Read the part of the string we're interested in
        while (ctx.Position < ctx.Code.Length)
        {
            char c = ctx.Code[ctx.Position];
            if (char.IsDigit(c) || c == '.')
                ctx.Position++;
            else
                break;
        }
        ReadOnlySpan<char> str = ctx.Code.AsSpan(startPosition, ctx.Position - startPosition);

        // Parse the number immediately
        if (long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            if ((long)(double)value == value)
                return new Token(ctx, new TokenConstant((double)value), startPosition);
            return new Token(ctx, new TokenConstant(value), startPosition);
        }
        if (double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out double value2))
            return new Token(ctx, new TokenConstant(value2), startPosition);

        ctx.Error($"Invalid number \"{str}\"", startPosition);
        return new Token(ctx, startPosition);
    }

    private static Token ReadHex(CodeContext ctx)
    {
        int startPosition = ctx.Position;

        // Read the part of the string we're interested in
        ctx.Position++;
        if (ctx.Code[ctx.Position] == 'x')
            ctx.Position++;
        int startNumber = ctx.Position;
        while (ctx.Position < ctx.Code.Length)
        {
            char c = ctx.Code[ctx.Position];
            if (char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
                ctx.Position++;
            else
                break;
        }
        ReadOnlySpan<char> str = ctx.Code.AsSpan(startNumber, ctx.Position - startNumber);

        // Parse the number immediately
        if (long.TryParse(str, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value))
        {
            if (value >= int.MinValue && value <= int.MaxValue)
                return new Token(ctx, new TokenConstant((double)value), startPosition);
            return new Token(ctx, new TokenConstant(value), startPosition);
        }

        ctx.Error($"Invalid hex literal \"{str}\"", startPosition);
        return new Token(ctx, startPosition);
    }

    private static Token ReadVerbatimString(CodeContext ctx)
    {
        int startPosition = ctx.Position;

        StringBuilder sb = new();
        char startChar = ctx.Code[ctx.Position++];

        bool finished = false;

        while (ctx.Position < ctx.Code.Length)
        {
            char c = ctx.Code[ctx.Position];
            ctx.Position++;
            if (c == startChar)
            {
                finished = true;
                break;
            }
            sb.Append(c);
        }

        if (!finished)
            ctx.Error("Unenclosed string", startPosition);

        return new Token(ctx, new TokenConstant(sb.ToString()), startPosition);
    }

    private static Token ReadString(CodeContext ctx)
    {
        int startPosition = ctx.Position++;

        bool finished = false;

        StringBuilder sb = new();
        while (ctx.Position < ctx.Code.Length)
        {
            char c = ctx.Code[ctx.Position];
            if (c == '"')
            {
                ctx.Position++;
                finished = true;
                break;
            }
            if (c == '\\')
            {
                ctx.Position++;
                if (ctx.Position < ctx.Code.Length)
                {
                    // Escape codes
                    c = ctx.Code[ctx.Position++];
                    switch (c)
                    {
                        case '\n':
                            // Ignore newline
                            continue;
                        case 'a':
                            sb.Append('\a');
                            break;
                        case 'b':
                            sb.Append('\b');
                            break;
                        case 'f':
                            sb.Append('\f');
                            break;
                        case 'n':
                            sb.Append('\n');
                            break;
                        case 'r':
                            sb.Append('\r');
                            break;
                        case 't':
                            sb.Append('\t');
                            break;
                        case 'v':
                            sb.Append('\v');
                            break;
                        case 'u':
                            {
                                // Unicode character (as hex)
                                ctx.Position++;
                                int result = 0;
                                int charsRead = 0;
                                while (ctx.Position < ctx.Code.Length && charsRead < 6)
                                {
                                    // Read current character as 4 bits (one hex character)
                                    int curr = ConvertHexToInt(ctx.Code[ctx.Position]);
                                    if (curr == -1)
                                        break;
                                    result = (result << 4) + curr;
                                    ctx.Position++;
                                    charsRead++;
                                }
                                if (charsRead != 0)
                                {
                                    try
                                    {
                                        sb.Append(char.ConvertFromUtf32(result));
                                    }
                                    catch (ArgumentOutOfRangeException)
                                    {
                                        ctx.Error("\\u value in string not in valid range.", new Token(ctx, startPosition));
                                    }
                                }
                            }
                            break;
                        case 'x':
                            {
                                // Hex character
                                ctx.Position++;
                                int result = 0;
                                int charsRead = 0;
                                while (ctx.Position < ctx.Code.Length && charsRead < 2)
                                {
                                    // Read current character as 4 bits (one hex character)
                                    int curr = ConvertHexToInt(ctx.Code[ctx.Position]);
                                    if (curr == -1)
                                        break;
                                    result = (result << 4) + curr;
                                    ctx.Position++;
                                    charsRead++;
                                }
                                if (charsRead == 2)
                                    sb.Append((char)result);
                                else
                                    ctx.Error("\\x value in string is missing valid hex characters.", new Token(ctx, startPosition));
                            }
                            break;
                        default:
                            {
                                if (c >= '0' && c <= '7')
                                {
                                    // Octal character
                                    ctx.Position++;
                                    int result = 0;
                                    int charsRead = 0;
                                    while (ctx.Position < ctx.Code.Length && charsRead < 3)
                                    {
                                        // Read current character as octal
                                        c = ctx.Code[ctx.Position];
                                        if (c < '0' || c > '7')
                                            break;
                                        result = (result * 8) + (c - '0');
                                        ctx.Position++;
                                        charsRead++;
                                    }
                                    if (charsRead == 3)
                                        sb.Append((char)result);
                                    else
                                        ctx.Error("\\??? octal value in string is missing valid octal characters.", new Token(ctx, startPosition));
                                }
                                else
                                {
                                    // Verbatim character
                                    sb.Append(c);
                                }
                            }
                            break;
                    }
                }
            }
            else if (c == '\n')
            {
                ctx.Error("Cannot have raw newlines in normal strings.", new Token(ctx, startPosition));
                ctx.Position++;
            }
            else
            {
                sb.Append(c);
                ctx.Position++;
            }
        }

        if (!finished)
            ctx.Error("Unenclosed string", startPosition);

        return new Token(ctx, new TokenConstant(sb.ToString()), startPosition);
    }

    private static int ConvertHexToInt(char c)
    {
        if (c >= '0' && c <= '9')
            return c - '0';
        if (c >= 'a' && c <= 'f')
            return 10 + (c - 'a');
        if (c >= 'A' && c <= 'F')
            return 10 + (c - 'A');
        return -1;
    }
}
