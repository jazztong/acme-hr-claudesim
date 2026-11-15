using System.Net;
using System.Net.Http.Json;

namespace Api.Tests;

/// <summary>GET /employees/{id}/summary: own data only for an employee (403 otherwise), 404 for an unknown id.</summary>
public class EmployeeSummaryApiTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Employee_sees_their_own_summary()
    {
        var farah = await api.SignedInAs(People.Farah);
        var res = await farah.GetAsync("/employees/6/summary");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var summary = await res.Content.ReadFromJsonAsync<Summary>();
        Assert.Equal(6, summary!.EmployeeId);
        Assert.True(summary.DaysTakenThisYear >= 0);
    }

    [Fact]
    public async Task Employee_cannot_see_someone_elses_summary()
    {
        var farah = await api.SignedInAs(People.Farah);
        Assert.Equal(HttpStatusCode.Forbidden, (await farah.GetAsync("/employees/7/summary")).StatusCode);
    }

    [Fact]
    public async Task Unknown_employee_summary_is_404()
    {
        var hr = await api.SignedInAs(People.Dina); // HR may see everyone, so the only reason to refuse is that the id does not exist
        Assert.Equal(HttpStatusCode.NotFound, (await hr.GetAsync("/employees/99999/summary")).StatusCode);
    }

    record Summary(int EmployeeId, decimal DaysTakenThisYear);
}
