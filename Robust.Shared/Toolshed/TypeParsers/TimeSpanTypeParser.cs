using System;
using Robust.Shared.Console;
using Robust.Shared.Toolshed.Syntax;

namespace Robust.Shared.Toolshed.TypeParsers;

public sealed class TimeSpanTypeParser : TypeParser<TimeSpan>
{
    public override bool TryParse(ParserContext ctx, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        var span = ctx.GetWord(ParserContext.IsToken);

        return span is not null && TimeSpan.TryParse(span, out result);
    }

    public override CompletionResult? TryAutocomplete(ParserContext ctx, CommandArgument? arg)
    {
        return CompletionResult.FromHint(GetArgHint(arg));
    }
}
