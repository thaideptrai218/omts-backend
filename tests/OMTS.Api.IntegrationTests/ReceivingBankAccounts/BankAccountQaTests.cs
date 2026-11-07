using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.ReceivingBankAccounts;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.ReceivingBankAccounts.ReceivingBankAccountTestKit;

namespace OMTS.Api.IntegrationTests.ReceivingBankAccounts;

/// <summary>QA of DEBT-01: the flow step by step, what each failure of the service leaves behind, the audit row, the logs.</summary>
public sealed class BankAccountQaTests
{
    private const string NewNumber = "QANEW0000099";
    private const string ProviderReason = "QA_REASON_7731";
    private const string ProviderText = "QA_PROVIDER_TEXT_SECRET";

    private static Func<ReceivingAccountToAccept, CancellationToken, Task<AccountAcceptanceResult>> Failing(string kind) => kind switch
    {
        "refused" => (_, _) => Task.FromResult(AccountAcceptanceResult.Rejected(ProviderReason)),
        "unavailable" => (_, _) => Task.FromResult(AccountAcceptanceResult.Unavailable(ProviderReason)),
        "unknown answer" => (_, _) => Task.FromResult(new AccountAcceptanceResult((AccountAcceptanceOutcome)99)),
        "throws at once" => (account, _) => throw new InvalidOperationException($"{ProviderText} {account.AccountNumber}"),
        "fails later" => async (account, _) =>
        {
            await Task.Yield();
            throw new HttpRequestException($"{ProviderText} {account.AccountNumber}");
        },
        "own timeout" => (_, _) => Task.FromException<AccountAcceptanceResult>(new TimeoutException(ProviderText)),
        "own cancel" => (_, _) => Task.FromException<AccountAcceptanceResult>(new TaskCanceledException(ProviderText)),
        "silent, honours the token" => async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return AccountAcceptanceResult.Accepted;
        },
        "silent, ignores the token" => (_, _) => new TaskCompletionSource<AccountAcceptanceResult>().Task,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [InlineData("refused", HttpStatusCode.Conflict)]
    [InlineData("unavailable", HttpStatusCode.ServiceUnavailable)]
    [InlineData("unknown answer", HttpStatusCode.ServiceUnavailable)]
    [InlineData("throws at once", HttpStatusCode.ServiceUnavailable)]
    [InlineData("fails later", HttpStatusCode.ServiceUnavailable)]
    [InlineData("own timeout", HttpStatusCode.ServiceUnavailable)]
    [InlineData("own cancel", HttpStatusCode.ServiceUnavailable)]
    [InlineData("silent, honours the token", HttpStatusCode.ServiceUnavailable)]
    [InlineData("silent, ignores the token", HttpStatusCode.ServiceUnavailable)]
    public async Task Every_failure_of_the_service_keeps_the_old_account_and_says_only_msg_debt_02(string kind, HttpStatusCode status)
    {
        var answer = (Func<ReceivingAccountToAccept, CancellationToken, Task<AccountAcceptanceResult>>)((_, _) => Task.FromResult(AccountAcceptanceResult.Accepted));
        var service = new ScriptedPaymentService((account, token) => answer(account, token));
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service, timeoutSeconds: 1);
        var owner = await OwnerAsync(factory, api);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner)))).StatusCode);
        var before = (await AccountAsync(owner)).GetRawText();
        var version = await RowVersionAsync(owner);
        answer = Failing(kind);

        foreach (var confirm in new[] { false, true })
        {
            var response = await owner.PutAsJsonAsync(Route, Body(version, Bidv, NewNumber, "HTX KHAC", confirm));
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(status, response.StatusCode);
            Assert.Equal("MSG-DEBT-02", (await ProblemAsync(response)).GetProperty("code").GetString());
            Assert.DoesNotContain(ProviderReason, body);
            Assert.DoesNotContain(ProviderText, body);
        }

        Assert.Equal(before, (await AccountAsync(owner)).GetRawText());
        Assert.Equal(version, await RowVersionAsync(owner));
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == AuditAction)));
        var logs = string.Join('\n', factory.Logs.Lines);
        Assert.DoesNotContain(NewNumber, logs);
        Assert.DoesNotContain(FirstNumber, logs);
        Assert.DoesNotContain(ProviderText, logs);
    }

    [Fact]
    public async Task A_slow_answer_inside_the_limit_is_saved_with_the_time_of_the_answer()
    {
        var service = new ScriptedPaymentService(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            return AccountAcceptanceResult.Accepted;
        });
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service, timeoutSeconds: 30);
        var owner = await OwnerAsync(factory, api);
        var version = await RowVersionAsync(owner);
        var start = DateTimeOffset.UtcNow;

        var saved = await owner.PutAsJsonAsync(Route, Body(version));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.True((await AccountAsync(owner)).GetProperty("acceptedAt").GetDateTimeOffset() >= start.AddSeconds(0.9));
    }

    [Fact]
    public async Task An_acceptance_after_the_limit_saves_nothing_and_a_new_try_works()
    {
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        var service = new ScriptedPaymentService(async (_, _) =>
        {
            if (first)
            {
                first = false;
                await late.Task;
            }

            return AccountAcceptanceResult.Accepted;
        });
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service, timeoutSeconds: 1);
        var owner = await OwnerAsync(factory, api);
        var version = await RowVersionAsync(owner);

        var timedOut = await owner.PutAsJsonAsync(Route, Body(version));
        late.SetResult();
        await Task.Yield();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, timedOut.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await AccountAsync(owner)).ValueKind);
        Assert.Equal(0, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == AuditAction)));
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(Route, Body(version))).StatusCode);
    }

    [Fact]
    public async Task A_cancelled_request_saves_nothing_and_a_late_acceptance_changes_nothing()
    {
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<AccountAcceptanceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ScriptedPaymentService((_, _) =>
        {
            asked.TrySetResult();
            return answer.Task;
        });
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service);
        var owner = await OwnerAsync(factory, api);
        using var abort = new CancellationTokenSource();

        var pending = owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), number: NewNumber), abort.Token);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        answer.SetResult(AccountAcceptanceResult.Accepted);
        await Task.Delay(200);
        Assert.Equal(JsonValueKind.Null, (await AccountAsync(owner)).ValueKind);
        Assert.Equal(0, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == AuditAction)));
        Assert.DoesNotContain(NewNumber, string.Join('\n', factory.Logs.Lines));
    }

    // PM call, not a defect: confirmAccount is the only proof of step 6, as a client flag cannot prove that a person saw the
    // question. This records it: the old codes were never told, and the service is still asked.
    [Fact]
    public async Task PM_call_a_client_that_sends_confirm_first_saves_without_the_question()
    {
        var service = ScriptedPaymentService.Answering(AccountAcceptanceResult.Accepted);
        using var factory = new AuthApiFactory();
        await using var api = WithServiceAndCount(factory, service, 3);
        var owner = await OwnerAsync(factory, api);
        await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner)));

        var skipped = await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), Bidv, SecondNumber));

        Assert.Equal(HttpStatusCode.OK, skipped.StatusCode);
        Assert.Equal(2, service.Calls);
        Assert.Equal(SecondNumber, (await AccountAsync(owner)).GetProperty("accountNumber").GetString());
    }

    [Fact]
    public async Task A_confirmation_of_another_account_than_the_one_asked_about_saves_only_what_the_service_accepted()
    {
        var asked = new List<string>();
        var service = new ScriptedPaymentService((account, _) =>
        {
            asked.Add(account.AccountNumber);
            return Task.FromResult(account.AccountNumber == SecondNumber ? AccountAcceptanceResult.Rejected("NO") : AccountAcceptanceResult.Accepted);
        });
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service);
        var owner = await OwnerAsync(factory, api);
        var version = await RowVersionAsync(owner);

        Assert.Equal("MSG-DEBT-01", await (await owner.PutAsJsonAsync(Route, Body(version, confirm: false))).ErrorCodeAsync());
        Assert.Equal("MSG-DEBT-02", await (await owner.PutAsJsonAsync(Route, Body(version, number: SecondNumber))).ErrorCodeAsync());

        Assert.Equal([FirstNumber, SecondNumber], asked);
        Assert.Equal(JsonValueKind.Null, (await AccountAsync(owner)).ValueKind);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[true]")]
    public async Task A_forged_confirm_value_is_refused_before_the_service(string confirm)
    {
        var service = ScriptedPaymentService.Answering(AccountAcceptanceResult.Accepted);
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service);
        var owner = await OwnerAsync(factory, api);
        var json = $"{{\"bankBin\":\"{Vietcombank}\",\"accountNumber\":\"{FirstNumber}\",\"accountHolderName\":\"HTX\",\"rowVersion\":\"{await RowVersionAsync(owner)}\",\"confirmAccount\":{confirm}}}";

        var response = await owner.PutAsync(Route, new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, service.Calls);
        Assert.Equal(JsonValueKind.Null, (await AccountAsync(owner)).ValueKind);
    }

    [Fact]
    public async Task The_audit_row_names_the_owner_and_only_a_save_writes_one()
    {
        using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        var version = await RowVersionAsync(owner);

        await owner.PutAsJsonAsync(Route, Body(version, confirm: false));
        await owner.PutAsJsonAsync(Route, Body(version, number: RefusedByFake));
        Assert.Equal(0, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == AuditAction)));

        await owner.PutAsJsonAsync(Route, Body(version));

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == AuditAction));
        var ownerId = await factory.QueryAsync(db => db.Accounts.Where(a => a.SubjectType == SubjectType.CooperativeOwner).Select(a => a.Id).SingleAsync());
        Assert.Equal(ownerId, audit.ActorAccountId);
        var after = JsonDocument.Parse(audit.After!).RootElement;
        Assert.Equal("HTX TRUNG NAM", after.GetProperty("AccountHolderName").GetString());
        Assert.Equal((await AccountAsync(owner)).GetProperty("acceptedAt").GetDateTimeOffset(), after.GetProperty("AcceptedAt").GetDateTimeOffset());
    }

    // PM call, not a defect: GBR-02 asks for the values before and after, and the number is masked as for an employee. Two
    // numbers with the same last four digits then show as no change. This records it.
    [Fact]
    public async Task PM_call_two_numbers_with_the_same_ending_look_the_same_in_the_audit_row()
    {
        using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), number: "TESTAAAA0001"));
        await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), number: "TESTBBBB0001"));

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == AuditAction && e.Before != null));

        Assert.Equal(
            JsonDocument.Parse(audit.Before!).RootElement.GetProperty("AccountNumber").GetString(),
            JsonDocument.Parse(audit.After!).RootElement.GetProperty("AccountNumber").GetString());
    }

    [Theory]
    [InlineData("T001")]
    [InlineData("T1")]
    public async Task L2_a_short_account_number_is_masked_in_the_audit_row(string number)
    {
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, ScriptedPaymentService.Answering(AccountAcceptanceResult.Accepted));
        var owner = await OwnerAsync(factory, api);

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), number: number))).StatusCode);

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == AuditAction));
        Assert.DoesNotContain($"\"AccountNumber\":\"{number}\"", audit.After);
    }

    [Fact]
    public async Task L2_a_short_account_number_is_masked_in_the_log()
    {
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, ScriptedPaymentService.Answering(AccountAcceptanceResult.Rejected("NO")));
        var owner = await OwnerAsync(factory, api);

        await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), number: "T002"));

        Assert.DoesNotContain("account T002", string.Join('\n', factory.Logs.Lines));
    }

    // On the test server this depends on where the abort runs, so the proof is without HTTP and on a real socket.
    // Without HTTP, off the synchronisation context of the test runner, as a request runs on a server.
    [Fact]
    public async Task M2_the_call_to_the_service_is_told_to_stop_when_the_caller_cancels()
    {
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ScriptedPaymentService(async (_, token) =>
        {
            using var registration = token.Register(() => stopped.TrySetResult());
            asked.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return AccountAcceptanceResult.Accepted;
        });
        var acceptance = new ReceivingAccountAcceptance(
            service,
            Options.Create(new PaymentNotificationOptions { TimeoutSeconds = 1 }),
            TimeProvider.System,
            NullLogger<ReceivingAccountAcceptance>.Instance);
        using var abort = new CancellationTokenSource();

        var pending = Task.Run(() => acceptance.AskAsync(new ReceivingAccountToAccept(Bidv, "BIDV", FirstNumber, "HTX"), abort.Token));
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // And on a real Kestrel socket, where the client closes the connection.
    [Fact]
    public async Task M2_the_call_to_the_service_is_told_to_stop_when_the_client_goes_away_from_kestrel()
    {
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ScriptedPaymentService(async (_, token) =>
        {
            using var registration = token.Register(() => stopped.TrySetResult());
            asked.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return AccountAcceptanceResult.Accepted;
        });
        await using var api = new KestrelPortFactory(service);
        await api.EnsureCooperativeAsync();
        var password = AcsTestKit.NewPassword();
        var account = await api.AddAccountAsync(AcsTestKit.NewCode(), password);
        using var client = api.CreateKestrelClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await AcsTestKit.LoginTokensAsync(client, account.Username, password)).GetProperty("accessToken").GetString());
        var version = await RowVersionAsync(client);
        using var abort = new CancellationTokenSource();

        var pending = client.PutAsJsonAsync(Route, Body(version), abort.Token);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // The token's timer and WaitAsync's timer are due at once. When WaitAsync goes first, the method leaves and disposes the
    // token before its timer fires, so that call is never told. Off the runner's context, as a request runs on a server.
    // "Told" is read from the token the service holds. A callback registered on it is not a sound counter: the cancel of
    // Task.Delay can resume the service inline, whose using then removes that callback before the cancel reaches it (39 of
    // 40 once on CI, with every token cancelled).
    [Fact]
    public async Task M2_the_call_to_the_service_is_told_to_stop_at_the_time_limit_each_time()
    {
        const int calls = 40;
        var tokens = new System.Collections.Concurrent.ConcurrentBag<CancellationToken>();
        var service = new ScriptedPaymentService(async (_, token) =>
        {
            tokens.Add(token);
            await Task.Delay(Timeout.Infinite, token);
            return AccountAcceptanceResult.Accepted;
        });
        var acceptance = new ReceivingAccountAcceptance(
            service,
            Options.Create(new PaymentNotificationOptions { TimeoutSeconds = 1 }),
            TimeProvider.System,
            NullLogger<ReceivingAccountAcceptance>.Instance);
        var account = new ReceivingAccountToAccept(Bidv, "BIDV", FirstNumber, "HTX");

        var answers = await Task.WhenAll(Enumerable.Range(0, calls).Select(_ => Task.Run(() => acceptance.AskAsync(account, CancellationToken.None))));
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.All(answers, a => Assert.Equal(AccountAcceptanceOutcome.Unavailable, a.Outcome));
        Assert.Equal(calls, tokens.Count);
        Assert.All(tokens, t => Assert.True(t.IsCancellationRequested, "the service was not told to stop at the time limit"));
    }

    // Observation, no defect: the limit covers the task the provider returns. An adapter that blocks before its first await
    // is not stopped, and a late Accepted is then saved. Any adapter on HttpClient returns at once; a real one must too.
    [Fact]
    public async Task Observation_a_provider_that_blocks_before_returning_its_task_is_outside_the_limit()
    {
        var service = new ScriptedPaymentService((_, _) =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(2.5));
            return Task.FromResult(AccountAcceptanceResult.Accepted);
        });
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service, timeoutSeconds: 1);
        var owner = await OwnerAsync(factory, api);

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner)))).StatusCode);
    }

    // Observation, no defect: Reason is documented as a technical code that never holds the number, and the adapter that
    // breaks this is not written yet. A real adapter must not pass the provider's own text through.
    [Fact]
    public async Task Observation_a_reason_from_the_provider_reaches_the_log_as_it_is()
    {
        var service = new ScriptedPaymentService((account, _) => Task.FromResult(AccountAcceptanceResult.Rejected($"closed {account.AccountNumber}")));
        using var factory = new AuthApiFactory();
        await using var api = WithService(factory, service);
        var owner = await OwnerAsync(factory, api);

        await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner), number: NewNumber));

        Assert.Contains(NewNumber, string.Join('\n', factory.Logs.Lines));
    }

    private static WebApplicationFactory<Program> WithServiceAndCount(AuthApiFactory factory, IPaymentNotificationService service, int count) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton(service);
            services.AddSingleton<ISentInvoiceQrCodeQuery>(new FixedCount(count));
        }));

    /// <summary>The API on a real socket with a scripted service; the kit's Kestrel host takes no extra services.</summary>
    private sealed class KestrelPortFactory(IPaymentNotificationService service) : AuthApiFactory
    {
        private IHost? _kestrel;

        public HttpClient CreateKestrelClient()
        {
            _ = Services;
            return new HttpClient { BaseAddress = new Uri(_kestrel!.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()) };
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Payments:TimeoutSeconds", "1");
            builder.ConfigureTestServices(services => services.AddSingleton(service));
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var testHost = builder.Build();
            builder.ConfigureWebHost(web => web.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)));
            _kestrel = builder.Build();
            KestrelApiFactory.StartAndWait(_kestrel);
            KestrelApiFactory.StartAndWait(testHost);
            return testHost;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _kestrel?.StopAsync().GetAwaiter().GetResult();
                _kestrel?.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FixedCount(int count) : ISentInvoiceQrCodeQuery
    {
        public Task<int> CountUnpaidInvoicesWithSentCodesAsync(CancellationToken cancellationToken) => Task.FromResult(count);
    }
}
