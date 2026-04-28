using System;
using System.ComponentModel.DataAnnotations;

namespace ClinicAdoNet.API.DTOs;

public class UpdateAppointmentRequestDto
{
    [Required]
    public int IdPatient { get; set; }
    [Required]
    public int IdDoctor { get; set; }
    [Required]
    public DateTime AppointmentDate { get; set; }
    [Required]
    public string Status { get; set; } = string.Empty;
    [Required, MaxLength(250)]
    public string Reason { get; set; } = string.Empty;
    public string InternalNotes { get; set; } = string.Empty;
}