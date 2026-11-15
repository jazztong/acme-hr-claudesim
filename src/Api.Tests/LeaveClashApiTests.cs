using System.Net;
using System.Net.Http.Json;

namespace Api.Tests;

/// <summary>GET /leave/{id}/clashes: only someone who may approve the request (its manager or HR) sees who else is off.</summary>
public class LeaveClashApiTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    // Two more of Aina's direct reports (Seed.cs: employees 11 and 16 have manager 1).
    const string Kumar = "kumar.wong11@acme-hr.example";
    const string Priya = "priya.hassan16@acme-hr.example";

    static DateOnly Monday(int week) => Api.Data.Seed.MondayOnOrAfter(new DateOnly(DateTime.UtcNow.Year + 1, 1, 5)).AddDays(7 * week);
    static object Days(DateOnly from, int days) => new { startDate = from, endDate = from.AddDays(days - 1), halfDay = "None", reason = "Test" };

    static async Task<int> CreateLeave(HttpClient who, DateOnly from, int days)
    {
        var res = await who.PostAsJsonAsync("/leave", Days(from, days));
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<Row>())!.Id;
    }

    /// <summary>A one-day request by Farah (auto-approved): enough to test who may look at it.</summary>
    async Task<int> FarahsRequest(int week) => await CreateLeave(await api.SignedInAs(People.Farah), Monday(week), 1);

    [Fact]
    public async Task Manager_sees_clash_for_their_report()
    {
        var mon = Monday(10);
        await CreateLeave(await api.SignedInAs(Kumar), mon, 2);  // Mon-Tue, auto-approved
        await CreateLeave(await api.SignedInAs(Priya), mon, 2);
        var request = await CreateLeave(await api.SignedInAs(People.Farah), mon, 4); // Mon-Thu, Pending

        var aina = await api.SignedInAs(People.Aina);
        var res = await aina.GetAsync($"/leave/{request}/clashes");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = (await res.Content.ReadFromJsonAsync<ClashesResponse>())!;
        Assert.Equal(request, body.RequestId);
        Assert.Equal([mon, mon.AddDays(1)], body.Clashes.Select(c => c.Date));
        Assert.All(body.Clashes, c => Assert.Equal(["Kumar Wong", "Priya Hassan"], c.PeopleOff.Order()));
    }

    [Fact]
    public async Task Another_employee_gets_403()
    {
        var request = await FarahsRequest(20);
        var gopal = await api.SignedInAs(People.Gopal); // employee in another team
        Assert.Equal(HttpStatusCode.Forbidden, (await gopal.GetAsync($"/leave/{request}/clashes")).StatusCode);
    }

    [Fact]
    public async Task Requester_cannot_see_clashes_on_own_request_403()
    {
        var request = await FarahsRequest(22);
        var farah = await api.SignedInAs(People.Farah);
        Assert.Equal(HttpStatusCode.Forbidden, (await farah.GetAsync($"/leave/{request}/clashes")).StatusCode);
    }

    [Fact]
    public async Task Manager_of_another_team_gets_403()
    {
        var request = await FarahsRequest(24);
        var ben = await api.SignedInAs("ben.lim2@acme-hr.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.GetAsync($"/leave/{request}/clashes")).StatusCode);
    }

    [Fact]
    public async Task HR_sees_clashes()
    {
        var request = await FarahsRequest(26);
        var hr = await api.SignedInAs(People.Dina);
        var res = await hr.GetAsync($"/leave/{request}/clashes");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Empty((await res.Content.ReadFromJsonAsync<ClashesResponse>())!.Clashes);
    }

    [Fact]
    public async Task Unknown_request_is_404()
    {
        var aina = await api.SignedInAs(People.Aina);
        Assert.Equal(HttpStatusCode.NotFound, (await aina.GetAsync("/leave/99999/clashes")).StatusCode);
    }

    record Row(int Id);
    record Clash(DateOnly Date, List<string> PeopleOff);
    record ClashesResponse(int RequestId, List<Clash> Clashes);
}
