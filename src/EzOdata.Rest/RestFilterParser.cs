using System.Globalization;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core;
using EzOdata.Core.Query;

namespace EzOdata.Rest;

/// <summary>
/// The small SQL-like filter grammar (spec 06 §5), parsed to the shared FilterNode IR —
/// never concatenated into SQL. Identifiers are validated against the schema downstream.
///
/// expr      := orExpr
/// orExpr    := andExpr ("or" andExpr)*
/// andExpr   := term ("and" term)*
/// term      := "not"? (comparison | "(" expr ")")
/// comparison:= field op value | field "in" "(" value ("," value)* ")"
///            | field ("is null" | "is not null") | field "like" string
/// </summary>
public sealed class RestFilterParser
{
    private readonly List<Token> _tokens;
    private int _pos;

    private RestFilterParser(List<Token> tokens) => _tokens = tokens;

    public static FilterNode Parse(string input)
    {
        var parser = new RestFilterParser(Tokenize(input));
        var node = parser.ParseOr();
        parser.Expect(TokenKind.End);
        return node;
    }

    private FilterNode ParseOr()
    {
        var left = ParseAnd();
        while (TryConsumeKeyword("or"))
        {
            left = new LogicalNode(LogicalOp.Or, [left, ParseAnd()]);
        }

        return left;
    }

    private FilterNode ParseAnd()
    {
        var left = ParseTerm();
        while (TryConsumeKeyword("and"))
        {
            left = new LogicalNode(LogicalOp.And, [left, ParseTerm()]);
        }

        return left;
    }

    private FilterNode ParseTerm()
    {
        if (TryConsumeKeyword("not"))
        {
            return new NotNode(ParseTerm());
        }

        if (Current.Kind == TokenKind.LParen)
        {
            Advance();
            var inner = ParseOr();
            Expect(TokenKind.RParen);
            return inner;
        }

        return ParseComparison();
    }

    private FilterNode ParseComparison()
    {
        var field = ParseFieldRef();

        // is null / is not null
        if (TryConsumeKeyword("is"))
        {
            var negated = TryConsumeKeyword("not");
            ExpectKeyword("null");
            return new ComparisonNode(field, negated ? ComparisonOp.Ne : ComparisonOp.Eq, ConstantValue.Null);
        }

        // in (...)
        if (TryConsumeKeyword("in"))
        {
            Expect(TokenKind.LParen);
            var values = new List<ConstantValue> { new(ParseLiteral()) };
            while (Current.Kind == TokenKind.Comma)
            {
                Advance();
                values.Add(new ConstantValue(ParseLiteral()));
            }

            Expect(TokenKind.RParen);
            return new InNode(field, values);
        }

        // like 'pattern' → contains-style via FunctionNode using raw LIKE semantics
        if (TryConsumeKeyword("like"))
        {
            var pattern = ParseLiteral();
            return new FunctionNode(FilterFunction.Contains,
                [new FieldArg(field), new ConstantArg(new ConstantValue(StripLikeWildcards(pattern)))]);
        }

        var op = ParseOperator();

        // contains / starts with / ends with → functions
        if (op is FilterFunction fn)
        {
            var value = ParseLiteral();
            return new FunctionNode(fn, [new FieldArg(field), new ConstantArg(new ConstantValue(value))]);
        }

        var comparison = (ComparisonOp)op!;
        return new ComparisonNode(field, comparison, new ConstantValue(ParseLiteral()));
    }

    private object ParseOperator()
    {
        // returns ComparisonOp or FilterFunction
        if (Current.Kind == TokenKind.Operator)
        {
            var symbol = Current.Text;
            Advance();
            return symbol switch
            {
                "=" => ComparisonOp.Eq,
                "!=" or "<>" => ComparisonOp.Ne,
                ">" => ComparisonOp.Gt,
                ">=" => ComparisonOp.Ge,
                "<" => ComparisonOp.Lt,
                "<=" => ComparisonOp.Le,
                _ => throw Error($"Unknown operator '{symbol}'."),
            };
        }

        if (Current.Kind == TokenKind.Keyword)
        {
            var word = Current.Text.ToLowerInvariant();
            if (word == "contains") { Advance(); return FilterFunction.Contains; }
            if (word == "starts")
            {
                Advance();
                ExpectKeyword("with");
                return FilterFunction.StartsWith;
            }

            if (word == "ends")
            {
                Advance();
                ExpectKeyword("with");
                return FilterFunction.EndsWith;
            }
        }

        throw Error($"Expected a comparison operator near '{Current.Text}'.");
    }

