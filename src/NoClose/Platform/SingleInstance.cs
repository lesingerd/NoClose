using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoClose.Platform;

public sealed record PipeRequest(string Verb, string[] Args);

public sealed record PipeResponse(bool Ok, string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PipeRequest))]
[JsonSerializable(typeof(PipeResponse))]
internal sealed partial class PipeJsonContext : JsonSerializerContext
{
}

/// <summary>Per-session single instance mutex.</summary>
internal sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\NoClose.SingleInstance.{6D1F1D7B-3C2B-4C5E-9E0A-2B1E7C9A4F10}";

    private Mutex? _mutex;
    private bool _owned;

    public bool IsOwner => _owned;

    /// <summary>True if this process is now the first (owning) instance. A mutex we may not open counts as "another instance exists".</summary>
    public bool TryAcquire()
    {
        if (_owned) return true;
        try
        {
            if (_mutex == null)
            {
                // Explicit DACL: only the current user (the elevated copy of the same user included) may open it.
                using var id = WindowsIdentity.GetCurrent();
                var security = new MutexSecurity();
                security.AddAccessRule(new MutexAccessRule(id.User!, MutexRights.FullControl, AccessControlType.Allow));
                _mutex = MutexAcl.Create(initiallyOwned: false, MutexName, out _, security);
            }
            _owned = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _owned = true; // previous owner died; we now own it
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists but belongs to a principal we cannot open it as: treat it as "another instance owns this session".
            _owned = false;
        }
        return _owned;
    }

    public void Release()
    {
        if (_owned)
        {
            try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
            _owned = false;
        }
    }

    public void Dispose()
    {
        Release();
        _mutex?.Dispose();
        _mutex = null;
    }
}

internal static class PipeNames
{
    public static string Current()
    {
        using var id = WindowsIdentity.GetCurrent();
        return $"NoClose.Cmd.{System.Diagnostics.Process.GetCurrentProcess().SessionId}.{id.User?.Value}";
    }
}

/// <summary>
/// One line of JSON request, one line of JSON response, per connection. Connections are served concurrently and every one has a
/// timeout, so a client that connects and never writes cannot block later commands.
/// </summary>
internal sealed class PipeServer : IDisposable
{
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<PipeRequest, Task<PipeResponse>> _handler;
    private readonly Action<string> _log;
    private readonly string _name;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public PipeServer(Func<PipeRequest, Task<PipeResponse>> handler, Action<string> log, string? pipeName = null)
    {
        _handler = handler;
        _log = log;
        _name = pipeName ?? PipeNames.Current();
    }

    public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

    /// <summary>
    /// The DACL grants the current user (any integrity level of that user) full control. No explicit mandatory label is added: an unlabeled
    /// object has an implicit Medium / no-write-up label, so an elevated (high) instance still accepts the unelevated (medium) command line
    /// of the same user, and setting a SACL (including a label through PipeSecurity) fails without SeSecurityPrivilege.
    /// </summary>
    internal static NamedPipeServerStream Create(string name)
    {
        using var id = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(id.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = Create(_name);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var connection = server;
                server = null; // ownership moves to the connection task
                _ = Task.Run(() => ServeAsync(connection, ct), ct);
            }
            catch (OperationCanceledException) { server?.Dispose(); break; }
            catch (Exception ex)
            {
                server?.Dispose();
                _log("Pipe server error: " + ex.Message);
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        try
        {
            await using var _ = server;
            using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };

            PipeResponse response;
            try
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(ReadTimeout);
                var line = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
                var req = line == null ? null : JsonSerializer.Deserialize(line, PipeJsonContext.Default.PipeRequest);
                if (req == null) response = new PipeResponse(false, "Empty request.");
                else
                {
                    var work = _handler(req);
                    var finished = await Task.WhenAny(work, Task.Delay(HandlerTimeout, ct)).ConfigureAwait(false);
                    response = finished == work ? await work.ConfigureAwait(false) : new PipeResponse(false, "The running instance did not answer in time.");
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                response = new PipeResponse(false, "No request received.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                response = new PipeResponse(false, "Request failed: " + ex.Message);
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, PipeJsonContext.Default.PipeResponse).AsMemory(), ct).ConfigureAwait(false);
            try { server.WaitForPipeDrain(); } catch { }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log("Pipe connection error: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(500); } catch { }
        _cts.Dispose();
    }
}

internal static class PipeClient
{
    /// <summary>How long the client waits for the answer once connected (the instance may be running a stale journal restore).</summary>
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Returns null if the running instance could not be reached; sets <paramref name="accessDenied"/> on UnauthorizedAccess.</summary>
    public static PipeResponse? Send(PipeRequest request, out bool accessDenied, int timeoutMs = 3000, string? pipeName = null, TimeSpan? responseTimeout = null)
    {
        accessDenied = false;
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName ?? PipeNames.Current(), PipeDirection.InOut, PipeOptions.None);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            writer.WriteLine(JsonSerializer.Serialize(request, PipeJsonContext.Default.PipeRequest));
            // A hung instance must not hang the command line forever: bound the read.
            var read = Task.Run(() => reader.ReadLine());
            if (!read.Wait(responseTimeout ?? ResponseTimeout)) return null;
            var line = read.Result;
            return line == null ? null : JsonSerializer.Deserialize(line, PipeJsonContext.Default.PipeResponse);
        }
        catch (UnauthorizedAccessException)
        {
            accessDenied = true;
            return null;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or JsonException or AggregateException)
        {
            return null;
        }
    }
}
