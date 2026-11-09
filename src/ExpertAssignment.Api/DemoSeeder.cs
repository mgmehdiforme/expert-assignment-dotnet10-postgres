using ExpertAssignment.Domain;
using ExpertAssignment.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ExpertAssignment.Api;

public static class DemoSeeder
{
    public static async Task SeedAsync(AppDbContext db)
    {
        if (await db.Specialties.AnyAsync()) return;
        var legal = new Specialty { Name = "Legal" };
        var technical = new Specialty { Name = "Technical" };
        var financial = new Specialty { Name = "Financial" };
        db.Specialties.AddRange(legal, technical, financial);
        await db.SaveChangesAsync();
        db.Experts.AddRange(
            new Expert { Name = "Expert A", Capacity = 4, IsActive = true, ExpertSpecialties = [new ExpertSpecialty { SpecialtyId = legal.Id }, new ExpertSpecialty { SpecialtyId = financial.Id }] },
            new Expert { Name = "Expert B", Capacity = 10, IsActive = true, ExpertSpecialties = [new ExpertSpecialty { SpecialtyId = legal.Id }, new ExpertSpecialty { SpecialtyId = technical.Id }] },
            new Expert { Name = "Expert C", Capacity = 8, IsActive = true, ExpertSpecialties = [new ExpertSpecialty { SpecialtyId = legal.Id }] });
        await db.SaveChangesAsync();
    }
}
