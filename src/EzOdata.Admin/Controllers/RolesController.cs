using EzOdata.Core.Policy;
using EzOdata.Core.Schema;
using EzOdata.Data;
using EzOdata.Data.Entities;
using EzOdata.Data.Runtime;
using EzOdata.OData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EzOdata.Admin.Controllers;

public sealed record SimulateRequest(
    string ServiceName, string Table, string Verb,
    Dictionary<string, string>? IdentityClaims);

public sealed record SimulateResponse(
    bool Allowed, bool Hidden, string? DenialCode, string? DenialMessage, bool Bypass,
    IReadOnlyCollection<string> DeniedFields,
    IReadOnlyDictionary<string, string> MaskedFields,
    IReadOnlyCollection<string> WriteOnlyFields,
    string? EffectiveRowFilter);

public sealed record FieldPolicyInput(string FieldPattern, string Action, string? MaskValue);

public sealed record AccessRuleInput(
    string? ServiceName,
    string ResourcePattern,
    string[] Verbs,
    string Effect,
    int Priority,
    string? RowFilter,
    FieldPolicyInput[]? FieldPolicies);

public sealed record RoleInput(
    string Name,
    string? Description,
    bool IsActive,
    bool IsAdmin,
    bool BypassDataRules,
    AccessRuleInput[]? Access);

public sealed record RoleResponse(
    long Id, string Name, string? Description, bool IsActive, bool IsAdmin, bool BypassDataRules,
    IReadOnlyList<AccessRuleResponse> Access, long RowVersion)
{
    public static RoleResponse From(RoleEntity role, IReadOnlyDictionary<long, string> serviceNames) =>
        new(role.Id, role.Name, role.Description, role.IsActive, role.IsAdmin, role.BypassDataRules,
            role.Access.Select(a => new AccessRuleResponse(
                a.Id,
                a.ServiceId is { } sid && serviceNames.TryGetValue(sid, out var name) ? name : null,
                a.ResourcePattern,
                VerbsToNames(a.Verbs),
                a.Effect,
                a.Priority,
                a.RowFilter,
                a.FieldPolicies.Select(f => new FieldPolicyInput(f.FieldPattern, f.Action, f.MaskValue)).ToList()))
                .ToList(),
            role.RowVersion);

    private static string[] VerbsToNames(int mask)
    {
        var names = new List<string>(5);
        if ((mask & 1) != 0) names.Add("GET");
        if ((mask & 2) != 0) names.Add("POST");
        if ((mask & 4) != 0) names.Add("PUT");
        if ((mask & 8) != 0) names.Add("PATCH");
        if ((mask & 16) != 0) names.Add("DELETE");
        return names.ToArray();
    }
}

public sealed record AccessRuleResponse(
    long Id, string? ServiceName, string ResourcePattern, string[] Verbs, string Effect,
    int Priority, string? RowFilter, IReadOnlyList<FieldPolicyInput> FieldPolicies);

[ApiController]
[Route("system/roles")]
[Authorize(Policy = AdminPolicy.Name)]
public class RolesController : ControllerBase
{
    private readonly SystemDbContext _db;
    private readonly PolicyEngine _policy;
    private readonly ODataRowFilterParser _rowFilterParser;

    public RolesController(SystemDbContext db, PolicyEngine policy, ODataRowFilterParser rowFilterParser)
    {
        _db = db;
        _policy = policy;
        _rowFilterParser = rowFilterParser;
    }

    /// <summary>
    /// Permission simulator (spec 07 §5): evaluates this role against a live service
    /// table without issuing any data query. Powers the console "test this role" UX.
    /// </summary>
    [HttpPost("{id:long}/simulate")]
    public async Task<IActionResult> Simulate(long id, [FromBody] SimulateRequest request, CancellationToken ct)
    {
        var role = await LoadAsync(id, ct);
        if (role is null) return NotFound();

        var verb = RoleRuleMapper.ParseVerb(request.Verb);
        if (verb == Verb.None)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Unknown verb '{request.Verb}'.");
        }

