using ClinicAdoNet.API.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Data;
using WebApplication1.DTOs;

namespace ClinicAdoNet.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AppointmentsController : ControllerBase
{
    private readonly string _connectionString;

    public AppointmentsController(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? throw new InvalidOperationException("Brak Connection String w konfiguracji");
    }

    [HttpGet]
    public async Task<IActionResult> GetAppointments([FromQuery] string? status, [FromQuery] string? patientLastName)
    {
        var appointments = new List<AppointmentListDto>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var query = """
            SELECT a.IdAppointment, a.AppointmentDate, a.Status, a.Reason, 
                   p.FirstName + N' ' + p.LastName AS PatientFullName, p.Email AS PatientEmail
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            WHERE (@Status IS NULL OR a.Status = @Status)
              AND (@PatientLastName IS NULL OR p.LastName = @PatientLastName)
            ORDER BY a.AppointmentDate;
            """;

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@Status", SqlDbType.NVarChar).Value = (object?)status ?? DBNull.Value;
        command.Parameters.Add("@PatientLastName", SqlDbType.NVarChar).Value = (object?)patientLastName ?? DBNull.Value;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            appointments.Add(new AppointmentListDto
            {
                IdAppointment = reader.GetInt32(reader.GetOrdinal("IdAppointment")),
                AppointmentDate = reader.GetDateTime(reader.GetOrdinal("AppointmentDate")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                Reason = reader.GetString(reader.GetOrdinal("Reason")),
                PatientFullName = reader.GetString(reader.GetOrdinal("PatientFullName")),
                PatientEmail = reader.GetString(reader.GetOrdinal("PatientEmail"))
            });
        }
        return Ok(appointments);
    }

