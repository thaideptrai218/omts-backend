using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Employees;
using OMTS.Application.Features.Files;

namespace OMTS.Api.Controllers;

/// <summary>
/// LABOR-01 – employee profiles: the Cooperative Owner, and a Zone Manager (§I.4.4). Every Zone Manager sees every employee;
/// the service checks that they maintain the one they change (a shared working zone, BR-LABOR-01).
/// </summary>
[ApiController]
[Route("api/employees")]
[Authorize]
public sealed class EmployeesController(
    EmployeeService employees,
    EmployeeQueryService queries,
    EmployeeFileService files,
    EmployeeWorkingZoneService workingZones,
    EmploymentService employment) : ControllerBase
{
    /// <summary>
    /// Filters: <c>search</c> (name, code or phone), <c>statusId</c>, <c>zoneId</c>, <c>employeeType</c> (SeasonalWorker or Staff, the
    /// type in force today) and <c>jobTypeId</c>. <c>sortBy</c> is name, code, phone, type, status or lastWorkingDay with
    /// <c>sortDir</c> asc or desc, over the whole result before paging (then by id); without it, the employees of the caller's
    /// zones come first, then by name. A value that does not exist is 400 MSG-GEN-01.
    /// </summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.LaborWorkers, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<Paged<EmployeeListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] EmployeeListQuery query, CancellationToken cancellationToken) =>
        (await queries.SearchAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// The list as filtered and sorted (the same parameters, no paging), as <c>employees-yyyyMMdd.xlsx</c>: at most 5000 rows (400
    /// otherwise, narrow the filter). <c>ids</c> (at most 200) exports only those employees and ignores the other filters. The
    /// personal and bank columns are there only for a caller who maintains employees, and filled only on the rows they maintain;
    /// the bank account number and the ID number are never exported.
    /// </summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.LaborWorkers, PermissionAction.Export, PermissionScope.AnyZone)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Export([FromQuery] EmployeeExportQuery query, CancellationToken cancellationToken) =>
        (await files.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    /// <summary>The import file with its header row and no data (<c>employees-import-template.xlsx</c>). Cooperative Owner only.</summary>
    [HttpGet("import/template")]
    [RequireCooperativeOwner]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult ImportTemplate() => files.Template().ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    /// <summary>
    /// Multipart field <c>file</c>: .xlsx, at most 2 MB and 1000 rows. Each row is checked like a new employee; rows with an error are
    /// skipped and listed in <c>errors</c> (row, fields, message), the others are imported. Cooperative Owner only.
    /// </summary>
    [HttpPost("import")]
    [RequireCooperativeOwner]
    [RequestSizeLimit(3 * 1024 * 1024)]
    [UploadField("File")]
    [ProducesResponseType<EmployeeImportResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Import(IFormFile? file, CancellationToken cancellationToken) =>
        (await files.ImportAsync(file is null ? null : await file.ToIncomingFileAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);

    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.LaborWorkers, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<EmployeeView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await queries.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// A Zone Manager sends <c>ownerApproved</c>: true (MSG-LABOR-01 otherwise). <c>code</c> is optional: left empty it is NV000001…
    /// (MSG-GEN-02 when a typed one is taken). With an optional <c>account</c> block, for the
    /// Cooperative Owner only, the staff account is created in the same save; its temporary password is returned once.
    /// </summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.LaborWorkers, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<EmployeeSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveEmployeeRequest request, CancellationToken cancellationToken) =>
        (await employees.CreateAsync(request, cancellationToken)).ToActionResult(e => StatusCode(StatusCodes.Status201Created, e));

    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionFunctions.LaborWorkers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<EmployeeSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveEmployeeRequest request, CancellationToken cancellationToken) =>
        (await employees.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>LABOR-01.2: adds one of the caller's zones to any Employed employee.</summary>
    [HttpPost("{id:guid}/working-zones")]
    [RequirePermission(PermissionFunctions.LaborWorkers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<EmployeeSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AddWorkingZone(Guid id, AddWorkingZoneRequest request, CancellationToken cancellationToken) =>
        (await workingZones.AddAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>The Cooperative Owner creates the staff account of an Employed employee who has none.</summary>
    [HttpPost("{id:guid}/account")]
    [RequireCooperativeOwner]
    [ProducesResponseType<EmployeeSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateAccount(Guid id, NewEmployeeAccount request, CancellationToken cancellationToken) =>
        (await employees.CreateAccountAsync(id, request, cancellationToken)).ToActionResult(e => StatusCode(StatusCodes.Status201Created, e));

    /// <summary>LABOR-01.3: the employee is Left from the day after <c>lastWorkingDay</c>.</summary>
    [HttpPost("{id:guid}/end-employment")]
    [RequirePermission(PermissionFunctions.LaborWorkers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<EmploymentChanged>(StatusCodes.Status200OK)]
    public async Task<IActionResult> EndEmployment(Guid id, EndEmploymentRequest request, CancellationToken cancellationToken) =>
        (await employment.EndEmploymentAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>LABOR-01.4: a Left employee is Employed again from today.</summary>
    [HttpPost("{id:guid}/resume")]
    [RequirePermission(PermissionFunctions.LaborWorkers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<EmploymentChanged>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Resume(Guid id, ResumeEmployeeRequest request, CancellationToken cancellationToken) =>
        (await employment.ResumeAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
