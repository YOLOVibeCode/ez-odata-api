using System.Text;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core;
using EzOdata.Core.Policy;
using EzOdata.Core.Protocol;
using EzOdata.Core.Query;
using EzOdata.Core.Schema;
using EzOdata.Core.Services;
using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Csdl;
using Microsoft.OData.UriParser;
using Microsoft.OData.UriParser.Aggregation;
using ApplyClause = EzOdata.Core.Query.ApplyClause;

namespace EzOdata.OData;

/// <summary>
/// The host-agnostic OData engine (spec 02 §5, 05): one instance serves all services.
/// Every operation flows through the policy engine (spec 08 §4) — requests parse
/// against the full model, restricted fields fail with 403, hidden tables behave as
/// nonexistent, and $metadata/service documents are identity-trimmed (OD-8).
/// Writes land in Phase 3; $expand/$apply in Phase 4.
/// </summary>
public sealed partial class ODataRequestHandler
{
    private readonly IServiceRuntimeResolver _services;
    private readonly IConnectorRegistry _connectors;
    private readonly EdmModelFactory _models;
    private readonly SkipTokenCodec _skipTokens;
    private readonly PolicyEngine _policy;
    private readonly IPolicySource _policySource;
    private readonly ODataRowFilterParser _rowFilterParser;

    public ODataRequestHandler(
        IServiceRuntimeResolver services,
        IConnectorRegistry connectors,
        EdmModelFactory models,
        SkipTokenCodec skipTokens,
        PolicyEngine policy,
        IPolicySource policySource,
        ODataRowFilterParser rowFilterParser)
    {
        _services = services;
        _connectors = connectors;
        _models = models;
        _skipTokens = skipTokens;
        _policy = policy;
        _policySource = policySource;
        _rowFilterParser = rowFilterParser;
    }