        var service = await _db.Services.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Name == request.ServiceName, ct);
        if (service is null)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Unknown service '{request.ServiceName}'.");
        }

        var snapshotRow = await _db.SchemaSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ServiceId == service.Id && s.IsCurrent, ct);
        if (snapshotRow is null)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Service has no schema snapshot yet.");
        }

        var schema = SnapshotSerializer.Deserialize(snapshotRow.SnapshotJson);
        var table = schema.FindTable(request.Table);
        if (table is null)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Unknown table '{request.Table}'.");
        }

        var serviceNames = await ServiceNamesAsync(ct);
        var ruleSet = RoleRuleMapper.ToRuleSet(role, serviceNames);
        var identity = new RequestIdentity
        {
            RoleIds = [role.Id],
            Claims = request.IdentityClaims ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };

        var parser = _rowFilterParser.Bind(service.Name, schema, snapshotRow.VersionHash, identity);
        var decision = _policy.Authorize(
            identity, [ruleSet], service.Name, table.ExposedName, verb,
            table.Columns.Select(c => c.ExposedName).ToList(), parser);

        // Surface the effective row filter as the raw configured expressions (display form).
        var rowFilters = ruleSet.Rules
            .Where(r => r.Effect == RuleEffect.Allow && r.RowFilter is not null
                        && (r.ServiceName is null || r.ServiceName == service.Name)
                        && EzOdata.Core.Text.Glob.IsMatch(table.ExposedName, r.ResourcePattern))
            .Select(r => r.RowFilter!)
            .ToList();

        return Ok(new SimulateResponse(
            decision.Allowed, decision.Hidden, decision.DenialCode, decision.DenialMessage, decision.Bypass,
            decision.DeniedFields, decision.MaskedFields, decision.WriteOnlyFields,
            decision.Allowed && rowFilters.Count > 0 ? string.Join(" and ", rowFilters.Select(f => $"({f})")) : null));
    }

    [HttpGet]
    public async Task<IReadOnlyList<RoleResponse>> List(CancellationToken ct)
    {
        var serviceNames = await ServiceNamesAsync(ct);
        var roles = await _db.Roles
            .AsNoTracking()
            .Include(r => r.Access).ThenInclude(a => a.FieldPolicies)
            .OrderBy(r => r.Name)
            .ToListAsync(ct);
        return roles.Select(r => RoleResponse.From(r, serviceNames)).ToList();
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct)
    {
        var role = await LoadAsync(id, ct);
        return role is null ? NotFound() : Ok(RoleResponse.From(role, await ServiceNamesAsync(ct)));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] RoleInput input, CancellationToken ct)
    {
        if (await ValidateAsync(input, ct) is { } problem) return problem;
        if (await _db.Roles.AnyAsync(r => r.Name == input.Name, ct))
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "A role with this name already exists.");
        }

        var role = new RoleEntity
        {
            Name = input.Name,
            Description = input.Description,
            IsActive = input.IsActive,
            IsAdmin = input.IsAdmin,
            BypassDataRules = input.BypassDataRules,
        };
        await ApplyAccessAsync(role, input.Access ?? [], ct);

        _db.Roles.Add(role);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = role.Id }, RoleResponse.From(role, await ServiceNamesAsync(ct)));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Replace(long id, [FromBody] RoleInput input, CancellationToken ct)
    {
        var role = await LoadAsync(id, ct);
        if (role is null) return NotFound();
        if (await ValidateAsync(input, ct) is { } problem) return problem;

        role.Name = input.Name;
        role.Description = input.Description;
        role.IsActive = input.IsActive;
        role.IsAdmin = input.IsAdmin;
        role.BypassDataRules = input.BypassDataRules;

        // Rules saved atomically with the role (spec 07 §5): replace wholesale.
        _db.RoleServiceAccess.RemoveRange(role.Access);
        role.Access.Clear();
        await ApplyAccessAsync(role, input.Access ?? [], ct);

        await _db.SaveChangesAsync(ct);
        return Ok(RoleResponse.From(role, await ServiceNamesAsync(ct)));
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        var role = await LoadAsync(id, ct);
        if (role is null) return NotFound();

        // Block deletion while referenced (spec 03 §5)
        var referencingApps = await _db.Apps.Where(a => a.RoleId == id).Select(a => a.Name).ToListAsync(ct);
        var referencingUsers = await _db.UserRoles.CountAsync(ur => ur.RoleId == id, ct);
        if (referencingApps.Count > 0 || referencingUsers > 0)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Role is in use.",
                detail: $"Apps: [{string.Join(", ", referencingApps)}], user assignments: {referencingUsers}.");
        }

        _db.Roles.Remove(role);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<RoleEntity?> LoadAsync(long id, CancellationToken ct) =>
        await _db.Roles
            .Include(r => r.Access).ThenInclude(a => a.FieldPolicies)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

    private async Task<Dictionary<long, string>> ServiceNamesAsync(CancellationToken ct) =>
        await _db.Services.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);

    private async Task<IActionResult?> ValidateAsync(RoleInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 64)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Role name is required (max 64 chars).");
        }

        foreach (var rule in input.Access ?? [])
        {
            if (string.IsNullOrWhiteSpace(rule.ResourcePattern))
            {
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: "resourcePattern is required on every rule.");
            }

            if (rule.Effect is not ("allow" or "deny"))
            {
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: "effect must be 'allow' or 'deny'.");
            }

            if (rule.ServiceName is { } svc && !await _db.Services.AnyAsync(s => s.Name == svc, ct))
            {
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Unknown service '{svc}'.");
            }

            foreach (var field in rule.FieldPolicies ?? [])
            {
                if (field.Action is not ("deny" or "mask" or "writeonly"))
                {
                    return Problem(statusCode: StatusCodes.Status400BadRequest,
                        title: "Field policy action must be deny | mask | writeonly.");
                }
            }

            foreach (var verb in rule.Verbs ?? [])
            {
                if (VerbBit(verb) == 0)
                {
                    return Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Unknown verb '{verb}'.");
                }
            }
        }

        return null;
    }

    private async Task ApplyAccessAsync(RoleEntity role, AccessRuleInput[] rules, CancellationToken ct)
    {
        var serviceIds = await _db.Services.AsNoTracking().ToDictionaryAsync(s => s.Name, s => s.Id, ct);

        foreach (var rule in rules)
        {
            var entity = new RoleServiceAccessEntity
            {
                ServiceId = rule.ServiceName is { } svc ? serviceIds[svc] : null,
                ResourcePattern = rule.ResourcePattern,
                Verbs = (rule.Verbs ?? []).Aggregate(0, (mask, v) => mask | VerbBit(v)),
                Effect = rule.Effect,
                Priority = rule.Priority,
                RowFilter = rule.RowFilter,
                FieldPolicies = (rule.FieldPolicies ?? []).Select(f => new FieldPolicyEntity
                {
                    FieldPattern = f.FieldPattern,
                    Action = f.Action,
                    MaskValue = f.MaskValue,
                }).ToList(),
            };
            role.Access.Add(entity);
        }
    }

    private static int VerbBit(string verb) => verb.ToUpperInvariant() switch
    {
        "GET" => 1,
        "POST" => 2,
        "PUT" => 4,
        "PATCH" => 8,
        "DELETE" => 16,
        _ => 0,
    };
}
