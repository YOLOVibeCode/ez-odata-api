using System.Text.Json;
using EzOdata.Admin.Services;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core.Services;
using EzOdata.Data;
using EzOdata.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EzOdata.Admin.Controllers;

[ApiController]
[Route("system/services")]
[Authorize(Policy = AdminPolicy.Name)]
public class ServicesController : ControllerBase
{
    private readonly SystemDbContext _db;
    private readonly ServiceConnectionCodec _codec;
    private readonly IConnectorRegistry _connectors;

    public ServicesController(SystemDbContext db, ServiceConnectionCodec codec, IConnectorRegistry connectors)
    {
        _db = db;
        _codec = codec;
        _connectors = connectors;
    }

    [HttpGet]
    public async Task<IReadOnlyList<ServiceResponse>> List(CancellationToken ct) =>
        (await _db.Services.OrderBy(s => s.Name).ToListAsync(ct)).Select(ServiceResponse.From).ToList();

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct)
    {
        var service = await _db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        return service is null ? NotFound() : Ok(ServiceResponse.From(service));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateServiceRequest request, CancellationToken ct)
    {
        if (!ServiceName.IsValid(request.Name))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Service name must match ^[a-z][a-z0-9_-]{1,62}$.");
        }

        if (!ConnectorTypes.IsKnown(request.ConnectorType))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: $"Unknown connector type. Known: {string.Join(", ", ConnectorTypes.All)}.");
        }

        if (string.IsNullOrWhiteSpace(request.Label))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Label is required.");
        }

        var connectorType = request.ConnectorType.ToLowerInvariant();
        if (_codec.Validate(connectorType, request.Connection) is { } connectionError)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: connectionError);
        }

        var options = request.Options ?? new ServiceOptions();
        if (options.Error() is { } optionsError)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: optionsError);
        }

        if (await _db.Services.AnyAsync(s => s.Name == request.Name, ct))
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "A service with this name already exists.");
        }

        var (encrypted, fingerprint, display) = _codec.Encode(connectorType, request.Connection);
        var service = new ServiceEntity
        {
            Name = request.Name,
            Label = request.Label.Trim(),
            Description = request.Description,
            ConnectorType = connectorType,
            ConnectionEncrypted = encrypted,
            ConnectionFingerprint = fingerprint,
            ConnectionDisplay = display,
            OptionsJson = JsonSerializer.Serialize(options, JsonDefaults.Options),
            Status = ServiceStatus.Pending,
        };
        _db.Services.Add(service);
        await _db.SaveChangesAsync(ct);

        // Introspection job enqueue lands in Phase 1 (schema cache manager).
        return CreatedAtAction(nameof(Get), new { id = service.Id }, ServiceResponse.From(service));
    }

    [HttpPatch("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] UpdateServiceRequest request, CancellationToken ct)
    {
        var service = await _db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (service is null) return NotFound();

        if (CheckRowVersion(service.RowVersion) is { } preconditionFailure) return preconditionFailure;

        if (request.Label is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Label))
            {
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Label cannot be empty.");
            }

            service.Label = request.Label.Trim();
        }

        if (request.Description is not null) service.Description = request.Description;
        if (request.SchemaRefreshMinutes is not null) service.SchemaRefreshMinutes = request.SchemaRefreshMinutes;

        if (request.Options is not null)
        {
            if (request.Options.Error() is { } optionsError)
            {
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: optionsError);
            }

            service.OptionsJson = JsonSerializer.Serialize(request.Options, JsonDefaults.Options);
        }

        if (request.Connection is not null)
        {
            if (_codec.Validate(service.ConnectorType, request.Connection) is { } connectionError)
            {
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: connectionError);
            }

            (service.ConnectionEncrypted, service.ConnectionFingerprint, service.ConnectionDisplay) =
                _codec.Encode(service.ConnectorType, request.Connection);
            service.Status = ServiceStatus.Pending; // re-introspection required after credential change
            service.StatusDetail = null;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(ServiceResponse.From(service));
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        var service = await _db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (service is null) return NotFound();

        // Soft delete; name freed by suffixing (spec 03 §2.1, §5)
        service.IsDeleted = true;
        service.Name = $"{service.Name}__deleted_{service.Id}";
        service.Status = ServiceStatus.Disabled;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:long}/test")]
    public async Task<IActionResult> TestConnection(long id, CancellationToken ct)
    {
        var service = await _db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (service is null) return NotFound();

        if (!_connectors.TryGet(service.ConnectorType, out var connector))
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: $"Connector '{service.ConnectorType}' is not available in this build.");
        }

        var spec = _codec.Decode(service.ConnectionEncrypted);
        var result = await connector.Tester.TestAsync(spec, ct);
        return Ok(new { ok = result.Ok, category = result.Category.ToString(), message = result.Message, serverVersion = result.ServerVersion });
    }

    [HttpPost("{id:long}/refresh")]
    public async Task<IActionResult> Refresh(long id, CancellationToken ct)
    {
        var service = await _db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (service is null) return NotFound();

        if (service.Status == ServiceStatus.Disabled)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Enable the service before refreshing.");
        }

        service.Status = service.Status == ServiceStatus.Active ? ServiceStatus.Refreshing : ServiceStatus.Pending;
        await _db.SaveChangesAsync(ct);
        return Accepted(new { status = service.Status.ToString() });
    }

    [HttpGet("{id:long}/schema")]
    public async Task<IActionResult> Schema(long id, CancellationToken ct)
    {
        var snapshot = await _db.SchemaSnapshots
            .Where(s => s.ServiceId == id && s.IsCurrent)
            .Select(s => new { s.VersionHash, s.IntrospectedAt, s.SnapshotJson })
            .FirstOrDefaultAsync(ct);

        return snapshot is null
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "No schema snapshot yet.")
            : Content(snapshot.SnapshotJson, "application/json");
    }

    [HttpPost("{id:long}/enable")]
    public Task<IActionResult> Enable(long id, CancellationToken ct) => SetDisabled(id, false, ct);

    [HttpPost("{id:long}/disable")]
    public Task<IActionResult> Disable(long id, CancellationToken ct) => SetDisabled(id, true, ct);

    private async Task<IActionResult> SetDisabled(long id, bool disabled, CancellationToken ct)
    {
        var service = await _db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (service is null) return NotFound();

        if (disabled)
        {
            service.Status = ServiceStatus.Disabled;
        }
        else if (service.Status == ServiceStatus.Disabled)
        {
            // Back to Pending until a snapshot exists; Phase 1 flips straight to Active when one does.
            service.Status = await _db.SchemaSnapshots.AnyAsync(s => s.ServiceId == id && s.IsCurrent, ct)
                ? ServiceStatus.Active
                : ServiceStatus.Pending;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(ServiceResponse.From(service));
    }

    private IActionResult? CheckRowVersion(long currentRowVersion)
    {
        var ifMatch = Request.Headers.IfMatch.ToString();
        if (string.IsNullOrEmpty(ifMatch) || ifMatch == "*") return null;

        return ifMatch.Trim('"') == currentRowVersion.ToString()
            ? null
            : Problem(statusCode: StatusCodes.Status412PreconditionFailed, title: "Resource was modified by another request.");
    }
}
