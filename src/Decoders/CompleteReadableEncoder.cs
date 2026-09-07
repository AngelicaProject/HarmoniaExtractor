using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SaintCoinach.Text;

namespace XivExdUnpacker.Decoders;

public sealed class CompleteReadableEncoder
{
    private const byte TagStartMarker = 0x02;
    private const byte TagEndMarker = 0x03;

    private static readonly Encoding Utf8 =
        new UTF8Encoding(false);

    public static byte[] Encode(string value)
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value));

        var parser = new ReadableParser(value);
        var nodes = parser.Parse();

        using var output = new MemoryStream();

        EncodeNodes(output, nodes);

        return output.ToArray();
    }

    private static void EncodeNodes(
        Stream output,
        IReadOnlyList<ReadableNode> nodes)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case TextNode text:
                    WriteText(output, text.Text);
                    break;

                case TagNode tag:
                    EncodeTag(output, tag);
                    break;

                default:
                    throw new InvalidDataException(
                        $"Unknown node type: {node.GetType()}");
            }
        }
    }

    private static void WriteText(
        Stream output,
        string text)
    {
        var bytes = Utf8.GetBytes(text);

        output.Write(
            bytes,
            0,
            bytes.Length);
    }

    // ============================================================
    // TAGS
    // ============================================================

    private static void EncodeTag(
        Stream output,
        TagNode tag)
    {
        switch (tag.Name)
        {
            case "Else":
                throw new InvalidDataException(
                    "<Else/> is only valid inside If/IfEquals.");

            case "Case":
                throw new InvalidDataException(
                    "<Case> is only valid inside Switch.");

            case "If":
                EncodeIf(output, tag);
                return;

            case "IfEquals":
                EncodeIfEquals(output, tag);
                return;

            case "Switch":
                EncodeSwitch(output, tag);
                return;

            case "Color":
                EncodeColor(output, tag);
                return;

            case "Format":
                EncodeFormat(output, tag);
                return;

            case "ZeroPaddedValue":
                EncodeZeroPaddedValue(output, tag);
                return;

            case "Emphasis":
            case "Emphasis2":
                EncodeEmphasis(output, tag);
                return;

            // ----------------------------------------------------
            // Tags whose content is ONE expression.
            //
            // Examples:
            //
            // <Value>IntegerParameter(2)</Value>
            // <Highlight>PlayerParameter(7)</Highlight>
            //
            // Decoder produces these using DecodeWrappedExpressionTag.
            // ----------------------------------------------------

            case "Value":
            case "Highlight":
            case "TwoDigitValue":
            case "InstanceContent":
                EncodeWrappedExpression(output, tag);
                return;
        }

        if (!Enum.TryParse<TagType>(
                tag.Name,
                ignoreCase: false,
                out var tagType))
        {
            throw new InvalidDataException(
                $"Unknown SeString tag: <{tag.Name}>");
        }

        if (tag.IsClosing)
        {
            throw new InvalidDataException(
                $"Unexpected closing tag </{tag.Name}>.");
        }

        // --------------------------------------------------------
        // Empty/self-closing tag
        //
        // <LineBreak/>
        // <Dash/>
        // etc.
        // --------------------------------------------------------

        if (tag.IsSelfClosing &&
            tag.Arguments.Count == 0 &&
            tag.Children.Count == 0)
        {
            WriteTag(
                output,
                tagType,
                Array.Empty<byte>());

            return;
        }

        // --------------------------------------------------------
        // Explicit raw content.
        //
        // Used by tags decoded as:
        //
        // <UnknownXX>010203</UnknownXX>
        //
        // depending on the parser implementation.
        // --------------------------------------------------------

        if (tag.RawContent != null)
        {
            WriteTag(
                output,
                tagType,
                ParseHex(tag.RawContent));

            return;
        }

        // --------------------------------------------------------
        // Parameterized tag
        //
        // <Clickable(...) />
        // <SheetEn(Item,2,...)/>
        // <Gui(...)/>
        // etc.
        // --------------------------------------------------------

        if (tag.Arguments.Count > 0)
        {
            using var payload = new MemoryStream();

            foreach (var argument in tag.Arguments)
            {
                EncodeExpression(
                    payload,
                    argument);
            }

            WriteTag(
                output,
                tagType,
                payload.ToArray());

            return;
        }

        // --------------------------------------------------------
        // Raw-content fallback.
        //
        // IMPORTANT:
        //
        // CompleteReadableDecoder uses DecodeHexContentTag()
        // for unknown/unhandled tags.
        //
        // Therefore:
        //
        // <Unknown60>0142</Unknown60>
        //
        // must be interpreted as raw bytes 01 42.
        //
        // Same for:
        //
        // <UIForeground>F201F4</UIForeground>
        //
        // --------------------------------------------------------

        if (tag.Children.Count > 0)
        {
            if (TryGetSingleTextChild(
                    tag,
                    out var textContent) &&
                IsHex(textContent))
            {
                WriteTag(
                    output,
                    tagType,
                    ParseHex(textContent));

                return;
            }

            throw new InvalidDataException(
                $"Tag <{tag.Name}> has children but no encoder.");
        }

        WriteTag(
            output,
            tagType,
            Array.Empty<byte>());
    }

    // ============================================================
    // WRAPPED EXPRESSION TAGS
    // ============================================================

    private static void EncodeWrappedExpression(
        Stream output,
        TagNode tag)
    {
        if (tag.IsClosing)
        {
            throw new InvalidDataException(
                $"Unexpected closing tag </{tag.Name}>.");
        }

        if (!Enum.TryParse<TagType>(
                tag.Name,
                ignoreCase: false,
                out var tagType))
        {
            throw new InvalidDataException(
                $"Unknown SeString tag: <{tag.Name}>");
        }

        if (tag.Arguments.Count != 0)
        {
            throw new InvalidDataException(
                $"<{tag.Name}> does not accept arguments.");
        }

        if (tag.Children.Count == 0)
        {
            WriteTag(
                output,
                tagType,
                Array.Empty<byte>());

            return;
        }

        // Должен быть ровно один expression.
        var expression = NodesToExpression(
            tag.Children);

        using var payload = new MemoryStream();

        EncodeExpression(
            payload,
            expression);

        WriteTag(
            output,
            tagType,
            payload.ToArray());
    }

    // ============================================================
    // IF
    // ============================================================

    private static void EncodeIf(
        Stream output,
        TagNode tag)
    {
        RequireArguments(tag, 1);

        using var payload = new MemoryStream();

        EncodeExpression(
            payload,
            tag.Arguments[0]);

        var (trueNodes, falseNodes) =
            SplitElse(tag.Children);

        EncodeNestedString(
            payload,
            trueNodes);

        if (falseNodes != null)
        {
            EncodeNestedString(
                payload,
                falseNodes);
        }

        WriteTag(
            output,
            TagType.If,
            payload.ToArray());
    }

    // ============================================================
    // IF EQUALS
    // ============================================================

    private static void EncodeIfEquals(
        Stream output,
        TagNode tag)
    {
        RequireArguments(tag, 2);

        using var payload = new MemoryStream();

        EncodeExpression(
            payload,
            tag.Arguments[0]);

        EncodeExpression(
            payload,
            tag.Arguments[1]);

        var (trueNodes, falseNodes) =
            SplitElse(tag.Children);

        EncodeNestedString(
            payload,
            trueNodes);

        if (falseNodes != null)
        {
            EncodeNestedString(
                payload,
                falseNodes);
        }

        WriteTag(
            output,
            TagType.IfEquals,
            payload.ToArray());
    }

    // ============================================================
    // SWITCH
    // ============================================================

    private static void EncodeSwitch(
        Stream output,
        TagNode tag)
    {
        RequireArguments(tag, 1);

        using var payload = new MemoryStream();

        EncodeExpression(
            payload,
            tag.Arguments[0]);

        foreach (var child in tag.Children)
        {
            if (child is not TagNode caseNode ||
                caseNode.Name != "Case")
            {
                throw new InvalidDataException(
                    "<Switch> can contain only <Case(...)>...</Case>.");
            }

            RequireArguments(
                caseNode,
                1);

            if (!int.TryParse(
                    caseNode.Arguments[0],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out _))
            {
                throw new InvalidDataException(
                    $"Invalid Case index: {caseNode.Arguments[0]}");
            }

            EncodeNestedString(
                payload,
                caseNode.Children);
        }

        WriteTag(
            output,
            TagType.Switch,
            payload.ToArray());
    }

    // ============================================================
    // COLOR
    // ============================================================

    private static void EncodeColor(
        Stream output,
        TagNode tag)
    {
        if (tag.IsClosing)
        {
            WriteTag(
                output,
                TagType.Color,
                new byte[]
                {
                    0xEC
                });

            return;
        }

        RequireArguments(
            tag,
            1);

        using var payload = new MemoryStream();

        EncodeExpression(
            payload,
            tag.Arguments[0]);

        WriteTag(
            output,
            TagType.Color,
            payload.ToArray());
    }

    // ============================================================
    // FORMAT
    // ============================================================

    private static void EncodeFormat(
        Stream output,
        TagNode tag)
    {
        RequireArguments(
            tag,
            2);

        using var payload = new MemoryStream();

        EncodeExpression(
            payload,
            tag.Arguments[0]);

        var raw = ParseHex(
            tag.Arguments[1]);

        payload.Write(
            raw,
            0,
            raw.Length);

        WriteTag(
            output,
            TagType.Format,
            payload.ToArray());
    }

    // ============================================================
    // ZERO PADDED VALUE
    // ============================================================

    private static void EncodeZeroPaddedValue(
        Stream output,
        TagNode tag)
    {
        RequireArguments(
            tag,
            1);

        if (tag.Children.Count == 0)
        {
            throw new InvalidDataException(
                "<ZeroPaddedValue> requires content.");
        }

        var content =
            NodesToExpression(tag.Children);

        using var payload = new MemoryStream();

        EncodeExpression(
            payload,
            content);

        EncodeExpression(
            payload,
            tag.Arguments[0]);

        WriteTag(
            output,
            TagType.ZeroPaddedValue,
            payload.ToArray());
    }

    // ============================================================
    // EMPHASIS
    // ============================================================

    private static void EncodeEmphasis(
        Stream output,
        TagNode tag)
    {
        using var payload = new MemoryStream();

        var status =
            tag.IsClosing
                ? 0
                : 1;

        WriteInteger(
            payload,
            status);

        var tagType =
            tag.Name == "Emphasis"
                ? TagType.Emphasis
                : TagType.Emphasis2;

        WriteTag(
            output,
            tagType,
            payload.ToArray());
    }

    // ============================================================
    // EXPRESSIONS
    // ============================================================

    private static void EncodeExpression(
        Stream output,
        string expression)
    {
        expression = expression.Trim();

        if (expression.Length == 0)
        {
            throw new InvalidDataException(
                "Empty expression.");
        }

        // --------------------------------------------------------
        // Integer
        // --------------------------------------------------------

        if (TryParseInteger(
                expression,
                out var integer))
        {
            EncodeIntegerExpression(
                output,
                integer);

            return;
        }

        // --------------------------------------------------------
        // TopLevelParameter
        // --------------------------------------------------------

        if (TryParseTopLevelParameter(
                expression,
                out var topLevel))
        {
            var opcode =
                topLevel + 1;

            if (opcode < 0xD0 ||
                opcode >= 0xE0)
            {
                throw new InvalidDataException(
                    $"Invalid TopLevelParameter: {topLevel}");
            }

            output.WriteByte(
                (byte)opcode);

            return;
        }

        // --------------------------------------------------------
        // Function call
        // --------------------------------------------------------

        if (LooksLikeCall(expression))
        {
            var call =
                ParseCall(expression);

            switch (call.Name)
            {
                case "IntegerParameter":
                    EncodeUnaryExpression(
                        output,
                        DecodeExpressionType.IntegerParameter,
                        call);
                    return;

                case "PlayerParameter":
                    EncodeUnaryExpression(
                        output,
                        DecodeExpressionType.PlayerParameter,
                        call);
                    return;

                case "StringParameter":
                    EncodeUnaryExpression(
                        output,
                        DecodeExpressionType.StringParameter,
                        call);
                    return;

                case "ObjectParameter":
                    EncodeUnaryExpression(
                        output,
                        DecodeExpressionType.ObjectParameter,
                        call);
                    return;

                case "GreaterThanOrEqualTo":
                    EncodeBinaryExpression(
                        output,
                        DecodeExpressionType.GreaterThanOrEqualTo,
                        call);
                    return;

                case "GreaterThan":
                    EncodeBinaryExpression(
                        output,
                        DecodeExpressionType.GreaterThan,
                        call);
                    return;

                case "LessThanOrEqualTo":
                    EncodeBinaryExpression(
                        output,
                        DecodeExpressionType.LessThanOrEqualTo,
                        call);
                    return;

                case "LessThan":
                    EncodeBinaryExpression(
                        output,
                        DecodeExpressionType.LessThan,
                        call);
                    return;

                case "Equal":
                    EncodeBinaryExpression(
                        output,
                        DecodeExpressionType.Equal,
                        call);
                    return;

                case "NotEqual":
                    EncodeBinaryExpression(
                        output,
                        DecodeExpressionType.NotEqual,
                        call);
                    return;

                default:
                    throw new InvalidDataException(
                        $"Unknown expression: {call.Name}");
            }
        }

        // --------------------------------------------------------
        // Everything else is a nested string.
        //
        // Например:
        //
        // ObjStr
        //
        // --------------------------------------------------------

        EncodeNestedString(
            output,
            new[]
            {
                new TextNode(expression)
            });
    }

    private static void EncodeUnaryExpression(
        Stream output,
        DecodeExpressionType type,
        ExpressionCall call)
    {
        RequireArgumentCount(
            call,
            1);

        output.WriteByte(
            (byte)type);

        EncodeExpression(
            output,
            call.Arguments[0]);
    }

    private static void EncodeBinaryExpression(
        Stream output,
        DecodeExpressionType type,
        ExpressionCall call)
    {
        RequireArgumentCount(
            call,
            2);

        output.WriteByte(
            (byte)type);

        EncodeExpression(
            output,
            call.Arguments[0]);

        EncodeExpression(
            output,
            call.Arguments[1]);
    }

    // ============================================================
    // NESTED STRING
    // ============================================================

    private static void EncodeNestedString(
        Stream output,
        IReadOnlyList<ReadableNode> nodes)
    {
        output.WriteByte(
            (byte)DecodeExpressionType.Decode);

        using var nested =
            new MemoryStream();

        EncodeNodes(
            nested,
            nodes);

        var data =
            nested.ToArray();

        WriteInteger(
            output,
            data.Length);

        output.Write(
            data,
            0,
            data.Length);
    }

    private static string NodesToExpression(
        IReadOnlyList<ReadableNode> nodes)
    {
        // Обычный случай:
        //
        // <Value>IntegerParameter(2)</Value>
        //
        // => IntegerParameter(2)
        //

        if (nodes.Count == 1 &&
            nodes[0] is TextNode text)
        {
            return text.Text.Trim();
        }

        // Если внутри expression есть настоящий TagNode,
        // это уже не простой expression.
        //
        // Сохраняем его как бинарное nested expression.
        using var ms =
            new MemoryStream();

        EncodeNodes(
            ms,
            nodes);

        // ВАЖНО:
        //
        // Этот путь нужен только для редких сложных случаев.
        // Для обычных Value/Highlight decoder всегда выдаёт
        // TextNode с expression.
        //

        return Convert.ToHexString(
            ms.ToArray());
    }

    // ============================================================
    // INTEGER
    // ============================================================

    private static void EncodeIntegerExpression(
        Stream output,
        int value)
    {
        if (value >= 0 &&
            value <= 206)
        {
            output.WriteByte(
                (byte)(value + 1));

            return;
        }

        if (value >= 0 &&
            value <= byte.MaxValue)
        {
            output.WriteByte(
                (byte)DecodeExpressionType.Byte);

            output.WriteByte(
                (byte)value);

            return;
        }

        if (value >= 0 &&
            value <= 0xFFFF)
        {
            output.WriteByte(
                (byte)DecodeExpressionType.Int16_1);

            WriteUInt16(
                output,
                (ushort)value);

            return;
        }

        if (value >= 0 &&
            value <= 0xFFFFFF)
        {
            output.WriteByte(
                (byte)DecodeExpressionType.Int24);

            WriteUInt24(
                output,
                value);

            return;
        }

        output.WriteByte(
            (byte)DecodeExpressionType.Int32);

        WriteInt32(
            output,
            value);
    }

    private static void WriteInteger(
        Stream output,
        int value)
    {
        if (value < 0)
        {
            throw new InvalidDataException(
                $"Negative integer: {value}");
        }

        if (value <= 238)
        {
            output.WriteByte(
                (byte)(value + 1));

            return;
        }

        if (value <= byte.MaxValue)
        {
            output.WriteByte(
                (byte)IntegerType.Byte);

            output.WriteByte(
                (byte)value);

            return;
        }

        if (value <= 0xFFFF)
        {
            output.WriteByte(
                (byte)IntegerType.Int16);

            WriteUInt16(
                output,
                (ushort)value);

            return;
        }

        if (value <= 0xFFFFFF)
        {
            output.WriteByte(
                (byte)IntegerType.Int24);

            WriteUInt24(
                output,
                value);

            return;
        }

        output.WriteByte(
            (byte)IntegerType.Int32);

        WriteInt32(
            output,
            value);
    }

    // ============================================================
    // TAG WRITER
    // ============================================================

    private static void WriteTag(
        Stream output,
        TagType tag,
        byte[] payload)
    {
        output.WriteByte(
            TagStartMarker);

        output.WriteByte(
            (byte)tag);

        WriteInteger(
            output,
            payload.Length);

        if (payload.Length > 0)
        {
            output.Write(
                payload,
                0,
                payload.Length);
        }

        output.WriteByte(
            TagEndMarker);
    }

    // ============================================================
    // PARSING HELPERS
    // ============================================================

    private static bool LooksLikeCall(
        string value)
    {
        var open =
            value.IndexOf('(');

        return open > 0 &&
               value.EndsWith(")");
    }

    private static bool TryParseInteger(
        string value,
        out int result)
    {
        return int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out result);
    }

    private static bool TryParseTopLevelParameter(
        string value,
        out int result)
    {
        result = 0;

        const string prefix =
            "TopLevelParameter(";

        if (!value.StartsWith(prefix) ||
            !value.EndsWith(")"))
        {
            return false;
        }

        var inner =
            value[
                prefix.Length..^1];

        return int.TryParse(
            inner,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out result);
    }

    private static ExpressionCall ParseCall(
        string expression)
    {
        var open =
            expression.IndexOf('(');

        if (open <= 0 ||
            !expression.EndsWith(")"))
        {
            throw new InvalidDataException(
                $"Invalid expression: {expression}");
        }

        var name =
            expression[..open];

        var argsText =
            expression[
                (open + 1)..^1];

        return new ExpressionCall(
            name,
            SplitArguments(argsText));
    }

    private static List<string> SplitArguments(
        string value)
    {
        var result =
            new List<string>();

        if (string.IsNullOrWhiteSpace(value))
            return result;

        var start = 0;
        var depth = 0;

        for (var i = 0;
             i < value.Length;
             i++)
        {
            switch (value[i])
            {
                case '(':
                    depth++;
                    break;

                case ')':
                    depth--;
                    break;

                case ',' when depth == 0:
                    result.Add(
                        value[
                            start..i]
                        .Trim());

                    start =
                        i + 1;

                    break;
            }
        }

        result.Add(
            value[start..]
                .Trim());

        return result;
    }

    private static (
        IReadOnlyList<ReadableNode> TruePart,
        IReadOnlyList<ReadableNode>? FalsePart)
        SplitElse(
            IReadOnlyList<ReadableNode> nodes)
    {
        var index = -1;

        for (var i = 0;
             i < nodes.Count;
             i++)
        {
            if (nodes[i] is TagNode
                {
                    Name: "Else",
                    IsSelfClosing: true
                })
            {
                if (index != -1)
                {
                    throw new InvalidDataException(
                        "Multiple <Else/> tags.");
                }

                index = i;
            }
        }

        if (index == -1)
            return (
                nodes,
                null);

        return (
            nodes
                .Take(index)
                .ToList(),

            nodes
                .Skip(index + 1)
                .ToList());
    }

    // ============================================================
    // RAW HEX HELPERS
    // ============================================================

    private static bool TryGetSingleTextChild(
        TagNode tag,
        out string text)
    {
        text = string.Empty;

        if (tag.Children.Count != 1)
            return false;

        if (tag.Children[0] is not TextNode textNode)
            return false;

        text =
            textNode.Text.Trim();

        return true;
    }

    private static bool IsHex(
        string value)
    {
        value = value.Trim();

        if (value.Length == 0 ||
            value.Length % 2 != 0)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        return true;
    }

    private static byte[] ParseHex(
        string value)
    {
        value =
            value.Trim();

        if (value.Length % 2 != 0)
        {
            throw new InvalidDataException(
                $"Invalid hex string: {value}");
        }

        var result =
            new byte[value.Length / 2];

        for (var i = 0;
             i < result.Length;
             i++)
        {
            if (!byte.TryParse(
                    value.AsSpan(i * 2, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var b))
            {
                throw new InvalidDataException(
                    $"Invalid hex string: {value}");
            }

            result[i] = b;
        }

        return result;
    }

    // ============================================================
    // VALIDATION
    // ============================================================

    private static void RequireArguments(
        TagNode tag,
        int count)
    {
        if (tag.Arguments.Count != count)
        {
            throw new InvalidDataException(
                $"<{tag.Name}> expects {count} arguments, " +
                $"got {tag.Arguments.Count}.");
        }
    }

    private static void RequireArgumentCount(
        ExpressionCall call,
        int count)
    {
        if (call.Arguments.Count != count)
        {
            throw new InvalidDataException(
                $"{call.Name} expects {count} arguments, " +
                $"got {call.Arguments.Count}.");
        }
    }

    // ============================================================
    // BINARY WRITERS
    // ============================================================

    private static void WriteUInt16(
        Stream output,
        ushort value)
    {
        output.WriteByte(
            (byte)(value >> 8));

        output.WriteByte(
            (byte)value);
    }

    private static void WriteUInt24(
        Stream output,
        int value)
    {
        output.WriteByte(
            (byte)(value >> 16));

        output.WriteByte(
            (byte)(value >> 8));

        output.WriteByte(
            (byte)value);
    }

    private static void WriteInt32(
        Stream output,
        int value)
    {
        output.WriteByte(
            (byte)(value >> 24));

        output.WriteByte(
            (byte)(value >> 16));

        output.WriteByte(
            (byte)(value >> 8));

        output.WriteByte(
            (byte)value);
    }

    private sealed record ExpressionCall(
        string Name,
        List<string> Arguments);
}