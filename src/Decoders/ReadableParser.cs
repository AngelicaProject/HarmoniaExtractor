using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XivExdUnpacker.Decoders;

internal sealed class ReadableParser
{
    private readonly string _input;
    private int _position;

    public ReadableParser(string input)
    {
        _input = input;
    }

    public IReadOnlyList<ReadableNode> Parse()
    {
        var result = ParseNodes(null);

        if (_position != _input.Length)
        {
            throw Error(
                $"Unexpected character at position {_position}.");
        }

        return result;
    }

    private List<ReadableNode> ParseNodes(
        string? closingTag)
    {
        var result = new List<ReadableNode>();
        var text = new StringBuilder();

        while (_position < _input.Length)
        {
            if (_input[_position] != '<')
            {
                text.Append(_input[_position]);
                _position++;
                continue;
            }

            if (text.Length > 0)
            {
                result.Add(
                    new TextNode(
                        UnescapeText(text.ToString())));

                text.Clear();
            }

            if (StartsWith("</"))
            {
                if (closingTag == null)
                    throw Error("Unexpected closing tag.");

                var closeName =
                    ParseClosingTag();

                if (!string.Equals(
                        closeName,
                        closingTag,
                        StringComparison.Ordinal))
                {
                    throw Error(
                        $"Expected </{closingTag}>, " +
                        $"got </{closeName}>.");
                }

                return result;
            }

            var tag = ParseOpeningTag();

            if (tag.IsSelfClosing)
            {
                result.Add(tag);
                continue;
            }

            var children = ParseNodes(tag.Name);

            result.Add(
                tag with
                {
                    Children = children
                });
        }

        if (closingTag != null)
        {
            throw Error(
                $"Missing </{closingTag}>.");
        }

        return result;
    }

    private TagNode ParseOpeningTag()
    {
        Expect('<');

        var name = ReadIdentifier();

        var arguments = new List<string>();

        SkipWhitespace();

        if (Peek('('))
        {
            _position++;

            var argumentText =
                ReadBalanced('(', ')');

            arguments =
                SplitArguments(argumentText);
        }

        SkipWhitespace();

        if (Peek('/'))
        {
            _position++;
            Expect('>');

            return new TagNode(
                name,
                arguments,
                Array.Empty<ReadableNode>(),
                IsSelfClosing: true);
        }

        Expect('>');

        // <Else/> is handled as self-closing above.
        return new TagNode(
            name,
            arguments,
            Array.Empty<ReadableNode>());
    }

    private string ParseClosingTag()
    {
        Expect('<');
        Expect('/');

        var name = ReadIdentifier();

        SkipWhitespace();

        Expect('>');

        return name;
    }

    private string ReadIdentifier()
    {
        var start = _position;

        while (_position < _input.Length)
        {
            var c = _input[_position];

            if (char.IsLetterOrDigit(c) ||
                c == '_' ||
                c == '-')
            {
                _position++;
                continue;
            }

            break;
        }

        if (start == _position)
            throw Error("Expected identifier.");

        return _input[start.._position];
    }

    private string ReadBalanced(
        char opening,
        char closing)
    {
        var start = _position;
        var depth = 1;

        while (_position < _input.Length)
        {
            var c = _input[_position];

            if (c == opening)
                depth++;

            else if (c == closing)
            {
                depth--;

                if (depth == 0)
                {
                    var result =
                        _input[start.._position];

                    _position++;

                    return result;
                }
            }

            _position++;
        }

        throw Error(
            $"Missing closing '{closing}'.");
    }

    private static List<string> SplitArguments(
        string input)
    {
        var result = new List<string>();

        if (string.IsNullOrWhiteSpace(input))
            return result;

        var start = 0;
        var depth = 0;

        for (var i = 0; i < input.Length; i++)
        {
            switch (input[i])
            {
                case '(':
                    depth++;
                    break;

                case ')':
                    depth--;
                    break;

                case ',' when depth == 0:
                    result.Add(
                        input[start..i].Trim());

                    start = i + 1;
                    break;
            }
        }

        result.Add(
            input[start..].Trim());

        return result;
    }

    private static string UnescapeText(
        string text)
    {
        return text
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&amp;", "&");
    }

    private bool StartsWith(string value)
    {
        return _input.AsSpan(_position)
            .StartsWith(value.AsSpan());
    }

    private bool Peek(char value)
    {
        return _position < _input.Length &&
               _input[_position] == value;
    }

    private void Expect(char value)
    {
        if (!Peek(value))
        {
            throw Error(
                $"Expected '{value}'.");
        }

        _position++;
    }

    private void SkipWhitespace()
    {
        while (_position < _input.Length &&
               char.IsWhiteSpace(_input[_position]))
        {
            _position++;
        }
    }

    private InvalidDataException Error(
        string message)
    {
        return new InvalidDataException(
            $"{message} Position: {_position}.");
    }
}