using Api.Data;
using Api.Models;
using Api.Services;

namespace Api.Tests;

/// <summary>Clash warning: a day of the request on which two or more teammates (same manager) already have Approved leave.</summary>
public class LeaveClashTests
{
    // A Monday next year: the seed data is this year, so it never overlaps.
    static readonly DateOnly Mon = Seed.MondayOnOrAfter(new DateOnly(DateTime.UtcNow.Year + 1, 6, 1));

    /// <summary>A new team: manager, the requester and three teammates. Returns the db and their ids.</summary>
    static (AcmeDb Db, int Requester, int A, int B, int C, int Outsider) Team()
    {
        var db = TestDb.Create();
        Employee New(string name, int? manager) => new() { Name = name, Email = $"{name.Replace(" ", ".").ToLowerInvariant()}@clash.test", Department = "Test", ManagerId = manager };
        var boss = New("Team Boss", null); var other = New("Other Boss", null);
        db.Employees.AddRange(boss, other);
        db.SaveChanges();
        var req = New("Rita Requester", boss.Id); var a = New("Adam A", boss.Id); var b = New("Bella B", boss.Id); var c = New("Chris C", boss.Id);
        var outsider = New("Olly Outsider", other.Id);
        db.Employees.AddRange(req, a, b, c, outsider);
        db.SaveChanges();
        return (db, req.Id, a.Id, b.Id, c.Id, outsider.Id);
    }

    static LeaveRequest Leave(int employeeId, DateOnly from, DateOnly to, LeaveStatus status = LeaveStatus.Approved, HalfDay half = HalfDay.None) =>
        new() { EmployeeId = employeeId, StartDate = from, EndDate = to, Status = status, HalfDay = half };

    static async Task<List<LeaveClash>> ClashesFor(AcmeDb db, LeaveRequest request, params LeaveRequest[] existing)
    {
        db.LeaveRequests.AddRange(existing);
        db.LeaveRequests.Add(request);
        await db.SaveChangesAsync();
        return await new LeaveService(db).GetClashesAsync(request);
    }

    [Fact]
    public async Task Two_teammates_off_on_a_day_is_a_clash()
    {
        var t = Team();
        using var db = t.Db;
        var clashes = await ClashesFor(db, Leave(t.Requester, Mon, Mon.AddDays(4), LeaveStatus.Pending),
            Leave(t.A, Mon.AddDays(1), Mon.AddDays(1)), Leave(t.B, Mon, Mon.AddDays(2)));
        var clash = Assert.Single(clashes);
        Assert.Equal(Mon.AddDays(1), clash.Date);
        Assert.Equal(["Adam A", "Bella B"], clash.PeopleOff.Order());
    }

    [Fact]
    public async Task One_teammate_off_is_not_a_clash()
    {
        var t = Team();
        using var db = t.Db;
        Assert.Empty(await ClashesFor(db, Leave(t.Requester, Mon, Mon.AddDays(4), LeaveStatus.Pending), Leave(t.A, Mon, Mon.AddDays(4))));
    }

    [Fact]
    public async Task Pending_and_rejected_leave_do_not_count()
    {
        var t = Team();
        using var db = t.Db;
        Assert.Empty(await ClashesFor(db, Leave(t.Requester, Mon, Mon, LeaveStatus.Pending),
            Leave(t.A, Mon, Mon), Leave(t.B, Mon, Mon, LeaveStatus.Pending), Leave(t.C, Mon, Mon, LeaveStatus.Rejected)));
    }

    [Fact]
    public async Task People_in_another_team_do_not_count()
    {
        var t = Team();
        using var db = t.Db;
        Assert.Empty(await ClashesFor(db, Leave(t.Requester, Mon, Mon, LeaveStatus.Pending), Leave(t.A, Mon, Mon), Leave(t.Outsider, Mon, Mon)));
    }

    [Fact]
    public async Task The_requesters_own_leave_does_not_count()
    {
        var t = Team();
        using var db = t.Db;
        Assert.Empty(await ClashesFor(db, Leave(t.Requester, Mon, Mon, LeaveStatus.Pending), Leave(t.A, Mon, Mon), Leave(t.Requester, Mon, Mon, half: HalfDay.PM)));
    }

    [Fact]
    public async Task Half_day_counts_as_off()
    {
        var t = Team();
        using var db = t.Db;
        var clash = Assert.Single(await ClashesFor(db, Leave(t.Requester, Mon, Mon, LeaveStatus.Pending), Leave(t.A, Mon, Mon), Leave(t.B, Mon, Mon, half: HalfDay.AM)));
        Assert.Equal(Mon, clash.Date);
    }

    [Fact]
    public async Task Weekend_days_are_not_checked()
    {
        var t = Team();
        using var db = t.Db;
        var sat = Mon.AddDays(5);
        // Request Fri-Mon; the team is "off" across the weekend only, so no working day clashes.
        Assert.Empty(await ClashesFor(db, Leave(t.Requester, Mon.AddDays(4), Mon.AddDays(7), LeaveStatus.Pending),
            Leave(t.A, sat, sat.AddDays(1)), Leave(t.B, sat, sat.AddDays(1))));
    }

    [Fact]
    public async Task Employee_without_a_manager_has_no_clashes()
    {
        using var db = TestDb.Create();
        // Seed employee 1 (Aina) has no manager; employees 2 and 3 have no manager either.
        var clashes = await ClashesFor(db, Leave(1, Mon, Mon, LeaveStatus.Pending), Leave(2, Mon, Mon), Leave(3, Mon, Mon));
        Assert.Empty(clashes);
    }
}
