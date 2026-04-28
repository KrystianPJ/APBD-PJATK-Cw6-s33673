using System;
using System.ComponentModel.DataAnnotations;

namespace WebApplication1.DTOs;

public class CreateAppointmentRequestDto
{
    [Required]
    public int IdPatient { get; set; }
    [Required]
    public int IdDoctor { get; set; }
    [Required]
    public DateTime AppointmentDate { get; set; }
    [Required, MaxLength(250)]
    public string Reason { get; set; } = string.Empty;
}