    public async Task<EngineResponse> HandleAsync(string serviceName, EngineRequest request, CancellationToken ct)
    {
        var runtime = await _services.ResolveAsync(serviceName, ct);
        if (runtime is null)
        {
            return Error(404, "NotFound", $"Unknown service '{serviceName}'.");
        }

        if (runtime.Status == ServiceStatus.Disabled)
        {
            return Error(503, "ServiceUnavailable", "Service is disabled.");
        }

        if (runtime.Status is not ServiceStatus.Active and not ServiceStatus.Refreshing)
        {
            return Error(503, "ServiceUnavailable",
                "Service schema is not ready yet. Introspection may still be running.");
        }

        if (!_connectors.TryGet(runtime.ConnectorType, out var connector))
        {
            return Error(503, "ServiceUnavailable", $"Connector '{runtime.ConnectorType}' is not available.");
        }

        // ---- security context (spec 08 §4) ----
        var roleRules = await _policySource.GetRoleRulesAsync(request.Identity.RoleIds, ct);
        var parseRowFilter = _rowFilterParser.Bind(runtime.Name, runtime.Schema, runtime.SchemaVersion, request.Identity);
        var security = new SecurityContext(request.Identity, roleRules, parseRowFilter, runtime);

        var model = _models.GetOrBuild(runtime.Name, runtime.SchemaVersion, runtime.Schema);
        var isRead = string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (!isRead)
            {
                return await HandleWriteAsync(model, runtime, connector, request, security, ct);
            }

            return request.Path switch
            {
                "" => WriteServiceDocument(model, request.ServiceRoot, runtime, security),
                "$metadata" => await WriteTrimmedMetadataAsync(runtime, security, request, ct),
                "openapi.json" => await WriteOpenApiAsync(runtime, security, request, EzOdata.Docs.ApiDialect.ODataV4, ct),
                _ => await HandleQueryAsync(model, runtime, connector, request, security, ct),
            };
        }
        catch (QueryPolicyRewriter.FieldDeniedException ex)
        {
            return Error(403, ErrorCodes.ForbiddenFieldDenied, ex.Message);
        }
        catch (PayloadBinder.BindException ex)
        {
            return Error(400, ex.ErrorCode, ex.Message);
        }
        catch (ConnectorException ex)
        {
            // Error taxonomy → HTTP (spec 04 §8)
            var status = ex.ErrorCode switch
            {
                ErrorCodes.ConflictUniqueViolation or ErrorCodes.ConflictForeignKeyViolation => 409,
                ErrorCodes.ValidationNotNullViolation or ErrorCodes.ValidationValueTooLong
                    or ErrorCodes.ValidationInvalidValue => 400,
                ErrorCodes.ForbiddenRowFilter => 403,
                ErrorCodes.UpstreamPermissionDenied => 502,
                ErrorCodes.UpstreamTimeout => 504,
                ErrorCodes.UpstreamUnavailable => 503,
                _ => 500,
            };
            return Error(status, ex.ErrorCode, ex.Message);
        }
        catch (ODataException ex)
        {
            return Error(400, ErrorCodes.ValidationBadFilter, ex.Message);
        }
        catch (QueryValidationException ex)
        {
            return Error(400, ex.ErrorCode, ex.Message);
        }
        catch (NotSupportedQueryException ex)
        {
            return Error(501, "NotImplemented", ex.Message);
        }
    }

    private sealed record SecurityContext(
        RequestIdentity Identity,
        IReadOnlyList<RoleRuleSet> RoleRules,
        RowFilterParser ParseRowFilter,
        ServiceRuntime Runtime)
    {
        public PolicyDecision Authorize(TableModel table, Verb verb, PolicyEngine engine) =>
            engine.Authorize(
                Identity, RoleRules, Runtime.Name, table.ExposedName, verb,
                table.Columns.Select(c => c.ExposedName).ToList(), ParseRowFilter);
    }

    // ---- routes ----

    private EngineResponse WriteServiceDocument(
        IEdmModel model, Uri serviceRoot, ServiceRuntime runtime, SecurityContext security)
    {
        var visible = SnapshotTrimmer.Trim(
            runtime.Schema, security.Identity, security.RoleRules, runtime.Name, _policy, security.ParseRowFilter);

        var document = new ODataServiceDocument
        {
            EntitySets = visible.Tables
                .Select(t => new ODataEntitySetInfo { Name = t.ExposedName, Url = new Uri(t.ExposedName, UriKind.Relative) })
                .ToList(),
        };

        using var stream = new MemoryStream();
        var message = new InMemoryResponseMessage(stream);
        var settings = new ODataMessageWriterSettings
        {
            BaseUri = serviceRoot,
            ODataUri = new ODataUri { ServiceRoot = serviceRoot },
            EnableMessageStreamDisposal = false,
        };
        using (var writer = new ODataMessageWriter(message, settings, model))
        {
            writer.WriteServiceDocument(document);
        }

        return Json(200, stream.ToArray());
    }

    private async Task<EngineResponse> WriteTrimmedMetadataAsync(
        ServiceRuntime runtime, SecurityContext security, EngineRequest request, CancellationToken ct)
    {
        // Per-identity model trimming (OD-8): cache key = schema + policy + roles.
        var policyVersion = await _policySource.GetPolicyVersionAsync(ct);
        var trimmed = SnapshotTrimmer.Trim(
            runtime.Schema, security.Identity, security.RoleRules, runtime.Name, _policy, security.ParseRowFilter);
        var roleKey = security.RoleRules.Any(r => r.BypassDataRules)
            ? "bypass"
            : string.Join(",", security.Identity.RoleIds.OrderBy(r => r));
        var versionKey = $"{runtime.SchemaVersion}|{policyVersion}|{roleKey}";
        var model = _models.GetOrBuild(runtime.Name + ":trimmed", versionKey, trimmed);

        return WriteMetadata(model, versionKey, request);
    }

    private async Task<EngineResponse> WriteOpenApiAsync(
        ServiceRuntime runtime, SecurityContext security, EngineRequest request,
        EzOdata.Docs.ApiDialect dialect, CancellationToken ct)
    {
        var policyVersion = await _policySource.GetPolicyVersionAsync(ct);
        var roleKey = security.RoleRules.Any(r => r.BypassDataRules)
            ? "bypass"
            : string.Join(",", security.Identity.RoleIds.OrderBy(r => r));
        var etag = EzOdata.Docs.OpenApiGenerator.ComputeETag($"{runtime.SchemaVersion}|{roleKey}", policyVersion, dialect);

        if (request.Headers.TryGetValue("If-None-Match", out var inm) && inm == etag)
        {
            return new EngineResponse { StatusCode = 304, Headers = new Dictionary<string, string> { ["ETag"] = etag } };
        }

        var trimmed = SnapshotTrimmer.Trim(
            runtime.Schema, security.Identity, security.RoleRules, runtime.Name, _policy, security.ParseRowFilter);
        var generator = new EzOdata.Docs.OpenApiGenerator();
        var json = generator.Generate(trimmed, dialect, runtime.Name, runtime.Name, runtime.SchemaVersion, request.ServiceRoot);

        return new EngineResponse
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(json),
            ContentType = "application/json",
            Headers = new Dictionary<string, string> { ["ETag"] = etag },
        };
    }

    /// <summary>Used by the REST docs route to render the REST-dialect OpenAPI for a service.</summary>
    public async Task<EngineResponse> HandleOpenApiAsync(
        string serviceName, EngineRequest request, EzOdata.Docs.ApiDialect dialect, CancellationToken ct)
    {
        var runtime = await _services.ResolveAsync(serviceName, ct);
        if (runtime is null || runtime.Status is ServiceStatus.Disabled or ServiceStatus.Pending or ServiceStatus.Failed)
        {
            return Error(404, "NotFound", $"Service '{serviceName}' is not available.");
        }

        var roleRules = await _policySource.GetRoleRulesAsync(request.Identity.RoleIds, ct);
        var parseRowFilter = _rowFilterParser.Bind(runtime.Name, runtime.Schema, runtime.SchemaVersion, request.Identity);
        var security = new SecurityContext(request.Identity, roleRules, parseRowFilter, runtime);
        return await WriteOpenApiAsync(runtime, security, request, dialect, ct);
    }

    private static EngineResponse WriteMetadata(IEdmModel model, string schemaVersion, EngineRequest request)
    {
        var etag = $"W/\"{Sha(schemaVersion)}\"";
        if (request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch) && ifNoneMatch == etag)
        {
            return new EngineResponse
            {
                StatusCode = 304,
                Headers = new Dictionary<string, string> { ["ETag"] = etag },
            };
        }

        using var stream = new MemoryStream();
        using (var xmlWriter = System.Xml.XmlWriter.Create(stream, new System.Xml.XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
        }))
        {
            if (!CsdlWriter.TryWriteCsdl(model, xmlWriter, CsdlTarget.OData, out var errors))
            {
                var detail = string.Join("; ", errors.Select(e => e.ErrorMessage));
                return Error(500, ErrorCodes.InternalUnmapped, $"CSDL generation failed: {detail}");
            }
        }

        return new EngineResponse
        {
            StatusCode = 200,
            Body = stream.ToArray(),
            ContentType = "application/xml",
            Headers = new Dictionary<string, string>
            {
                ["OData-Version"] = "4.0",
                ["ETag"] = etag,
            },
        };
    }

    private async Task<EngineResponse> HandleQueryAsync(
        IEdmModel model, ServiceRuntime runtime, ConnectorDescriptor connector,
        EngineRequest request, SecurityContext security, CancellationToken ct)
    {
        var relative = request.Path + (request.QueryString.Length > 0 ? "?" + request.QueryString : "");
        var parser = new ODataUriParser(model, new Uri(relative, UriKind.Relative))
        {
            Resolver = new ODataUriResolver { EnableCaseInsensitive = false },
        };

        ODataPath path;
        try
        {
            path = parser.ParsePath();
        }
        catch (ODataUnrecognizedPathException)
        {
            return Error(404, "NotFound", $"Resource '{request.Path}' not found.");
        }

        var segments = path.ToList();
        if (segments.Count == 0 || segments[0] is not EntitySetSegment entitySetSegment)
        {
            return Error(501, "NotImplemented",
                "Only entity set paths are supported in this milestone (spec 05 §2).");
        }

        var entitySet = entitySetSegment.EntitySet;
        var table = runtime.Schema.FindTable(entitySet.Name)
                    ?? throw new QueryValidationException(ErrorCodes.ValidationInvalidValue,
                        $"Entity set '{entitySet.Name}' has no backing table.");

        var isCount = segments.Count == 2 && segments[1] is CountSegment;
        var keySegment = segments.Count == 2 ? segments[1] as KeySegment : null;
        if (segments.Count > 2 || (segments.Count == 2 && !isCount && keySegment is null))
        {
            return Error(501, "NotImplemented",
                "Property paths, navigations, and $value land in later milestones (spec 05 §2).");
        }

        // ---- build IR ----
        var selectExpandClause = parser.ParseSelectAndExpand();
        var select = TranslateSelect(selectExpandClause);
        var expands = ODataAstTranslator.TranslateExpand(
            selectExpandClause, runtime.Options.MaxExpandDepth, runtime.Options.MaxExpandWidth);
        var filter = parser.ParseFilter() is { } filterClause ? ODataAstTranslator.TranslateFilter(filterClause) : null;
        var orderBy = ODataAstTranslator.TranslateOrderBy(parser.ParseOrderBy());
        var top = checked((int?)parser.ParseTop());
        var skip = checked((int?)parser.ParseSkip());
        var countRequested = parser.ParseCount() == true;

        // $search / $apply apply to collection requests only (not by-key or $count); ODL
        // rejects them on single-entity paths, so we only parse them here.
        if (keySegment is null && !isCount)
        {
            if (parser.ParseSearch() is { } searchClause)
            {
                if (!runtime.Options.EnableSearch)
                {
                    return Error(501, "NotImplemented", "$search is not enabled for this service.");
                }

                var searchFilter = BuildSearchFilter(searchClause, table);
                if (searchFilter is not null)
                {
                    filter = filter is null ? searchFilter : new LogicalNode(LogicalOp.And, [filter, searchFilter]);
                }
            }

            if (parser.ParseApply() is { } applyClause)
            {
                return await HandleApplyAsync(runtime, connector, security, table, applyClause, filter, ct);
            }
        }

        if (parser.ParseSkipToken() is { } token)
        {
            if (!_skipTokens.TryDecode(token, out var tokenSkip))
            {
                return Error(400, ErrorCodes.ValidationInvalidValue, "Invalid $skiptoken.");
            }

            skip = tokenSkip;
        }

        // ---- authorization (spec 08 §4): hidden → 404, denied → 403, allowed → rewrite ----
        var decision = security.Authorize(table, Verb.Get, _policy);
        if (!decision.Allowed)
        {
            return decision.Hidden
                ? Error(404, "NotFound", $"Resource '{table.ExposedName}' not found.")
                : Error(403, decision.DenialCode ?? "Forbidden", decision.DenialMessage ?? "Access denied.");
        }

        if (filter is not null && !decision.Bypass)
        {
            // Cross-table leak protection: nav paths and lambdas checked against their targets.
            CrossTableFilterValidator.Validate(filter, table, runtime.Schema,
                target => security.Authorize(target, Verb.Get, _policy));
        }

        if (keySegment is not null)
        {
            return await HandleByKeyAsync(model, runtime, connector, request, entitySet, table, keySegment, select, expands, security, path, decision, ct);
        }

        // Page-size policy (spec 05 §4.2): default page size; $top clamped to max.
        var effectiveTop = Math.Min(top ?? runtime.Options.DefaultPageSize, runtime.Options.MaxPageSize);

        // Ensure FK columns needed for $expand stitching are present even if $select omits them.
        var expandKeys = RequiredExpandKeys(table, expands, runtime.Schema);
        var augmentedSelect = AugmentSelect(select, expandKeys);

        var query = new QueryRequest
        {
            ServiceName = runtime.Name,
            Table = table.ExposedName,
            Filter = filter,
            OrderBy = orderBy,
            Select = augmentedSelect,
            Top = effectiveTop,
            Skip = skip,
            Count = countRequested,
        };

        var rewritten = QueryPolicyRewriter.Rewrite(query, decision, table);
        var masks = QueryPolicyRewriter.MasksToApply(query, decision);

        var execution = new QueryExecution(runtime.Connection, runtime.Schema, rewritten, new ExecutionOptions
        {
            CommandTimeoutSeconds = runtime.Options.CommandTimeoutSeconds,
            RowLimit = effectiveTop,
        });

        if (isCount)
        {
            var total = await connector.Reader.CountAsync(execution, ct);
            return new EngineResponse
            {
                StatusCode = 200,
                Body = Encoding.UTF8.GetBytes(total.ToString()),
                ContentType = "text/plain",
                Headers = ODataHeaders(),
            };
        }

        var result = await connector.Reader.QueryAsync(execution, ct);
        long? inlineCount = countRequested ? await connector.Reader.CountAsync(execution, ct) : null;

        ApplyMasks(result.Rows, masks);
        await ApplyExpandsAsync(runtime, connector, security, table, result.Rows, expands, ct);
        StripHelperColumns(result.Rows, select, expandKeys);

        Uri? nextLink = null;
        if (result.HasMore)
        {
            var nextToken = _skipTokens.Encode((skip ?? 0) + result.Rows.Count);
            nextLink = BuildNextLink(request, nextToken);
        }

        var body = ODataPayloadWriter.WriteResourceSet(
            model, entitySet, path, request.ServiceRoot, table, result.Rows, inlineCount, nextLink,
            NavResolver(runtime.Schema));
        return Json(200, body);
    }

    private static IReadOnlyList<string>? AugmentSelect(IReadOnlyList<string>? select, IReadOnlyList<string> extraKeys)
    {
        if (select is null || extraKeys.Count == 0) return select;

        var set = new List<string>(select);
        foreach (var key in extraKeys)
        {
            if (!set.Contains(key, StringComparer.Ordinal)) set.Add(key);
        }

        return set;
    }

    /// <summary>Removes FK columns added solely for expand stitching, unless the client selected them.</summary>
    private static void StripHelperColumns(
        IReadOnlyList<Row> rows, IReadOnlyList<string>? originalSelect, IReadOnlyList<string> expandKeys)
    {
        if (originalSelect is null || expandKeys.Count == 0) return;

        var toRemove = expandKeys.Where(k => !originalSelect.Contains(k, StringComparer.Ordinal)).ToList();
        if (toRemove.Count == 0) return;

        foreach (var row in rows)
        {
            foreach (var column in toRemove) row.Remove(column);
        }
    }

    private async Task<EngineResponse> HandleApplyAsync(
        ServiceRuntime runtime, ConnectorDescriptor connector, SecurityContext security,
        TableModel table, Microsoft.OData.UriParser.Aggregation.ApplyClause applyClause,
        FilterNode? filter, CancellationToken ct)
    {
        var decision = security.Authorize(table, Verb.Get, _policy);
        if (!decision.Allowed)
        {
            return decision.Hidden
                ? Error(404, "NotFound", $"Resource '{table.ExposedName}' not found.")
                : Error(403, decision.DenialCode ?? "Forbidden", decision.DenialMessage ?? "Access denied.");
        }

        var apply = ODataAstTranslator.TranslateApply(applyClause);
        var applyFilter = ODataAstTranslator.ExtractApplyFilter(applyClause);
        var combined = (filter, applyFilter) switch
        {
            (null, null) => null,
            (var f, null) => f,
            (null, var af) => af,
            var (f, af) => new LogicalNode(LogicalOp.And, [f!, af!]),
        };

        // Grouped/aggregated fields must be readable (no leaking a denied column via sum()).
        foreach (var field in apply.GroupBy.Concat(apply.Aggregations.Where(a => a.Field is not null).Select(a => a.Field!)))
        {
            if (decision.DeniedFields.Contains(field) || decision.MaskedFields.ContainsKey(field))
            {
                return Error(403, ErrorCodes.ForbiddenFieldDenied, $"Access to field '{field}' is denied.");
            }
        }

        if (decision.RowFilter is { } rowFilter)
        {
            combined = combined is null ? rowFilter : new LogicalNode(LogicalOp.And, [combined, rowFilter]);
        }

        var query = new QueryRequest
        {
            ServiceName = runtime.Name,
            Table = table.ExposedName,
            Filter = combined,
            Apply = apply,
        };

        var execution = new QueryExecution(runtime.Connection, runtime.Schema, query,
            new ExecutionOptions { CommandTimeoutSeconds = runtime.Options.CommandTimeoutSeconds });
        var result = await connector.Reader.QueryAsync(execution, ct);

        var body = WriteOpenEntitySet(runtime, table, apply, result.Rows);
        return Json(200, body);
    }

    private static byte[] WriteOpenEntitySet(
        ServiceRuntime runtime, TableModel table, ApplyClause apply, IReadOnlyList<Row> rows)
    {
        // $apply results are open/dynamic entities; serialize as a plain JSON OData payload.
        var values = rows.Select(row =>
        {
            var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var groupField in apply.GroupBy)
            {
                obj[groupField] = Normalize(row[groupField]);
            }

            foreach (var aggregate in apply.Aggregations)
            {
                obj[aggregate.Alias] = Normalize(row[aggregate.Alias]);
            }

            return obj;
        }).ToList();

        var payload = new Dictionary<string, object?>
        {
            ["@odata.context"] = $"{runtime.Name}/$metadata#{table.ExposedName}",
            ["value"] = values,
        };

        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    private static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        decimal d => d,
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        _ => value,
    };

    private static FilterNode? BuildSearchFilter(SearchClause searchClause, TableModel table)
    {
        // Term extraction: only simple single-term search supported in v1 (spec 05 §4.6).
        if (searchClause.Expression is not SearchTermNode term) return null;

        var searchable = table.Columns
            .Where(c => c.EdmType == "Edm.String" && (c.MaxLength is null or <= 1024))
            .ToList();
        if (searchable.Count == 0) return null;

        var clauses = searchable
            .Select(c => (FilterNode)new FunctionNode(FilterFunction.Contains,
                [new FieldArg(new FieldRef(c.ExposedName)), new ConstantArg(new ConstantValue(term.Text))]))
            .ToList();

        return clauses.Count == 1 ? clauses[0] : new LogicalNode(LogicalOp.Or, clauses);
    }

    private static ODataPayloadWriter.NavigationResolver NavResolver(SchemaSnapshot schema) =>
        (TableModel from, string navigation, out bool isCollection) =>
        {
            var toOne = from.ForeignKeys.FirstOrDefault(f => f.NavToOne == navigation);
            if (toOne is not null)
            {
                isCollection = false;
                return schema.FindTable(toOne.RefTable);
            }

            foreach (var candidate in schema.Tables)
            {
                if (candidate.ForeignKeys.Any(f => f.RefTable == from.ExposedName && f.NavToMany == navigation))
                {
                    isCollection = true;
                    return candidate;
                }
            }

            isCollection = false;
            return null;
        };

    /// <summary>
    /// FK columns the parent rows must carry for stitching (spec 04 §7.1). When $select
    /// omits them they are fetched anyway and stripped from the response afterward.
    /// </summary>
    private static IReadOnlyList<string> RequiredExpandKeys(
        TableModel table, IReadOnlyList<ExpandNode> expands, SchemaSnapshot schema)
    {
        var keys = new List<string>();
        foreach (var expand in expands)
        {
            var toOne = table.ForeignKeys.FirstOrDefault(f => f.NavToOne == expand.Navigation);
            if (toOne is not null)
            {
                keys.AddRange(toOne.Columns); // local FK columns
                continue;
            }

            var toMany = schema.Tables
                .SelectMany(t => t.ForeignKeys)
                .FirstOrDefault(f => f.RefTable == table.ExposedName && f.NavToMany == expand.Navigation);
            if (toMany is not null)
            {
                keys.AddRange(toMany.RefColumns); // referenced (usually PK) columns on the parent
            }
        }

        return keys.Distinct(StringComparer.Ordinal).ToList();
    }

    private async Task ApplyExpandsAsync(
        ServiceRuntime runtime, ConnectorDescriptor connector, SecurityContext security,
        TableModel table, IReadOnlyList<Row> rows, IReadOnlyList<ExpandNode> expands, CancellationToken ct)
    {
        if (expands.Count == 0) return;

        var executor = new ExpansionExecutor(
            connector.Reader, runtime.Connection, runtime.Schema,
            new ExecutionOptions { CommandTimeoutSeconds = runtime.Options.CommandTimeoutSeconds },
            runtime.Name,
            (childTable, _) =>
            {
                var decision = security.Authorize(childTable, Verb.Get, _policy);
                return new ExpansionExecutor.PolicyDecisionLite(
                    decision.Allowed, decision.Hidden, decision.DeniedFields, decision.MaskedFields, decision.RowFilter);
            });

        await executor.ExpandAsync(table, rows, expands, ct);
    }

    private async Task<EngineResponse> HandleByKeyAsync(
        IEdmModel model, ServiceRuntime runtime, ConnectorDescriptor connector, EngineRequest request,
        IEdmEntitySet entitySet, TableModel table, KeySegment keySegment,
        IReadOnlyList<string>? select, IReadOnlyList<ExpandNode> expands, SecurityContext security,
        ODataPath path, PolicyDecision decision, CancellationToken ct)
    {
        FilterNode? keyFilter = null;
        foreach (var pair in keySegment.Keys)
        {
            var comparison = new ComparisonNode(
                new FieldRef(pair.Key), ComparisonOp.Eq,
                new ConstantValue(ODataAstTranslator.NormalizeValue(pair.Value)));
            keyFilter = keyFilter is null
                ? comparison
                : new LogicalNode(LogicalOp.And, [keyFilter, comparison]);
        }

        var expandKeys = RequiredExpandKeys(table, expands, runtime.Schema);
        var query = new QueryRequest
        {
            ServiceName = runtime.Name,
            Table = table.ExposedName,
            Filter = keyFilter,
            Select = AugmentSelect(select, expandKeys),
            Top = 1,
        };

        // Row filter applies to by-key reads too: invisible rows are 404, never 403 (spec 08 §11.3)
        var rewritten = QueryPolicyRewriter.Rewrite(query, decision, table);
        var masks = QueryPolicyRewriter.MasksToApply(query, decision);

        var execution = new QueryExecution(runtime.Connection, runtime.Schema, rewritten, new ExecutionOptions
        {
            CommandTimeoutSeconds = runtime.Options.CommandTimeoutSeconds,
            RowLimit = 1,
        });

        var result = await connector.Reader.QueryAsync(execution, ct);
        if (result.Rows.Count == 0)
        {
            return Error(404, "NotFound", "Entity not found.");
        }

        ApplyMasks(result.Rows, masks);
        await ApplyExpandsAsync(runtime, connector, security, table, result.Rows, expands, ct);
        StripHelperColumns(result.Rows, select, expandKeys);

        var body = ODataPayloadWriter.WriteSingleResource(
            model, entitySet, path, request.ServiceRoot, table, result.Rows[0], NavResolver(runtime.Schema));
        return Json(200, body);
    }

    private static void ApplyMasks(IReadOnlyList<Row> rows, IReadOnlyDictionary<string, string> masks)
    {
        if (masks.Count == 0) return;

        foreach (var row in rows)
        {
            foreach (var mask in masks)
            {
                row.Set(mask.Key, mask.Value);
            }
        }
    }

    private static string Sha(string value)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
        var hex = new StringBuilder(16);
        for (var i = 0; i < 8; i++) hex.Append(bytes[i].ToString("x2"));
        return hex.ToString();
    }

    // ---- helpers ----

    private static IReadOnlyList<string>? TranslateSelect(SelectExpandClause? clause)
    {
        if (clause is null || clause.AllSelected) return null;

        var fields = new List<string>();
        foreach (var item in clause.SelectedItems)
        {
            if (item is PathSelectItem { SelectedPath: { } selectedPath }
                && selectedPath.FirstSegment is PropertySegment property)
            {
                fields.Add(property.Property.Name);
            }
            else if (item is not ExpandedNavigationSelectItem)
            {
                throw new NotSupportedQueryException("Only direct property names are supported in $select.");
            }
        }

        return fields;
    }

    private static Uri BuildNextLink(EngineRequest request, string nextToken)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.QueryString);
        query.Remove("$skiptoken");
        query["$skiptoken"] = nextToken;

        var builder = new UriBuilder(new Uri(request.ServiceRoot, request.Path))
        {
            Query = query.ToString(),
        };
        return builder.Uri;
    }

    private static Dictionary<string, string> ODataHeaders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["OData-Version"] = "4.0",
    };

    private static EngineResponse Json(int status, byte[] body) => new()
    {
        StatusCode = status,
        Body = body,
        ContentType = "application/json;odata.metadata=minimal",
        Headers = ODataHeaders(),
    };

    private static EngineResponse Error(int status, string code, string message) => new()
    {
        StatusCode = status,
        Body = ODataPayloadWriter.WriteError(code, message, status),
        ContentType = "application/json",
        Headers = ODataHeaders(),
    };
}