    [HttpGet("{idAppointment}")]
    public async Task<IActionResult> GetAppointment(int idAppointment)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var query = """
            SELECT a.IdAppointment, a.AppointmentDate, a.Status, a.Reason, a.InternalNotes, a.CreatedAt,
                   p.Email AS PatientEmail, p.PhoneNumber AS PatientPhone, d.LicenseNumber AS DoctorLicenseNumber
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON a.IdPatient = p.IdPatient
            JOIN dbo.Doctors d ON a.IdDoctor = d.IdDoctor
            WHERE a.IdAppointment = @IdAppointment;
            """;

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) 
            return NotFound(new ErrorResponseDto { Message = "Nie znaleziono wizyty" });

        return Ok(new AppointmentDetailsDto
        {
            IdAppointment = reader.GetInt32(reader.GetOrdinal("IdAppointment")),
            AppointmentDate = reader.GetDateTime(reader.GetOrdinal("AppointmentDate")),
            Status = reader.GetString(reader.GetOrdinal("Status")),
            Reason = reader.GetString(reader.GetOrdinal("Reason")),
            InternalNotes = reader.IsDBNull(reader.GetOrdinal("InternalNotes")) ? string.Empty : reader.GetString(reader.GetOrdinal("InternalNotes")),
            PatientEmail = reader.GetString(reader.GetOrdinal("PatientEmail")),
            PatientPhone = reader.GetString(reader.GetOrdinal("PatientPhone")), // W bazie PhoneNumber to NOT NULL, usunięto IsDBNull
            DoctorLicenseNumber = reader.GetString(reader.GetOrdinal("DoctorLicenseNumber")),
            CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt"))
        });
    }

    [HttpPost]
    public async Task<IActionResult> CreateAppointment([FromBody] CreateAppointmentRequestDto request)
    {
        if (request.AppointmentDate <= DateTime.Now) 
            return BadRequest(new ErrorResponseDto { Message = "Data wizyty musi być w przyszłości." });

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

       
        var checkActorsQuery = """
            SELECT 
                (SELECT IsActive FROM dbo.Patients WHERE IdPatient = @IdPatient) AS PatientActive,
                (SELECT IsActive FROM dbo.Doctors WHERE IdDoctor = @IdDoctor) AS DoctorActive
            """;
        await using var checkActorsCmd = new SqlCommand(checkActorsQuery, connection);
        checkActorsCmd.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        checkActorsCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        
        await using var actorsReader = await checkActorsCmd.ExecuteReaderAsync();
        await actorsReader.ReadAsync();
        
        var patientActive = actorsReader.IsDBNull(0) ? (bool?)null : actorsReader.GetBoolean(0);
        var doctorActive = actorsReader.IsDBNull(1) ? (bool?)null : actorsReader.GetBoolean(1);
        await actorsReader.CloseAsync();

        if (patientActive == null || patientActive == false) 
            return BadRequest(new ErrorResponseDto { Message = "Pacjent nie istnieje lub jest nieaktywny" });
        if (doctorActive == null || doctorActive == false) 
            return BadRequest(new ErrorResponseDto { Message = "Lekarz nie istnieje lub jest nieaktywny" });

        
        var conflictQuery = "SELECT 1 FROM dbo.Appointments WHERE IdDoctor = @IdDoctor AND AppointmentDate = @AppointmentDate AND Status != 'Cancelled'";
        await using var conflictCmd = new SqlCommand(conflictQuery, connection);
        conflictCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        conflictCmd.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
        
        if (await conflictCmd.ExecuteScalarAsync() != null) 
            return Conflict(new ErrorResponseDto { Message = "Lekarz ma już zaplanowaną wizytę w tym terminie" });

       
        var insertQuery = """
            INSERT INTO dbo.Appointments (IdPatient, IdDoctor, AppointmentDate, Reason, Status, CreatedAt)
            OUTPUT INSERTED.IdAppointment
            VALUES (@IdPatient, @IdDoctor, @AppointmentDate, @Reason, 'Scheduled', SYSUTCDATETIME());
            """;

        await using var insertCmd = new SqlCommand(insertQuery, connection);
        insertCmd.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        insertCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        insertCmd.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
        insertCmd.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = request.Reason;

        var newId = (int)await insertCmd.ExecuteScalarAsync();
        return CreatedAtAction(nameof(GetAppointment), new { idAppointment = newId }, request);
    }

    [HttpPut("{idAppointment}")]
    public async Task<IActionResult> UpdateAppointment(int idAppointment, [FromBody] UpdateAppointmentRequestDto request)
    {
        var allowedStatuses = new[] { "Scheduled", "Completed", "Cancelled" };
        if (!allowedStatuses.Contains(request.Status)) 
            return BadRequest(new ErrorResponseDto { Message = "Nieprawidłowy status wizyty" });

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        
        var checkCmd = new SqlCommand("SELECT Status, AppointmentDate FROM dbo.Appointments WHERE IdAppointment = @Id", connection);
        checkCmd.Parameters.Add("@Id", SqlDbType.Int).Value = idAppointment;
        await using var reader = await checkCmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) 
            return NotFound(new ErrorResponseDto { Message = "Wizyta nie istnieje" });
        
        var currentStatus = reader.GetString(0);
        var currentDate = reader.GetDateTime(1);
        await reader.CloseAsync();

        if (currentStatus == "Completed" && currentDate != request.AppointmentDate)
            return Conflict(new ErrorResponseDto { Message = "Nie można zmienić terminu wizyty o statusie Completed" });

        
        var checkActorsQuery = """
            SELECT 
                (SELECT IsActive FROM dbo.Patients WHERE IdPatient = @IdPatient) AS PatientActive,
                (SELECT IsActive FROM dbo.Doctors WHERE IdDoctor = @IdDoctor) AS DoctorActive
            """;
        await using var checkActorsCmd = new SqlCommand(checkActorsQuery, connection);
        checkActorsCmd.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        checkActorsCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        
        await using var actorsReader = await checkActorsCmd.ExecuteReaderAsync();
        await actorsReader.ReadAsync();
        var patientActive = actorsReader.IsDBNull(0) ? (bool?)null : actorsReader.GetBoolean(0);
        var doctorActive = actorsReader.IsDBNull(1) ? (bool?)null : actorsReader.GetBoolean(1);
        await actorsReader.CloseAsync();

        if (patientActive == null || patientActive == false) 
            return BadRequest(new ErrorResponseDto { Message = "Pacjent nie istnieje lub jest nieaktywny" });
        if (doctorActive == null || doctorActive == false) 
            return BadRequest(new ErrorResponseDto { Message = "Lekarz nie istnieje lub jest nieaktywny" });

       
        if (currentDate != request.AppointmentDate)
        {
            var conflictQuery = "SELECT 1 FROM dbo.Appointments WHERE IdDoctor = @IdDoctor AND AppointmentDate = @AppointmentDate AND IdAppointment != @IdAppointment AND Status != 'Cancelled'";
            await using var conflictCmd = new SqlCommand(conflictQuery, connection);
            conflictCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
            conflictCmd.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
            conflictCmd.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
            if (await conflictCmd.ExecuteScalarAsync() != null) 
                return Conflict(new ErrorResponseDto { Message = "Lekarz ma już zaplanowaną inną wizytę w tym terminie" });
        }

        
        var updateQuery = """
            UPDATE dbo.Appointments
            SET IdPatient = @IdPatient, IdDoctor = @IdDoctor, AppointmentDate = @AppointmentDate, 
                Status = @Status, Reason = @Reason, InternalNotes = @InternalNotes
            WHERE IdAppointment = @IdAppointment;
            """;

        await using var updateCmd = new SqlCommand(updateQuery, connection);
        updateCmd.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
        updateCmd.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        updateCmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        updateCmd.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
        updateCmd.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value = request.Status;
        updateCmd.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = request.Reason;
        updateCmd.Parameters.Add("@InternalNotes", SqlDbType.NVarChar, 500).Value = (object?)request.InternalNotes ?? DBNull.Value;

        await updateCmd.ExecuteNonQueryAsync();
        return Ok(request);
    }

    [HttpDelete("{idAppointment}")]
    public async Task<IActionResult> DeleteAppointment(int idAppointment)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var statusCmd = new SqlCommand("SELECT Status FROM dbo.Appointments WHERE IdAppointment = @Id", connection);
        statusCmd.Parameters.Add("@Id", SqlDbType.Int).Value = idAppointment;
        var statusObj = await statusCmd.ExecuteScalarAsync();

        if (statusObj == null) 
            return NotFound(new ErrorResponseDto { Message = "Wizyta nie istnieje" });
            
        if (statusObj.ToString() == "Completed") 
            return Conflict(new ErrorResponseDto { Message = "Nie można usunąć wizyty o statusie Completed" });

        var deleteCmd = new SqlCommand("DELETE FROM dbo.Appointments WHERE IdAppointment = @Id", connection);
        deleteCmd.Parameters.Add("@Id", SqlDbType.Int).Value = idAppointment;
        await deleteCmd.ExecuteNonQueryAsync();

        return NoContent();
    }
}