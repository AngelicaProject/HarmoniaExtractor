using System.Collections.Generic;

namespace XivExdUnpacker.Decoders;

public abstract record ReadableNode;

public sealed record TextNode(
    string Text) : ReadableNode;

public sealed record TagNode(
    string Name,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<ReadableNode> Children,
    bool IsSelfClosing = false,
    bool IsClosing = false,
    string? RawContent = null) : ReadableNode;