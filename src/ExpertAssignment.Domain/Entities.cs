namespace ExpertAssignment.Domain;

public enum RequestPriority { Normal = 0, Urgent = 1 }
public enum RequestStatus { Pending = 0, Assigned = 1, Closed = 2 }

public sealed class Specialty
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public ICollection<ExpertSpecialty> ExpertSpecialties { get; set; } = new List<ExpertSpecialty>();
}

public sealed class Expert
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public int Capacity { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastAssignedAt { get; set; }
    public ICollection<ExpertSpecialty> ExpertSpecialties { get; set; } = new List<ExpertSpecialty>();
}

public sealed class ExpertSpecialty
{
    public int ExpertId { get; set; }
    public Expert Expert { get; set; } = null!;
    public int SpecialtyId { get; set; }
    public Specialty Specialty { get; set; } = null!;
}

public sealed class ServiceRequest
{
    public long Id { get; set; }
    public string RequestKey { get; set; } = null!;
    public int SpecialtyId { get; set; }
    public Specialty Specialty { get; set; } = null!;
    public RequestPriority Priority { get; set; }
    public RequestStatus Status { get; set; } = RequestStatus.Pending;
    public int? AssignedExpertId { get; set; }
    public Expert? AssignedExpert { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
}

public sealed class AssignmentHistory
{
    public long Id { get; set; }
    public long RequestId { get; set; }
    public int ExpertId { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
    public DateTimeOffset? UnassignedAt { get; set; }
}
