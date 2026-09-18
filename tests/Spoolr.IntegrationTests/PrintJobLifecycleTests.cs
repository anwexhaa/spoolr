using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Spoolr.Api.Contracts;
using Spoolr.Core.Jobs;
using Spoolr.Core.Printers;

namespace Spoolr.IntegrationTests;

/// <summary>
/// Drives the service the way a printer and a submitter actually would, over HTTP.
/// </summary>
public sealed class PrintJobLifecycleTests(SpoolrApiFactory factory) : IClassFixture<SpoolrApiFactory>
{
    [Fact]
    public async Task Liveness_DoesNotDependOnTheDatabase()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_ReportsHealthyWhenTheDatabaseIsReachable()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FullLifecycle_SubmitClaimAcknowledgeComplete()
    {
        using var client = factory.CreateClient();
        var printer = await RegisterPrinterAsync(client);

        var submitted = await SubmitJobAsync(client, printer.Id, "quarterly-report.pdf", pages: 12);
        Assert.Equal(JobStatus.Queued, submitted.Status);
        Assert.Equal(0, submitted.AttemptCount);

        AuthenticateAsDevice(client, printer);

        var claimed = await client.PostAsync("/api/v1/device/jobs/next", content: null);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);

        var claimedJob = await ReadAsync<JobResponse>(claimed);
        Assert.Equal(submitted.Id, claimedJob.Id);
        Assert.Equal(JobStatus.Dispatched, claimedJob.Status);
        Assert.Equal(1, claimedJob.AttemptCount);

        var acknowledged = await client.PostAsync(
            $"/api/v1/device/jobs/{claimedJob.Id}/acknowledge", content: null);
        Assert.Equal(JobStatus.Printing, (await ReadAsync<JobResponse>(acknowledged)).Status);

        var completed = await client.PostAsJsonAsync(
            $"/api/v1/device/jobs/{claimedJob.Id}/result",
            new JobResultRequest { Succeeded = true },
            SpoolrApiFactory.Json);

        var finished = await ReadAsync<JobResponse>(completed);
        Assert.Equal(JobStatus.Completed, finished.Status);
        Assert.NotNull(finished.CompletedAt);
        Assert.Null(finished.LastError);