    private FieldRef ParseFieldRef()
    {
        var path = new List<string> { ExpectIdentifier() };
        while (Current.Kind == TokenKind.Dot)
        {
            Advance();
            path.Add(ExpectIdentifier());
        }

        if (path.Count > 2)
        {
            throw new NotSupportedQueryException("REST filter navigation paths are limited to depth 1.");
        }

        return new FieldRef(path);
    }

    private object? ParseLiteral()
    {
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.String:
                Advance();
                return token.Text;
            case TokenKind.Number:
                Advance();
                // Box each branch independently: a conditional expression would unify
                // long and decimal to decimal, mistyping integers.
                return token.Text.Contains('.')
                    ? (object)decimal.Parse(token.Text, CultureInfo.InvariantCulture)
                    : (object)long.Parse(token.Text, CultureInfo.InvariantCulture);
            case TokenKind.Keyword when token.Text.Equals("true", StringComparison.OrdinalIgnoreCase):
                Advance();
                return true;
            case TokenKind.Keyword when token.Text.Equals("false", StringComparison.OrdinalIgnoreCase):
                Advance();
                return false;
            case TokenKind.Keyword when token.Text.Equals("null", StringComparison.OrdinalIgnoreCase):
                Advance();
                return null;
            default:
                throw Error($"Expected a literal value near '{token.Text}'.");
        }
    }

    private static string StripLikeWildcards(object? pattern) =>
        (pattern?.ToString() ?? "").Trim('%');

    // ---- token helpers ----

    private Token Current => _tokens[_pos];

    private void Advance() => _pos++;

    private bool TryConsumeKeyword(string keyword)
    {
        if (Current.Kind == TokenKind.Keyword && Current.Text.Equals(keyword, StringComparison.OrdinalIgnoreCase))
        {
            Advance();
            return true;
        }

        return false;
    }

    private void ExpectKeyword(string keyword)
    {
        if (!TryConsumeKeyword(keyword)) throw Error($"Expected '{keyword}'.");
    }

    private string ExpectIdentifier()
    {
        if (Current.Kind != TokenKind.Identifier) throw Error($"Expected a field name near '{Current.Text}'.");
        var text = Current.Text;
        Advance();
        return text;
    }

    private void Expect(TokenKind kind)
    {
        if (Current.Kind != kind) throw Error($"Expected {kind} near '{Current.Text}'.");
        if (kind != TokenKind.End) Advance();
    }

    private static QueryValidationException Error(string message) =>
        new(ErrorCodes.ValidationBadFilter, message);

    // ---- tokenizer ----

    private enum TokenKind { Identifier, Keyword, Operator, String, Number, LParen, RParen, Comma, Dot, End }

    private readonly record struct Token(TokenKind Kind, string Text);

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "not", "in", "is", "null", "like", "true", "false",
        "contains", "starts", "ends", "with",
    };

    private static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < input.Length)
        {
            var c = input[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            switch (c)
            {
                case '(': tokens.Add(new Token(TokenKind.LParen, "(")); i++; continue;
                case ')': tokens.Add(new Token(TokenKind.RParen, ")")); i++; continue;
                case ',': tokens.Add(new Token(TokenKind.Comma, ",")); i++; continue;
                case '.': tokens.Add(new Token(TokenKind.Dot, ".")); i++; continue;
            }

            if (c == '\'')
            {
                var sb = new System.Text.StringBuilder();
                i++;
                while (i < input.Length)
                {
                    if (input[i] == '\'')
                    {
                        if (i + 1 < input.Length && input[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                        i++;
                        break;
                    }

                    sb.Append(input[i]);
                    i++;
                }

                tokens.Add(new Token(TokenKind.String, sb.ToString()));
                continue;
            }

            if (c is '=' or '!' or '<' or '>')
            {
                var op = c.ToString();
                if (i + 1 < input.Length && input[i + 1] == '=') { op += "="; i += 2; }
                else if (c == '<' && i + 1 < input.Length && input[i + 1] == '>') { op = "<>"; i += 2; }
                else i++;
                tokens.Add(new Token(TokenKind.Operator, op));
                continue;
            }

            if (char.IsDigit(c) || (c == '-' && i + 1 < input.Length && char.IsDigit(input[i + 1])))
            {
                var start = i;
                i++;
                while (i < input.Length && (char.IsDigit(input[i]) || input[i] == '.')) i++;
                tokens.Add(new Token(TokenKind.Number, input.Substring(start, i - start)));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] == '_')) i++;
                var word = input.Substring(start, i - start);
                tokens.Add(new Token(Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Identifier, word));
                continue;
            }

            throw Error($"Unexpected character '{c}'.");
        }

        tokens.Add(new Token(TokenKind.End, ""));
        return tokens;
    }
}
