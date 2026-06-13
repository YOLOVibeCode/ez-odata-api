using EzOdata.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EzOdata.Admin.Controllers;

[ApiController]
[Route("system/connectors")]
[Authorize(Policy = AdminPolicy.Name)]
public class ConnectorsController : ControllerBase
{
    [HttpGet]
    public IReadOnlyList<ConnectorInfoResponse> List() =>
    [
        new(ConnectorTypes.PostgreSql, "PostgreSQL"),
        new(ConnectorTypes.MySql, "MySQL / MariaDB"),
        new(ConnectorTypes.SqlServer, "SQL Server"),
        new(ConnectorTypes.Sqlite, "SQLite"),
    ];
}
