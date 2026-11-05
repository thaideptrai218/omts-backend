using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Infrastructure.Bootstrap;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>
/// The real <c>dotnet OMTS.Api.dll</c>, not a test host: a test host loads appsettings.Development.json whatever the
/// environment, so what a server really reads can only be shown by the process. Its folder stands in for the image
/// (/app): the two committed settings files beside the program, plus whatever a test adds. Nothing of the machine
/// comes in: no environment name, no user secrets of the developer.
/// </summary>
public sealed class StagingDemoQaProcessKit : IDisposable
{
    private const string UserSecretsId = "7d411920-193a-4e1b-83ec-b463c8e3492b";

    private static readonly string[] MachineVariables =
    [
        "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT", "ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS",
        "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", "DOTNET_STARTUP_HOOKS",
    ];

    public StagingDemoQaProcessKit()
    {
        Root = Path.Combine(Path.GetTempPath(), $"omts-qa-216-{Guid.NewGuid():N}");
        ImageFolder = Directory.CreateDirectory(Path.Combine(Root, "app")).FullName;
        StorageRoot = Path.Combine(Root, "files");
        AppData = Directory.CreateDirectory(Path.Combine(Root, "appdata")).FullName;
        foreach (var file in new[] { "appsettings.json", "appsettings.Development.json" })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(ImageFolder, file));
        }
    }

    public string Root { get; }
    public string ImageFolder { get; }
    public string StorageRoot { get; }
    public string AppData { get; }

    /// <summary>A database name on the test server; the caller drops it, a refused run must not even create it.</summary>
    public static string NewDatabaseConnectionString()
    {
        var server = Environment.GetEnvironmentVariable(SqlServerFactAttribute.VariableName)!;
        return new SqlConnectionStringBuilder(server) { InitialCatalog = $"omts_qa216_{Guid.NewGuid():N}", ConnectTimeout = 10 }.ConnectionString;
    }

    public static async Task<bool> DatabaseExistsAsync(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var name = builder.InitialCatalog;
        builder.InitialCatalog = "master";
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_ID(@name)";
        command.Parameters.AddWithValue("@name", name);
        return await command.ExecuteScalarAsync() is not (null or DBNull);
    }

    public static async Task DropDatabaseAsync(string connectionString)
    {
        SqlConnection.ClearAllPools();
        var builder = new SqlConnectionStringBuilder(connectionString);
        var name = builder.InitialCatalog;
        builder.InitialCatalog = "master";
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>What README "Demo on a shared server" step 1 asks for, without the environment name and the demo keys.</summary>
    public Dictionary<string, string?> ServerSettings(string connectionString) => new()
    {
        ["ConnectionStrings__OmtsDb"] = connectionString,
        ["Jwt__SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
        ["Storage__Local__SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
        ["Storage__Local__RootPath"] = StorageRoot,
        [EnvKey(BootstrapSettings.CooperativeFullNameKey)] = "HTX QA",
        [EnvKey(BootstrapSettings.OwnerDisplayNameKey)] = "Owner QA",
        [EnvKey(BootstrapSettings.OwnerUsernameKey)] = "owner",
        [EnvKey(BootstrapSettings.OwnerPhoneNumberKey)] = "0900000001",
        [EnvKey(BootstrapSettings.OwnerTemporaryPasswordKey)] = AcsTestKit.NewPassword(),
    };

    public static string EnvKey(string configurationKey) => configurationKey.Replace(":", "__");

    public void WriteImageFile(string name, string content) => File.WriteAllText(Path.Combine(ImageFolder, name), content);

    /// <summary>The file <c>dotnet user-secrets</c> would write for this project, under this kit's own profile folder.</summary>
    public void WriteUserSecrets(string json)
    {
        var folder = Directory.CreateDirectory(Path.Combine(AppData, "Microsoft", "UserSecrets", UserSecretsId));
        File.WriteAllText(Path.Combine(folder.FullName, "secrets.json"), json);
    }

    public sealed record ProcessRun(int ExitCode, string Output);

    /// <summary>Runs the program to its end. Null values remove a variable.</summary>
    public async Task<ProcessRun> RunAsync(IReadOnlyDictionary<string, string?> variables, params string[] arguments)
    {
        using var process = Start(variables, arguments, out var output);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Program did not end: {string.Join(' ', arguments)}");
        }

        // The asynchronous readers have seen everything only after this overload returns.
        process.WaitForExit();
        lock (output)
        {
            return new ProcessRun(process.ExitCode, output.ToString());
        }
    }

    /// <summary>Starts the web host and waits until it answers, or ends.</summary>
    public async Task<RunningApi> StartApiAsync(IReadOnlyDictionary<string, string?> variables)
    {
        var port = FreePort();
        var withUrl = new Dictionary<string, string?>(variables) { ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}" };
        var process = Start(withUrl, [], out var output);
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var api = new RunningApi(process, client, output);
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline && !process.HasExited)
        {
            try
            {
                if ((await client.GetAsync("/health")).StatusCode == HttpStatusCode.OK)
                {
                    return api;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(200);
        }

        return api;
    }

    private Process Start(IReadOnlyDictionary<string, string?> variables, string[] arguments, out StringBuilder output)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = ImageFolder,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "OMTS.Api.dll"));
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("Demo", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            info.Environment.Remove(key);
        }

        foreach (var name in MachineVariables)
        {
            info.Environment.Remove(name);
        }

        // User secrets live under %APPDATA% on Windows and ~/.microsoft on Linux.
        info.Environment["APPDATA"] = AppData;
        foreach (var (key, value) in variables)
        {
            if (value is null)
            {
                info.Environment.Remove(key);
            }
            else
            {
                info.Environment[key] = value;
            }
        }

        var sink = new StringBuilder();
        var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => Append(sink, e.Data);
        process.ErrorDataReceived += (_, e) => Append(sink, e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        output = sink;
        return process;
    }

    private static void Append(StringBuilder sink, string? line)
    {
        if (line is not null)
        {
            lock (sink)
            {
                sink.AppendLine(line);
            }
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    public sealed class RunningApi(Process process, HttpClient client, StringBuilder output) : IDisposable
    {
        public HttpClient Client { get; } = client;

        public bool HasExited => process.HasExited;

        public int ExitCode => process.ExitCode;

        public string Output
        {
            get
            {
                lock (output)
                {
                    return output.ToString();
                }
            }
        }

        public void Dispose()
        {
            Client.Dispose();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            process.Dispose();
        }
    }
}
