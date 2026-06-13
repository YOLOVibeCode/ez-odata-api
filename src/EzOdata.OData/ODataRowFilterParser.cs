using System.Text.RegularExpressions;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core.Policy;
using EzOdata.Core.Query;
using EzOdata.Core.Schema;
using Microsoft.OData;
using Microsoft.OData.UriParser;

namespace EzOdata.OData;

/// <summary>
/// Binds role row-filter parsing (OData $filter grammar, spec 08 §5.4) to a service
/// schema + identity, substituting @identity.* claims as typed literals first.
/// Fails closed: unresolvable claims or unparsable filters throw RowFilterException.
/// </summary>
public sealed class ODataRowFilterParser
{
    private static readonly Regex ClaimPattern = new(
        @"@identity\.([A-Za-z0-9_.]+)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private readonly EdmModelFactory _models;

    public ODataRowFilterParser(EdmModelFactory models) => _models = models;

    public RowFilterParser Bind(
        string serviceName, SchemaSnapshot schema, string schemaVersion, RequestIdentity identity)
    {
        return (table, rowFilter) =>
        {
            var substituted = ClaimPattern.Replace(rowFilter, match =>
            {
                var claim = match.Groups[1].Value;
                if (!identity.Claims.TryGetValue(claim, out var value))
                {
                    throw new RowFilterException($"Row filter references missing identity claim '{claim}'.");
                }

                // Numbers pass through bare; everything else becomes a quoted string literal.
                return long.TryParse(value, out _) || decimal.TryParse(value, out _)
                    ? value
                    : "'" + value.Replace("'", "''") + "'";
            });

            var model = _models.GetOrBuild(serviceName, schemaVersion, schema);

            try
            {
                var parser = new ODataUriParser(
                    model, new Uri($"{table}?$filter={Uri.EscapeDataString(substituted)}", UriKind.Relative));
                var clause = parser.ParseFilter()
                    ?? throw new RowFilterException("Row filter parsed to nothing.");
                return ODataAstTranslator.TranslateFilter(clause);
            }
            catch (RowFilterException)
            {
                throw;
            }
            catch (Exception ex) when (ex is ODataException or NotSupportedQueryException or QueryValidationException)
            {
                throw new RowFilterException($"Invalid row filter '{rowFilter}': {ex.Message}", ex);
            }
        };
    }
}