        var attempt = Assert.Single(finished.Attempts);
        Assert.True(attempt.Succeeded);
        Assert.NotNull(attempt.AcknowledgedAt);
    }

    [Fact]
    public async Task FailedJob_IsRetriedAndThenFailsForGood()
    {
        using var client = factory.CreateClient();
        var printer = await RegisterPrinterAsync(client);
        var job = await SubmitJobAsync(client, printer.Id, "doomed.pdf", pages: 1);

        AuthenticateAsDevice(client, printer);

        // MaxAttempts is 2 in the test configuration.
        var first = await FailOnceAsync(client, job.Id, "paper jam");
        Assert.Equal(JobStatus.Queued, first.Status);
        Assert.Equal(1, first.AttemptCount);
        Assert.Equal("paper jam", first.LastError);

        var second = await FailOnceAsync(client, job.Id, "paper jam");
        Assert.Equal(JobStatus.Failed, second.Status);
        Assert.Equal(2, second.AttemptCount);
        Assert.Equal(2, second.Attempts.Count);
        Assert.NotNull(second.CompletedAt);

        // Nothing is left for the printer to pick up.
        var empty = await client.PostAsync("/api/v1/device/jobs/next", content: null);
        Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode);
    }

    [Fact]
    public async Task ClaimNext_ReturnsNoContentWhenTheQueueIsEmpty()
    {
        using var client = factory.CreateClient();
        var printer = await RegisterPrinterAsync(client);

        AuthenticateAsDevice(client, printer);

        var response = await client.PostAsync("/api/v1/device/jobs/next", content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task DeviceEndpoints_RejectAnUnauthenticatedCaller()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/v1/device/jobs/next", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeviceEndpoints_RejectAWrongDeviceKey()
    {
        using var client = factory.CreateClient();
        var printer = await RegisterPrinterAsync(client);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("DeviceKey", $"{printer.Id}:not-the-real-key");

        var response = await client.PostAsync("/api/v1/device/jobs/next", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ADeviceCannotActOnAnotherPrintersJob()
    {
        using var client = factory.CreateClient();

        var owner = await RegisterPrinterAsync(client);
        var stranger = await RegisterPrinterAsync(client);

        var job = await SubmitJobAsync(client, owner.Id, "confidential.pdf", pages: 2);

        AuthenticateAsDevice(client, stranger);

        var response = await client.PostAsync($"/api/v1/device/jobs/{job.Id}/acknowledge", content: null);

        // Reported as missing rather than forbidden, so the endpoint does not confirm that
        // the id exists.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SubmitJob_ForAnUnknownPrinter_IsNotFound()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/jobs",
            new SubmitJobRequest
            {
                PrinterId = Guid.CreateVersion7(),
                DocumentName = "orphan.pdf",
                PageCount = 1,
            },
            SpoolrApiFactory.Json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SubmitJob_BeyondThePrinterPageLimit_IsRejectedUpFront()
    {
        using var client = factory.CreateClient();
        var printer = await RegisterPrinterAsync(client, maxPages: 10);

        var response = await client.PostAsJsonAsync(
            "/api/v1/jobs",
            new SubmitJobRequest
            {
                PrinterId = printer.Id,
                DocumentName = "phonebook.pdf",
                PageCount = 5_000,
            },
            SpoolrApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task SubmitJob_WithABlankDocumentName_IsAValidationProblem()
    {
        using var client = factory.CreateClient();
        var printer = await RegisterPrinterAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/v1/jobs",
            new SubmitJobRequest
            {
                PrinterId = printer.Id,
                DocumentName = "   ",
                PageCount = 0,
            },
            SpoolrApiFactory.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains(nameof(SubmitJobRequest.PageCount), problem.Errors.Keys);
    }

    [Fact]
    public async Task CancelledJob_IsNeverHandedToAPrinter()
    {
        using var client = factory.CreateClient();
        var printer = await RegisterPrinterAsync(client);
        var job = await SubmitJobAsync(client, printer.Id, "withdrawn.pdf", pages: 1);

        var cancelled = await client.PostAsJsonAsync(
            $"/api/v1/jobs/{job.Id}/cancel",
            new CancelJobRequest { Reason = "Sent to the wrong floor." },
            SpoolrApiFactory.Json);

        Assert.Equal(JobStatus.Cancelled, (await ReadAsync<JobResponse>(cancelled)).Status);

        AuthenticateAsDevice(client, printer);

        var claimed = await client.PostAsync("/api/v1/device/jobs/next", content: null);
        Assert.Equal(HttpStatusCode.NoContent, claimed.StatusCode);
    }

    [Fact]
    public async Task CancellingAFinishedJob_IsAConflict()
    {
        using var client = factory.CreateClient();
        var printer = await RegisterPrinterAsync(client);
        var job = await SubmitJobAsync(client, printer.Id, "done.pdf", pages: 1);

        AuthenticateAsDevice(client, printer);
        await client.PostAsync("/api/v1/device/jobs/next", content: null);
        await client.PostAsync($"/api/v1/device/jobs/{job.Id}/acknowledge", content: null);
        await client.PostAsJsonAsync(
            $"/api/v1/device/jobs/{job.Id}/result",
            new JobResultRequest { Succeeded = true },
            SpoolrApiFactory.Json);

        client.DefaultRequestHeaders.Authorization = null;

        var response = await client.PostAsJsonAsync(
            $"/api/v1/jobs/{job.Id}/cancel",
            new CancelJobRequest(),
            SpoolrApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_MovesThePrinterFromUnknownToOnline()
    {
        using var client = factory.CreateClient();
        var printer = await RegisterPrinterAsync(client);

        var before = await ReadAsync<PrinterResponse>(
            await client.GetAsync($"/api/v1/printers/{printer.Id}"));
        Assert.Equal(PrinterStatus.Unknown, before.Status);

        AuthenticateAsDevice(client, printer);

        var heartbeat = await client.PostAsJsonAsync(
            "/api/v1/device/heartbeat",
            new HeartbeatRequest { Status = PrinterStatus.Online },
            SpoolrApiFactory.Json);
        Assert.Equal(HttpStatusCode.NoContent, heartbeat.StatusCode);

        client.DefaultRequestHeaders.Authorization = null;

        var after = await ReadAsync<PrinterResponse>(
            await client.GetAsync($"/api/v1/printers/{printer.Id}"));

        Assert.Equal(PrinterStatus.Online, after.Status);
        Assert.NotNull(after.LastHeartbeatAt);
    }

    [Fact]
    public async Task ADeviceCannotReportItselfRetired()
    {
        using var client = factory.CreateClient();
        var printer = await RegisterPrinterAsync(client);

        AuthenticateAsDevice(client, printer);

        var response = await client.PostAsJsonAsync(
            "/api/v1/device/heartbeat",
            new HeartbeatRequest { Status = PrinterStatus.Retired },
            SpoolrApiFactory.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RegisteringADuplicateName_IsAConflict()
    {
        using var client = factory.CreateClient();
        var request = NewPrinterRequest();

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(
            "/api/v1/printers", request, SpoolrApiFactory.Json)).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(
            "/api/v1/printers", request, SpoolrApiFactory.Json)).StatusCode);
    }

    [Fact]
    public async Task Jobs_CanBeFilteredByPrinterAndStatus()
    {
        using var client = factory.CreateClient();
        var printer = await RegisterPrinterAsync(client);

        await SubmitJobAsync(client, printer.Id, "one.pdf", pages: 1);
        await SubmitJobAsync(client, printer.Id, "two.pdf", pages: 1);

        var page = await ReadAsync<PagedResponse<JobResponse>>(
            await client.GetAsync($"/api/v1/jobs?printerId={printer.Id}&status=Queued"));

        Assert.Equal(2, page.Total);
        Assert.All(page.Items, job => Assert.Equal(JobStatus.Queued, job.Status));
    }

    [Fact]
    public async Task Submit_RetriedWithTheSameIdempotencyKey_CreatesOneJob()
    {
        using var client = factory.CreateClient();
        var printer = await RegisterPrinterAsync(client);
        var request = NewSubmitRequest(printer.Id, "invoice.pdf");

        // The client timed out on the first call and does not know whether it landed.
        var first = await SubmitWithKeyAsync(client, request, "invoice-7781");
        var retry = await SubmitWithKeyAsync(client, request, "invoice-7781");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal("true", Assert.Single(retry.Headers.GetValues("Idempotent-Replayed")));

        var original = await ReadAsync<JobResponse>(first);
        var replayed = await ReadAsync<JobResponse>(retry);
        Assert.Equal(original.Id, replayed.Id);
        Assert.Equal(first.Headers.Location, retry.Headers.Location);

        var page = await ReadAsync<PagedResponse<JobResponse>>(
            await client.GetAsync($"/api/v1/jobs?printerId={printer.Id}"));

        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task Submit_ReusingAKeyForADifferentRequest_IsRejected()
    {
        using var client = factory.CreateClient();
        var printer = await RegisterPrinterAsync(client);

        await SubmitWithKeyAsync(client, NewSubmitRequest(printer.Id, "march.pdf"), "monthly-report");

        var reused = await SubmitWithKeyAsync(client, NewSubmitRequest(printer.Id, "april.pdf"), "monthly-report");

        // Answering with the March job would tell the caller April was queued when it was not.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
    }

    [Fact]
    public async Task Submit_Replay_ReturnsTheOriginalJobEvenAfterThePrinterIsRetired()
    {
        using var client = factory.CreateClient();
        var printer = await RegisterPrinterAsync(client);
        var request = NewSubmitRequest(printer.Id, "payslips.pdf");

        var original = await ReadAsync<JobResponse>(await SubmitWithKeyAsync(client, request, "payroll-sept"));

        await client.PostAsync($"/api/v1/printers/{printer.Id}/retire", content: null);

        // A fresh submission would now be refused. A retry is not a fresh submission: the
        // job already exists, and the caller is owed the answer the first request earned.
        var retry = await SubmitWithKeyAsync(client, request, "payroll-sept");

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(original.Id, (await ReadAsync<JobResponse>(retry)).Id);
    }

    [Theory]
    [InlineData("has spaces in it")]
    [InlineData("tab\there")]
    public async Task Submit_WithAMalformedIdempotencyKey_IsAValidationProblem(string key)
    {
        using var client = factory.CreateClient();
        var printer = await RegisterPrinterAsync(client);

        var response = await SubmitWithKeyAsync(client, NewSubmitRequest(printer.Id, "doc.pdf"), key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Submit_WithAnOverlongIdempotencyKey_IsAValidationProblem()
    {
        using var client = factory.CreateClient();
        var printer = await RegisterPrinterAsync(client);

        var response = await SubmitWithKeyAsync(
            client, NewSubmitRequest(printer.Id, "doc.pdf"), new string('k', 256));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static SubmitJobRequest NewSubmitRequest(Guid printerId, string documentName) => new()
    {
        PrinterId = printerId,
        DocumentName = documentName,
        PageCount = 4,
    };

    private static async Task<HttpResponseMessage> SubmitWithKeyAsync(
        HttpClient client,
        SubmitJobRequest request,
        string idempotencyKey)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/jobs")
        {
            Content = JsonContent.Create(request, options: SpoolrApiFactory.Json),
        };

        // Added without validation so the test can send keys the service must refuse.
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        return await client.SendAsync(message);
    }

    private static async Task<JobResponse> FailOnceAsync(HttpClient client, Guid jobId, string error)
    {
        await client.PostAsync("/api/v1/device/jobs/next", content: null);
        await client.PostAsync($"/api/v1/device/jobs/{jobId}/acknowledge", content: null);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/device/jobs/{jobId}/result",
            new JobResultRequest { Succeeded = false, Error = error },
            SpoolrApiFactory.Json);

        return await ReadAsync<JobResponse>(response);
    }

    private static RegisterPrinterRequest NewPrinterRequest(int maxPages = 500) => new()
    {
        Name = $"HQ-{Guid.NewGuid():N}",
        Location = "Hyderabad / Floor 3",
        Model = "Contoso LaserJet 9000",
        SupportsColor = true,
        SupportsDuplex = true,
        MaxPagesPerJob = maxPages,
    };

    private static async Task<RegisterPrinterResponse> RegisterPrinterAsync(
        HttpClient client,
        int maxPages = 500)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/printers", NewPrinterRequest(maxPages), SpoolrApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await ReadAsync<RegisterPrinterResponse>(response);
    }

    private static async Task<JobResponse> SubmitJobAsync(
        HttpClient client,
        Guid printerId,
        string documentName,
        int pages)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/jobs",
            new SubmitJobRequest
            {
                PrinterId = printerId,
                DocumentName = documentName,
                PageCount = pages,
            },
            SpoolrApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await ReadAsync<JobResponse>(response);
    }

    private static void AuthenticateAsDevice(HttpClient client, RegisterPrinterResponse printer) =>
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("DeviceKey", $"{printer.Id}:{printer.DeviceKey}");

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(SpoolrApiFactory.Json);

        Assert.NotNull(value);

        return value;
    }
}
